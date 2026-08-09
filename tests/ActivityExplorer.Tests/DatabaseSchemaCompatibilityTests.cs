using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed class DatabaseSchemaCompatibilityTests
{
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
}
