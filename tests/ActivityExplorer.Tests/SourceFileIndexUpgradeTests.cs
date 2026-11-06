using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed class SourceFileIndexUpgradeTests
{
    [Fact]
    public async Task Legacy_index_upgrade_preserves_activity_uniqueness_and_allows_independent_routes()
    {
        var directory = TestSupport.NewDirectory();
        var options = new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "source-index.db")}").Options;
        await using var db = new ExplorerDbContext(options);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "SourceFiles" ("Id" TEXT PRIMARY KEY, "OwnerId" TEXT NOT NULL, "Provider" INTEGER NOT NULL,
                "Sha256" TEXT NOT NULL, "ActivityId" TEXT NULL, "RouteId" TEXT NULL);
            CREATE UNIQUE INDEX "IX_SourceFiles_OwnerId_Provider_Sha256" ON "SourceFiles" ("OwnerId", "Provider", "Sha256");
            INSERT INTO "SourceFiles" VALUES ('activity', 'owner', 0, 'hash', 'activity', NULL);
            """);
        await DatabaseInitializer.EnsureSourceFileIndexesAsync(db);
        await DatabaseInitializer.EnsureSourceFileIndexesAsync(db);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "SourceFiles" VALUES ('route1', 'owner', 0, 'hash', NULL, 'route1');
            INSERT INTO "SourceFiles" VALUES ('route2', 'owner', 0, 'hash', NULL, 'route2');
            """);
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"SourceFiles\" VALUES ('duplicate', 'owner', 0, 'hash', 'other', NULL)"));
        Assert.Equal(3, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM \"SourceFiles\"").SingleAsync());
    }
}
