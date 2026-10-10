using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed class DatabaseSchemaCompatibilityTests
{
    [Fact]
    public async Task Exploration_upgrade_is_additive_idempotent_and_cascades_only_derived_memberships()
    {
        var directory = TestSupport.NewDirectory();
        var options = new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "legacy-exploration.db")}").Options;
        await using var db = new ExplorerDbContext(options);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "Activities" ("Id" TEXT NOT NULL PRIMARY KEY, "Title" TEXT NOT NULL);
            INSERT INTO "Activities" VALUES ('activity', 'Preserved title'), ('other', 'Other title');
            """);
        await DatabaseInitializer.EnsureExplorationSchemaAsync(db);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "ActivityExplorationIndexes" VALUES ('activity', 0, 1, 1, 0, NULL), ('other', 0, 1, 1, 1, 'Point only');
            INSERT INTO "ActivityExplorationCells" VALUES ('activity', 42), ('other', 42);
            """);
        await DatabaseInitializer.EnsureExplorationSchemaAsync(db);
        Assert.Equal("Preserved title", await ScalarAsync<string>(db, "SELECT \"Title\" FROM \"Activities\" WHERE \"Id\" = 'activity'"));
        Assert.Equal(0L, await ScalarAsync<long>(db, "SELECT \"ExplorationInputVersion\" FROM \"Activities\" WHERE \"Id\" = 'activity'"));
        Assert.Equal(2L, await ScalarAsync<long>(db, "SELECT COUNT(*) FROM \"ActivityExplorationCells\""));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"ActivityExplorationCells\" VALUES ('activity', 42)"));
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Activities\" WHERE \"Id\" = 'activity'");
        Assert.Equal(1L, await ScalarAsync<long>(db, "SELECT COUNT(*) FROM \"ActivityExplorationCells\""));
        Assert.Equal("Point only", await ScalarAsync<string>(db, "SELECT \"Diagnostic\" FROM \"ActivityExplorationIndexes\""));
    }

    [Fact]
    public async Task Goal_upgrade_is_additive_idempotent_and_cascades_definitions_with_their_owner()
    {
        var directory = TestSupport.NewDirectory();
        var options = new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "legacy-goals.db")}").Options;
        await using var db = new ExplorerDbContext(options);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "Owners" ("Id" TEXT NOT NULL PRIMARY KEY);
            CREATE TABLE "Activities" ("Id" TEXT NOT NULL PRIMARY KEY, "Title" TEXT NOT NULL);
            INSERT INTO "Owners" VALUES ('owner'), ('other');
            INSERT INTO "Activities" VALUES ('activity', 'Preserved title');
            """);
        await DatabaseInitializer.EnsureGoalsSchemaAsync(db);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Goals" VALUES ('goal', 'owner', 0, 1, '2026-10-07', NULL, NULL, 2);
            INSERT INTO "Goals" VALUES ('retained', 'other', 3, 0, '2026-10-01', '2026-10-31', NULL, 0);
            INSERT INTO "GoalDefinitionRevisions" VALUES ('goal', '2026-10-05', 'Current', 1, 20000);
            INSERT INTO "GoalDefinitionRevisions" VALUES ('goal', '2026-10-12', 'Future', NULL, 30000);
            INSERT INTO "GoalDefinitionRevisions" VALUES ('retained', '2026-10-01', 'Other goal', NULL, 10);
            """);
        await DatabaseInitializer.EnsureGoalsSchemaAsync(db);
        Assert.Equal("Preserved title", await ScalarAsync<string>(db, "SELECT \"Title\" FROM \"Activities\""));
        Assert.Equal(2L, await ScalarAsync<long>(db, "SELECT \"MutationVersion\" FROM \"Goals\" WHERE \"Id\" = 'goal'"));
        Assert.Equal(3L, await ScalarAsync<long>(db, "SELECT COUNT(*) FROM \"GoalDefinitionRevisions\""));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"GoalDefinitionRevisions\" VALUES ('goal', '2026-10-12', 'Duplicate boundary', NULL, 50000)"));
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Owners\" WHERE \"Id\" = 'owner'");
        Assert.Equal(1L, await ScalarAsync<long>(db, "SELECT COUNT(*) FROM \"Goals\""));
        Assert.Equal("Other goal", await ScalarAsync<string>(db, "SELECT \"Name\" FROM \"GoalDefinitionRevisions\""));
        Assert.Equal(1L, await ScalarAsync<long>(db, "SELECT COUNT(*) FROM \"Activities\""));
    }

    [Fact]
    public async Task Organization_upgrade_is_additive_idempotent_and_keeps_saved_deleted_tag_references()
    {
        var directory = TestSupport.NewDirectory();
        var options = new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "legacy-organization.db")}").Options;
        await using var db = new ExplorerDbContext(options);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "Owners" ("Id" TEXT NOT NULL PRIMARY KEY);
            CREATE TABLE "Activities" ("Id" TEXT NOT NULL PRIMARY KEY, "Title" TEXT NOT NULL);
            INSERT INTO "Owners" VALUES ('owner');
            INSERT INTO "Activities" VALUES ('activity', 'Preserved title');
            """);
        await DatabaseInitializer.EnsureOrganizationSchemaAsync(db);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Tags" VALUES ('tag', 'owner', 'Commute', 'COMMUTE');
            INSERT INTO "ActivityTags" VALUES ('activity', 'tag');
            INSERT INTO "SavedSearches" VALUES ('saved', 'owner', 'Saved', 'SAVED', '{{"TagId":"tag"}}');
            """);
        await DatabaseInitializer.EnsureOrganizationSchemaAsync(db);
        Assert.Equal("Preserved title", await ScalarAsync<string>(db, "SELECT \"Title\" FROM \"Activities\""));
        Assert.Equal(0L, await ScalarAsync<long>(db, "SELECT \"MutationVersion\" FROM \"Activities\""));
        Assert.Equal(1L, await ScalarAsync<long>(db, "SELECT COUNT(*) FROM \"ActivityTags\""));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync("INSERT INTO \"Tags\" VALUES ('duplicate', 'owner', 'commute', 'COMMUTE')"));
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Tags\" WHERE \"Id\" = 'tag'");
        Assert.Equal(0L, await ScalarAsync<long>(db, "SELECT COUNT(*) FROM \"ActivityTags\""));
        Assert.Equal(1L, await ScalarAsync<long>(db, "SELECT COUNT(*) FROM \"Activities\""));
        Assert.Contains("tag", await ScalarAsync<string>(db, "SELECT \"CriteriaJson\" FROM \"SavedSearches\""), StringComparison.Ordinal);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Owners\" WHERE \"Id\" = 'owner'");
        Assert.Equal(0L, await ScalarAsync<long>(db, "SELECT COUNT(*) FROM \"SavedSearches\""));
    }

    [Fact]
    public async Task Adds_segment_links_idempotently_and_cascades_only_relationships()
    {
        var directory = TestSupport.NewDirectory();
        var options = new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "legacy-links.db")}").Options;
        await using var db = new ExplorerDbContext(options);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"Segments\" (\"Id\" TEXT NOT NULL PRIMARY KEY)");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO \"Segments\" VALUES ('parent'), ('child'), ('other')");
        await DatabaseInitializer.EnsureSegmentLinksAsync(db);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO \"SegmentLinks\" VALUES ('parent', 'child'), ('other', 'child')");
        await DatabaseInitializer.EnsureSegmentLinksAsync(db);
        Assert.Equal(2L, await ScalarAsync<long>(db, "SELECT COUNT(*) FROM \"SegmentLinks\""));
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Segments\" WHERE \"Id\" = 'parent'");
        Assert.Equal(2L, await ScalarAsync<long>(db, "SELECT COUNT(*) FROM \"Segments\""));
        Assert.Equal(1L, await ScalarAsync<long>(db, "SELECT COUNT(*) FROM \"SegmentLinks\""));
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Segments\" WHERE \"Id\" = 'child'");
        Assert.Equal(0L, await ScalarAsync<long>(db, "SELECT COUNT(*) FROM \"SegmentLinks\""));
    }

    [Fact]
    public async Task Adds_segment_provenance_columns_to_an_existing_database_idempotently()
    {
        var directory = TestSupport.NewDirectory();
        var database = Path.Combine(directory, "legacy.db");
        var options = new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={database}")
            .Options;
        await using var db = new ExplorerDbContext(options);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"Segments\" (\"Id\" TEXT NOT NULL PRIMARY KEY)");

        await DatabaseInitializer.EnsureSegmentProvenanceColumnsAsync(db);
        await DatabaseInitializer.EnsureSegmentProvenanceColumnsAsync(db);

        var columns = new List<string>();
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA table_info('Segments')";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
        Assert.Contains("SourceKind", columns);
        Assert.Contains("SourceName", columns);
        Assert.Contains("SourceFormat", columns);
    }

    [Fact]
    public async Task Adds_effort_metric_columns_idempotently_without_recalculating_legacy_rows()
    {
        var directory = TestSupport.NewDirectory();
        var database = Path.Combine(directory, "legacy-efforts.db");
        var options = new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={database}")
            .Options;
        await using var db = new ExplorerDbContext(options);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "SegmentEfforts" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "AverageSpeedMetersPerSecond" REAL NULL
            )
            """);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"SegmentEfforts\" (\"Id\", \"AverageSpeedMetersPerSecond\") VALUES ({0}, {1})",
            Guid.NewGuid(), 4.5);

        await DatabaseInitializer.EnsureSegmentEffortMetricColumnsAsync(db);
        await DatabaseInitializer.EnsureSegmentEffortMetricColumnsAsync(db);

        var columns = new List<string>();
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA table_info('SegmentEfforts')";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        Assert.Contains("RecordedDistanceMeters", columns);
        Assert.Contains("MetricComputationVersion", columns);
        Assert.Equal(4.5, await ScalarAsync<double>(db, "SELECT \"AverageSpeedMetersPerSecond\" FROM \"SegmentEfforts\""));
        Assert.Equal(1L, await ScalarAsync<long>(db, "SELECT \"MetricComputationVersion\" FROM \"SegmentEfforts\""));
        Assert.Null(await ScalarAsync<object?>(db, "SELECT \"RecordedDistanceMeters\" FROM \"SegmentEfforts\""));
    }

    private static async Task<T?> ScalarAsync<T>(ExplorerDbContext db, string sql)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            var result = await command.ExecuteScalarAsync();
            return result is null or DBNull
                ? default
                : (T)Convert.ChangeType(result, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
