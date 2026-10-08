using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed class DatabaseSchemaCompatibilityTests
{
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
