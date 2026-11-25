using ActivityExplorer.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Storage;

internal sealed class OwnerProjectionVersion
{
    public Guid OwnerId { get; set; }
    public long InputGeneration { get; set; }
    public long RecordsGeneration { get; set; }
    public int RecordsCount { get; set; }
}

internal sealed class SegmentProjectionVersion
{
    public Guid SegmentId { get; set; }
    public long DefinitionGeneration { get; set; }
    public long PublishedDefinitionGeneration { get; set; }
    public long PublishedActivityGeneration { get; set; }
}

internal static class ProjectionVersions
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        var owner = modelBuilder.Entity<OwnerProjectionVersion>();
        owner.HasKey(value => value.OwnerId);
        owner.HasOne<OwnerProfile>().WithOne().HasForeignKey<OwnerProjectionVersion>(value => value.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);
        var segment = modelBuilder.Entity<SegmentProjectionVersion>();
        segment.HasKey(value => value.SegmentId);
        segment.HasOne<Segment>().WithOne().HasForeignKey<SegmentProjectionVersion>(value => value.SegmentId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    internal static async Task EnsureAsync(ExplorerDbContext db, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "OwnerProjectionVersions" (
                "OwnerId" TEXT NOT NULL PRIMARY KEY REFERENCES "Owners" ("Id") ON DELETE CASCADE,
                "InputGeneration" INTEGER NOT NULL,
                "RecordsGeneration" INTEGER NOT NULL,
                "RecordsCount" INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS "SegmentProjectionVersions" (
                "SegmentId" TEXT NOT NULL PRIMARY KEY REFERENCES "Segments" ("Id") ON DELETE CASCADE,
                "DefinitionGeneration" INTEGER NOT NULL,
                "PublishedDefinitionGeneration" INTEGER NOT NULL,
                "PublishedActivityGeneration" INTEGER NOT NULL);

            INSERT OR IGNORE INTO "OwnerProjectionVersions" SELECT "Id", 1, 0, 0 FROM "Owners" AS owner
                WHERE EXISTS (SELECT 1 FROM "Activities" WHERE "OwnerId" = owner."Id")
                   OR EXISTS (SELECT 1 FROM "StatisticSnapshots" WHERE "OwnerId" = owner."Id");
            INSERT OR IGNORE INTO "SegmentProjectionVersions" SELECT "Id", 1, 0, 0 FROM "Segments";

            CREATE TRIGGER IF NOT EXISTS "Projection_SegmentInsert" AFTER INSERT ON "Segments" BEGIN
                INSERT INTO "SegmentProjectionVersions" VALUES (NEW."Id", 1, 0, 0);
            END;
            """, cancellationToken);

        string[] activityInputs = ["OwnerId", "Sport", "StartTimeUtc", "IsIndoor", "DistanceMeters", "ElapsedTimeSeconds",
            "MovingTimeSeconds", "ElevationGainMeters", "HasGps", "MinLatitude", "MinLongitude", "MaxLatitude", "MaxLongitude"];
        string[] segmentInputs = ["OwnerId", "Sport", "DistanceMeters", "ToleranceMeters", "GeometryWkb",
            "MinLatitude", "MinLongitude", "MaxLatitude", "MaxLongitude"];
        var triggers = $"""
            CREATE TRIGGER IF NOT EXISTS "Projection_ActivityInsert" AFTER INSERT ON "Activities" BEGIN
                {AdvanceOwner("NEW.\"OwnerId\"")}
            END;
            CREATE TRIGGER IF NOT EXISTS "Projection_ActivityUpdate" AFTER UPDATE OF {Columns(activityInputs)} ON "Activities"
            WHEN {Changed(activityInputs)} BEGIN
                {AdvanceOwner("OLD.\"OwnerId\"")}
                {AdvanceOwner("NEW.\"OwnerId\"", " AND OLD.\"OwnerId\" IS NOT NEW.\"OwnerId\"")}
            END;
            CREATE TRIGGER IF NOT EXISTS "Projection_ActivityDelete" AFTER DELETE ON "Activities" BEGIN
                {AdvanceOwner("OLD.\"OwnerId\"")}
            END;
            CREATE TRIGGER IF NOT EXISTS "Projection_StreamInsert" AFTER INSERT ON "ActivityStreams" BEGIN
                {AdvanceOwner("(SELECT \"OwnerId\" FROM \"Activities\" WHERE \"Id\" = NEW.\"ActivityId\")")}
            END;
            CREATE TRIGGER IF NOT EXISTS "Projection_StreamUpdate" AFTER UPDATE OF "CompressedPayload" ON "ActivityStreams"
            WHEN OLD."CompressedPayload" IS NOT NEW."CompressedPayload" BEGIN
                {AdvanceOwner("(SELECT \"OwnerId\" FROM \"Activities\" WHERE \"Id\" = NEW.\"ActivityId\")")}
            END;
            CREATE TRIGGER IF NOT EXISTS "Projection_StreamDelete" AFTER DELETE ON "ActivityStreams" BEGIN
                {AdvanceOwner("(SELECT \"OwnerId\" FROM \"Activities\" WHERE \"Id\" = OLD.\"ActivityId\")")}
            END;
            CREATE TRIGGER IF NOT EXISTS "Projection_SegmentUpdate" AFTER UPDATE OF {Columns(segmentInputs)} ON "Segments"
            WHEN {Changed(segmentInputs)} BEGIN
                UPDATE "SegmentProjectionVersions" SET "DefinitionGeneration" = "DefinitionGeneration" + 1
                    WHERE "SegmentId" = NEW."Id";
            END;
            """;
        await db.Database.ExecuteSqlRawAsync(triggers, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string Columns(string[] columns) => string.Join(", ", columns.Select(column => $"\"{column}\""));
    private static string Changed(string[] columns) =>
        string.Join(" OR ", columns.Select(column => $"OLD.\"{column}\" IS NOT NEW.\"{column}\""));

    // The owner may already be gone when SQLite cascades a profile deletion.
    private static string AdvanceOwner(string ownerId, string extraCondition = "") => $"""
        INSERT INTO "OwnerProjectionVersions" ("OwnerId", "InputGeneration", "RecordsGeneration", "RecordsCount")
            SELECT "Id", 1, 0, 0 FROM "Owners" WHERE "Id" = {ownerId}{extraCondition}
            ON CONFLICT ("OwnerId") DO UPDATE SET "InputGeneration" = "InputGeneration" + 1;
        """;

    internal static IQueryable<OwnerProjectionVersion> PendingRecords(ExplorerDbContext db) =>
        db.OwnerProjectionVersions.AsNoTracking().Where(state => state.InputGeneration != state.RecordsGeneration ||
            state.RecordsCount != db.StatisticSnapshots.Count(record => record.OwnerId == state.OwnerId));

    internal static Task<long> ReadActivityGenerationAsync(ExplorerDbContext db, Guid ownerId, CancellationToken cancellationToken) =>
        db.OwnerProjectionVersions.Where(state => state.OwnerId == ownerId)
            .Select(state => state.InputGeneration).SingleOrDefaultAsync(cancellationToken);

    internal static Task<long> ReadDefinitionGenerationAsync(ExplorerDbContext db, Guid segmentId, CancellationToken cancellationToken) =>
        db.SegmentProjectionVersions.Where(state => state.SegmentId == segmentId)
            .Select(state => state.DefinitionGeneration).SingleOrDefaultAsync(cancellationToken);

    internal static IQueryable<Segment> PendingSegments(ExplorerDbContext db) =>
        db.Segments.AsNoTracking().Where(segment =>
            db.SegmentProjectionVersions.Any(state => state.SegmentId == segment.Id &&
                state.DefinitionGeneration != state.PublishedDefinitionGeneration) ||
            (db.OwnerProjectionVersions.Where(state => state.OwnerId == segment.OwnerId)
                .Select(state => (long?)state.InputGeneration).FirstOrDefault() ?? 0) !=
            (db.SegmentProjectionVersions.Where(state => state.SegmentId == segment.Id)
                .Select(state => (long?)state.PublishedActivityGeneration).FirstOrDefault() ?? 0));

    internal static IQueryable<SegmentEffort> CurrentEfforts(ExplorerDbContext db)
    {
        var pending = PendingSegments(db);
        return db.SegmentEfforts.AsNoTracking().Where(effort =>
            !pending.Any(segment => segment.Id == effort.SegmentId) &&
            effort.OwnerId == effort.Activity!.OwnerId && effort.OwnerId == effort.Segment!.OwnerId &&
            effort.Activity.Sport == effort.Segment.Sport);
    }

    internal static async Task PublishSegmentAsync(ExplorerDbContext db, Guid segmentId,
        long activityGeneration, long definitionGeneration, CancellationToken cancellationToken)
    {
        var state = await db.SegmentProjectionVersions.FindAsync([segmentId], cancellationToken);
        if (state is null)
        {
            state = new SegmentProjectionVersion { SegmentId = segmentId, DefinitionGeneration = definitionGeneration };
            db.SegmentProjectionVersions.Add(state);
        }
        state.PublishedActivityGeneration = activityGeneration;
        state.PublishedDefinitionGeneration = definitionGeneration;
    }
}
