using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Storage;

public static class McpLibraryRevision
{
    private static readonly string[] Tables =
    [
        "Owners", "Activities", "ActivityStreams", "ActivityLaps", "ActivityMetrics", "SourceFiles",
        "Routes", "Segments", "SegmentLinks", "SegmentEfforts", "Tags", "ActivityTags", "SavedSearches",
        "Goals", "GoalDefinitionRevisions", "WatchedFolders", "ApplicationSettings"
    ];

    public static Task<long> ReadAsync(ExplorerDbContext db, CancellationToken token) => db.Database.SqlQueryRaw<long>(
        "SELECT \"Revision\" AS \"Value\" FROM \"McpLibraryRevision\" WHERE \"Id\" = 1").SingleAsync(token);

    internal static async Task EnsureAsync(ExplorerDbContext db, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "McpLibraryRevision" (
                "Id" INTEGER NOT NULL PRIMARY KEY CHECK ("Id" = 1),
                "Revision" INTEGER NOT NULL CHECK (typeof("Revision") = 'integer' AND "Revision" >= 0));
            INSERT OR IGNORE INTO "McpLibraryRevision" ("Id", "Revision") VALUES (1, 0);
            """, token);

        var triggers = new StringBuilder();
        foreach (var table in Tables)
        {
            Append("INSERT");
            Append("DELETE");
            Append("UPDATE", table == "WatchedFolders" ? """
                 OF "Id", "OwnerId", "Path", "Enabled"
                """ : "", table == "WatchedFolders" ? """
                WHEN OLD."Id" IS NOT NEW."Id" OR OLD."OwnerId" IS NOT NEW."OwnerId"
                    OR OLD."Path" IS NOT NEW."Path" OR OLD."Enabled" IS NOT NEW."Enabled"
                """ : "");

            // Only static identifiers and SQL are used to create the triggers.
            void Append(string operation, string columns = "", string condition = "") => triggers.AppendLine(CultureInfo.InvariantCulture, $"""
                CREATE TRIGGER IF NOT EXISTS "McpLibrary_{table}_{operation}"
                AFTER {operation} {columns} ON "{table}" {condition}
                BEGIN
                    UPDATE "McpLibraryRevision" SET "Revision" = "Revision" + 1 WHERE "Id" = 1;
                END;
                """);
        }
        // The builder uses the static table inventory above and constant trigger fragments.
        await db.Database.ExecuteSqlRawAsync(triggers.ToString(), token);
        await transaction.CommitAsync(token);
    }
}
