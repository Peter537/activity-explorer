using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ActivityExplorer.Tests;

public sealed class ActivitySearchRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_functions_work_with_direct_closed_or_preopened_connections_and_reopening(bool alreadyOpen)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        if (alreadyOpen) await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite(connection).Options;
        await using var db = new ExplorerDbContext(options);
        await AssertFunctionsAsync(db);
        await connection.CloseAsync();
        await AssertFunctionsAsync(db);
    }

    [Fact]
    public async Task Search_functions_work_with_frozen_pooled_options_and_reused_contexts()
    {
        await using var services = new ServiceCollection()
            .AddPooledDbContextFactory<ExplorerDbContext>(options => options.UseSqlite("Data Source=:memory:"), poolSize: 1)
            .BuildServiceProvider();
        var factory = services.GetRequiredService<IDbContextFactory<ExplorerDbContext>>();
        ExplorerDbContext first;
        await using (first = await factory.CreateDbContextAsync())
        {
            await AssertFunctionsAsync(first);
        }
        await using var reused = await factory.CreateDbContextAsync();
        Assert.Same(first, reused);
        await AssertFunctionsAsync(reused);
    }

    private static async Task AssertFunctionsAsync(ExplorerDbContext db)
    {
        var result = await db.Database.SqlQueryRaw<int>("""
            SELECT CASE WHEN activity_contains('Æble ved SØEN', 'æBLE')
                AND activity_equals_name(' Å Bike ', 'å bike')
                AND NOT activity_contains(NULL, 'word')
                THEN 1 ELSE 0 END AS Value
            """).SingleAsync();
        Assert.Equal(1, result);
    }
}
