using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Storage;

// A disposable read model; ActivityExplorationCells remains authoritative.
internal static class ExplorationCellSetStore
{
    internal const int FormatVersion = 1;

    internal static async Task EnsureAsync(ExplorerDbContext db, CancellationToken cancellationToken = default)
    {
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "ActivityExplorationCellSets" (
                    "ActivityId" TEXT NOT NULL PRIMARY KEY,
                    "CellsJson" TEXT NOT NULL,
                    "FormatVersion" INTEGER NOT NULL,
                    FOREIGN KEY ("ActivityId") REFERENCES "ActivityExplorationIndexes" ("ActivityId") ON DELETE CASCADE
                ) WITHOUT ROWID;
                CREATE TRIGGER IF NOT EXISTS "TR_ExplorationCells_CellSet_Insert"
                AFTER INSERT ON "ActivityExplorationCells" BEGIN
                    DELETE FROM "ActivityExplorationCellSets" WHERE "ActivityId" = NEW."ActivityId";
                END;
                CREATE TRIGGER IF NOT EXISTS "TR_ExplorationCells_CellSet_Delete"
                AFTER DELETE ON "ActivityExplorationCells" BEGIN
                    DELETE FROM "ActivityExplorationCellSets" WHERE "ActivityId" = OLD."ActivityId";
                END;
                CREATE TRIGGER IF NOT EXISTS "TR_ExplorationCells_CellSet_Update"
                AFTER UPDATE ON "ActivityExplorationCells" BEGIN
                    DELETE FROM "ActivityExplorationCellSets" WHERE "ActivityId" IN (OLD."ActivityId", NEW."ActivityId");
                END;
                INSERT INTO "ActivityExplorationCellSets" ("ActivityId", "CellsJson", "FormatVersion")
                SELECT i."ActivityId", COALESCE((SELECT json_group_array(c."CellId")
                    FROM (SELECT "CellId" FROM "ActivityExplorationCells"
                          WHERE "ActivityId" = i."ActivityId" ORDER BY "CellId") AS c), '[]'), 1
                FROM "ActivityExplorationIndexes" AS i
                LEFT JOIN "ActivityExplorationCellSets" AS s ON s."ActivityId" = i."ActivityId"
                WHERE s."ActivityId" IS NULL OR s."FormatVersion" <> 1
                   OR CASE WHEN json_valid(s."CellsJson") THEN json_type(s."CellsJson") <> 'array' ELSE 1 END
                ON CONFLICT ("ActivityId") DO UPDATE SET
                    "CellsJson" = excluded."CellsJson", "FormatVersion" = excluded."FormatVersion";
                """, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        await ExplorationAggregateStore.EnsureAsync(db, cancellationToken);
    }

    internal static async Task PublishAsync(ExplorerDbContext db, Guid activityId, IReadOnlyList<int> cells, CancellationToken cancellationToken)
    {
        var exists = await db.Database.SqlQueryRaw<long>("""
            SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'ActivityExplorationCellSets') AS "Value"
            """).SingleAsync(cancellationToken);
        if (exists == 0) return;
        var json = JsonSerializer.Serialize(cells.Order().ToArray());
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ActivityExplorationCellSets" ("ActivityId", "CellsJson", "FormatVersion")
            VALUES ({activityId}, {json}, {FormatVersion})
            ON CONFLICT ("ActivityId") DO UPDATE SET "CellsJson" = excluded."CellsJson", "FormatVersion" = excluded."FormatVersion"
            """, cancellationToken);
    }
}
