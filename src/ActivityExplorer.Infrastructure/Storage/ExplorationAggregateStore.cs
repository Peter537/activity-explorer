using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ActivityExplorer.Infrastructure.Storage;

// Rebuildable, clustered projection of authoritative activity, header and cell-set rows.
internal static class ExplorationAggregateStore
{
    internal const int FormatVersion = 1;

    internal static async Task EnsureAsync(ExplorerDbContext db, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS "ActivityExplorationAggregateRows" (
                "OwnerId" TEXT NOT NULL,
                "ActivityId" TEXT NOT NULL,
                "Sport" INTEGER NOT NULL,
                "StartTimeTicks" INTEGER NOT NULL,
                "ExplorationInputVersion" INTEGER NOT NULL,
                "InputVersion" INTEGER NOT NULL,
                "ComputationVersion" INTEGER NOT NULL,
                "IsLimited" INTEGER NOT NULL,
                "CellCount" INTEGER NOT NULL,
                "CellsJson" TEXT NOT NULL,
                "FormatVersion" INTEGER NOT NULL,
                "AggregateVersion" INTEGER NOT NULL,
                PRIMARY KEY ("OwnerId", "ActivityId"),
                UNIQUE ("ActivityId"),
                FOREIGN KEY ("ActivityId") REFERENCES "ActivityExplorationCellSets" ("ActivityId") ON DELETE CASCADE
            ) WITHOUT ROWID;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        // The only interpolated text is a fixed SQL expression selected below, never user data.
        static string Refresh(string id) => $"""
            INSERT OR REPLACE INTO "ActivityExplorationAggregateRows"
                ("OwnerId", "ActivityId", "Sport", "StartTimeTicks", "ExplorationInputVersion", "InputVersion",
                 "ComputationVersion", "IsLimited", "CellCount", "CellsJson", "FormatVersion", "AggregateVersion")
            SELECT a."OwnerId", a."Id", a."Sport", a."StartTimeUtc", a."ExplorationInputVersion", i."InputVersion",
                   i."ComputationVersion", i."IsLimited", i."CellCount", c."CellsJson", c."FormatVersion", 1
            FROM "Activities" AS a
            JOIN "ActivityExplorationIndexes" AS i ON i."ActivityId" = a."Id"
            JOIN "ActivityExplorationCellSets" AS c ON c."ActivityId" = a."Id"
            WHERE a."Id" = {id};
            """;
        var triggers = new[]
        {
            $"CREATE TRIGGER IF NOT EXISTS \"TR_ExplorationAggregate_Set_Insert\" AFTER INSERT ON \"ActivityExplorationCellSets\" BEGIN {Refresh("NEW.\"ActivityId\"")} END",
            $"CREATE TRIGGER IF NOT EXISTS \"TR_ExplorationAggregate_Set_Update\" AFTER UPDATE ON \"ActivityExplorationCellSets\" BEGIN DELETE FROM \"ActivityExplorationAggregateRows\" WHERE \"ActivityId\" = OLD.\"ActivityId\"; {Refresh("NEW.\"ActivityId\"")} END",
            "CREATE TRIGGER IF NOT EXISTS \"TR_ExplorationAggregate_Set_Delete\" AFTER DELETE ON \"ActivityExplorationCellSets\" BEGIN DELETE FROM \"ActivityExplorationAggregateRows\" WHERE \"ActivityId\" = OLD.\"ActivityId\"; END",
            $"CREATE TRIGGER IF NOT EXISTS \"TR_ExplorationAggregate_Header_Insert\" AFTER INSERT ON \"ActivityExplorationIndexes\" BEGIN {Refresh("NEW.\"ActivityId\"")} END",
            $"CREATE TRIGGER IF NOT EXISTS \"TR_ExplorationAggregate_Header_Update\" AFTER UPDATE ON \"ActivityExplorationIndexes\" BEGIN DELETE FROM \"ActivityExplorationAggregateRows\" WHERE \"ActivityId\" = OLD.\"ActivityId\"; {Refresh("NEW.\"ActivityId\"")} END",
            "CREATE TRIGGER IF NOT EXISTS \"TR_ExplorationAggregate_Header_Delete\" AFTER DELETE ON \"ActivityExplorationIndexes\" BEGIN DELETE FROM \"ActivityExplorationAggregateRows\" WHERE \"ActivityId\" = OLD.\"ActivityId\"; END",
            $"CREATE TRIGGER IF NOT EXISTS \"TR_ExplorationAggregate_Activity_Update\" AFTER UPDATE OF \"Id\", \"OwnerId\", \"Sport\", \"StartTimeUtc\", \"ExplorationInputVersion\" ON \"Activities\" BEGIN DELETE FROM \"ActivityExplorationAggregateRows\" WHERE \"ActivityId\" = OLD.\"Id\"; {Refresh("NEW.\"Id\"")} END",
            "CREATE TRIGGER IF NOT EXISTS \"TR_ExplorationAggregate_Activity_Delete\" AFTER DELETE ON \"Activities\" BEGIN DELETE FROM \"ActivityExplorationAggregateRows\" WHERE \"ActivityId\" = OLD.\"Id\"; END"
        };
        foreach (var sql in triggers)
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        command.CommandText = """
            INSERT OR REPLACE INTO "ActivityExplorationAggregateRows"
                ("OwnerId", "ActivityId", "Sport", "StartTimeTicks", "ExplorationInputVersion", "InputVersion",
                 "ComputationVersion", "IsLimited", "CellCount", "CellsJson", "FormatVersion", "AggregateVersion")
            SELECT a."OwnerId", a."Id", a."Sport", a."StartTimeUtc", a."ExplorationInputVersion", i."InputVersion",
                   i."ComputationVersion", i."IsLimited", i."CellCount", c."CellsJson", c."FormatVersion", 1
            FROM "Activities" AS a
            JOIN "ActivityExplorationIndexes" AS i ON i."ActivityId" = a."Id"
            JOIN "ActivityExplorationCellSets" AS c ON c."ActivityId" = a."Id"
            WHERE NOT EXISTS (SELECT 1 FROM "ActivityExplorationAggregateRows" AS r
                              WHERE r."ActivityId" = a."Id" AND r."AggregateVersion" = 1)
            ORDER BY a."OwnerId", a."Id";
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
