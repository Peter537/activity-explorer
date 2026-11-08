using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Storage;

internal static class PeriodRecordProjectionStore
{
    internal const int FormatVersion = 1;

    internal sealed record Row(Guid OwnerId, SportKind Sport, long StartTicks, Guid ActivityId, string Title,
        double DistanceMeters, double MovingTimeSeconds, double ElevationGainMeters, bool IsIndoor, bool HasStream,
        SportKind? SummarySport, int? ComputationVersion, byte[]? ValuesBlob);

    internal static async Task<bool> ExistsAsync(ExplorerDbContext db, CancellationToken cancellationToken) =>
        await db.Database.SqlQueryRaw<long>("""
            SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'ActivityPeriodRecordRows') AS "Value"
            """).SingleAsync(cancellationToken) != 0;

    internal static IQueryable<Row> Read(ExplorerDbContext db, ResolvedOwnerPeriod period, SportKind sport, RecordScope scope)
    {
        var rows = db.Database.SqlQueryRaw<Row>("""
            SELECT "OwnerId", "Sport", "StartTimeUtc" AS "StartTicks", "ActivityId", "Title",
                "DistanceMeters", "MovingTimeSeconds", "ElevationGainMeters", "IsIndoor", "HasStream",
                "SummarySport", "ComputationVersion", "ValuesBlob"
            FROM "ActivityPeriodRecordRows"
            """).Where(row => row.OwnerId == period.OwnerId && row.Sport == sport);
        if (period.FromUtc is { } from)
        {
            var fromTicks = from.UtcTicks;
            rows = rows.Where(row => row.StartTicks >= fromTicks);
        }
        if (period.ToUtc is { } to)
        {
            var toTicks = to.UtcTicks;
            rows = rows.Where(row => row.StartTicks < toTicks);
        }
        if (scope != RecordScope.All)
        {
            var indoor = scope == RecordScope.Indoor;
            rows = rows.Where(row => row.IsIndoor == indoor);
        }
        return rows.OrderBy(row => row.StartTicks).ThenBy(row => row.ActivityId);
    }

    internal static async Task EnsureAsync(ExplorerDbContext db, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await ExistsAsync(db, cancellationToken))
        {
            // Build the derived table atomically, in the same order as its owner/sport/date range reads.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "ActivityPeriodRecordRows" (
                    "OwnerId" TEXT NOT NULL, "Sport" INTEGER NOT NULL, "StartTimeUtc" INTEGER NOT NULL,
                    "ActivityId" TEXT NOT NULL UNIQUE REFERENCES "Activities" ("Id") ON DELETE CASCADE,
                    "Title" TEXT NOT NULL, "DistanceMeters" REAL NOT NULL, "MovingTimeSeconds" REAL NOT NULL,
                    "ElevationGainMeters" REAL NOT NULL, "IsIndoor" INTEGER NOT NULL, "HasStream" INTEGER NOT NULL,
                    "SummarySport" INTEGER NULL, "ComputationVersion" INTEGER NULL, "ValuesBlob" BLOB NULL,
                    PRIMARY KEY ("OwnerId", "Sport", "StartTimeUtc", "ActivityId")
                ) WITHOUT ROWID;
                CREATE VIEW "ActivityPeriodRecordSource" AS
                    SELECT a."OwnerId", a."Sport", a."StartTimeUtc", a."Id" AS "ActivityId", a."Title",
                        a."DistanceMeters", a."MovingTimeSeconds", a."ElevationGainMeters", a."IsIndoor",
                        s."ActivityId" IS NOT NULL AS "HasStream", e."Sport" AS "SummarySport",
                        e."ComputationVersion", e."ValuesBlob"
                    FROM "Activities" AS a
                    LEFT JOIN "ActivityStreams" AS s ON s."ActivityId" = a."Id"
                    LEFT JOIN "ActivityRecordEffortSummaries" AS e ON e."ActivityId" = s."ActivityId";
                INSERT INTO "ActivityPeriodRecordRows"
                    SELECT * FROM "ActivityPeriodRecordSource" ORDER BY "OwnerId", "Sport", "StartTimeUtc", "ActivityId";
                CREATE TRIGGER "TR_PeriodRecordRows_ActivityInsert"
                AFTER INSERT ON "Activities" BEGIN
                    INSERT INTO "ActivityPeriodRecordRows" SELECT * FROM "ActivityPeriodRecordSource" WHERE "ActivityId" = NEW."Id";
                END;
                CREATE TRIGGER "TR_PeriodRecordRows_ActivityUpdate"
                AFTER UPDATE OF "Id", "OwnerId", "Sport", "StartTimeUtc", "Title", "DistanceMeters", "MovingTimeSeconds", "ElevationGainMeters", "IsIndoor"
                ON "Activities" BEGIN
                    DELETE FROM "ActivityPeriodRecordRows" WHERE "ActivityId" IN (OLD."Id", NEW."Id");
                    INSERT INTO "ActivityPeriodRecordRows" SELECT * FROM "ActivityPeriodRecordSource" WHERE "ActivityId" = NEW."Id";
                END;
                CREATE TRIGGER "TR_PeriodRecordRows_ActivityDelete"
                AFTER DELETE ON "Activities" BEGIN
                    DELETE FROM "ActivityPeriodRecordRows" WHERE "ActivityId" = OLD."Id";
                END;
                CREATE TRIGGER "TR_PeriodRecordRows_StreamInsert"
                AFTER INSERT ON "ActivityStreams" BEGIN
                    DELETE FROM "ActivityPeriodRecordRows" WHERE "ActivityId" = NEW."ActivityId";
                    INSERT INTO "ActivityPeriodRecordRows" SELECT * FROM "ActivityPeriodRecordSource" WHERE "ActivityId" = NEW."ActivityId";
                END;
                CREATE TRIGGER "TR_PeriodRecordRows_StreamUpdate"
                AFTER UPDATE OF "ActivityId", "CompressedPayload", "SchemaVersion" ON "ActivityStreams" BEGIN
                    DELETE FROM "ActivityPeriodRecordRows" WHERE "ActivityId" IN (OLD."ActivityId", NEW."ActivityId");
                    INSERT INTO "ActivityPeriodRecordRows" SELECT * FROM "ActivityPeriodRecordSource" WHERE "ActivityId" IN (OLD."ActivityId", NEW."ActivityId");
                END;
                CREATE TRIGGER "TR_PeriodRecordRows_StreamDelete"
                AFTER DELETE ON "ActivityStreams" BEGIN
                    DELETE FROM "ActivityPeriodRecordRows" WHERE "ActivityId" = OLD."ActivityId";
                    INSERT INTO "ActivityPeriodRecordRows" SELECT * FROM "ActivityPeriodRecordSource" WHERE "ActivityId" = OLD."ActivityId";
                END;
                CREATE TRIGGER "TR_PeriodRecordRows_SummaryInsert"
                AFTER INSERT ON "ActivityRecordEffortSummaries" BEGIN
                    DELETE FROM "ActivityPeriodRecordRows" WHERE "ActivityId" = NEW."ActivityId";
                    INSERT INTO "ActivityPeriodRecordRows" SELECT * FROM "ActivityPeriodRecordSource" WHERE "ActivityId" = NEW."ActivityId";
                END;
                CREATE TRIGGER "TR_PeriodRecordRows_SummaryUpdate"
                AFTER UPDATE ON "ActivityRecordEffortSummaries" BEGIN
                    DELETE FROM "ActivityPeriodRecordRows" WHERE "ActivityId" IN (OLD."ActivityId", NEW."ActivityId");
                    INSERT INTO "ActivityPeriodRecordRows" SELECT * FROM "ActivityPeriodRecordSource" WHERE "ActivityId" IN (OLD."ActivityId", NEW."ActivityId");
                END;
                CREATE TRIGGER "TR_PeriodRecordRows_SummaryDelete"
                AFTER DELETE ON "ActivityRecordEffortSummaries" BEGIN
                    DELETE FROM "ActivityPeriodRecordRows" WHERE "ActivityId" = OLD."ActivityId";
                    INSERT INTO "ActivityPeriodRecordRows" SELECT * FROM "ActivityPeriodRecordSource" WHERE "ActivityId" = OLD."ActivityId";
                END;
                """, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
