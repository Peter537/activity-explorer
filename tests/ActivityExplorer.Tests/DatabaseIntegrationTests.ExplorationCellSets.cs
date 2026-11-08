using System.Text.Json;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Compact_exploration_sets_preserve_results_and_fall_back_on_invalidation_or_corruption()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Cell-set owner");
        var other = await setup.SeedOwnerAsync("Other cell-set owner");
        var first = ExplorationActivity(owner, ReportingInstant("2026-10-01T12:00:00Z"));
        var second = ExplorationActivity(owner, ReportingInstant("2026-10-02T12:00:00Z"));
        var foreign = ExplorationActivity(other, ReportingInstant("2026-10-02T02:00:00Z"));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.Owners.SingleAsync(profile => profile.Id == owner)).TimeZoneId = "Europe/Copenhagen";
            (await db.Owners.SingleAsync(profile => profile.Id == other)).TimeZoneId = "America/Los_Angeles";
            SeedExploration(db, first, 1, 2);
            SeedExploration(db, second, 2, 3);
            SeedExploration(db, foreign, 8);
            await db.SaveChangesAsync();
        }
        var capture = new ReportingCommandCapture();
        var factory = new TestDbFactory(new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(setup.DataDirectory, "test.db")}").AddInterceptors(capture).Options);
        var service = new ExplorationQueryService(factory);
        var query = ExplorationQueryFor(owner);
        var baseline = await service.GetAsync(query);
        var baselineViewport = await service.GetViewportAsync(query, new(Zoom: 14));
        var multiQuery = ExplorationQueryFor(null) with
        {
            Dates = new(ReportingPreset.Custom, new(2026, 10, 2), new(2026, 10, 2)),
            NewOnly = true
        };
        var multiBaseline = await service.GetAsync(multiQuery);
        var multiViewport = await service.GetViewportAsync(multiQuery, new(Zoom: 14));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            await ExplorationCellSetStore.EnsureAsync(db);
            await ExplorationCellSetStore.EnsureAsync(db);
            Assert.Equal(3L, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM ActivityExplorationCellSets").SingleAsync());
        }
        capture.Commands.Clear();
        var compact = await service.GetAsync(query);
        var compactViewport = await service.GetViewportAsync(query, new(Zoom: 14));
        Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(compact));
        Assert.Equal(JsonSerializer.Serialize(baselineViewport), JsonSerializer.Serialize(compactViewport));
        Assert.Equal(JsonSerializer.Serialize(multiBaseline), JsonSerializer.Serialize(await service.GetAsync(multiQuery)));
        Assert.Equal(JsonSerializer.Serialize(multiViewport), JsonSerializer.Serialize(await service.GetViewportAsync(multiQuery, new(Zoom: 14))));
        // The native clustered reader is not an EF-intercepted command. A successful
        // fast path must not fall back to an EF read of compact metadata or memberships.
        Assert.DoesNotContain(capture.Commands, command => command.Sql.Contains("\"CellsJson\"", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Commands, command => command.Sql.Contains("FROM \"ActivityExplorationCells\"", StringComparison.Ordinal));

        // A partial update invalidates the cache in the same transaction, and rollback restores it.
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ActivityExplorationCells (ActivityId, CellId) VALUES ({first.Id}, {4})");
            Assert.Equal(0L, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM ActivityExplorationCellSets WHERE CellsJson = '[1,2]'").SingleAsync());
            await transaction.RollbackAsync();
            Assert.Equal(1L, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM ActivityExplorationCellSets WHERE CellsJson = '[1,2]'").SingleAsync());
            var nonCanonical = " [1, 2] ";
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ActivityExplorationCellSets SET CellsJson = {nonCanonical} WHERE ActivityId = {first.Id}");
        }
        capture.Commands.Clear();
        var compatibleFallback = await service.GetAsync(query);
        Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(compatibleFallback));
        Assert.Contains(capture.Commands, command => command.Sql.Contains("FROM \"ActivityExplorationCellSets\"", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Commands, command => command.Sql.Contains("FROM \"ActivityExplorationCells\"", StringComparison.Ordinal));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var malformed = "[x]";
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ActivityExplorationCellSets SET CellsJson = {malformed} WHERE ActivityId = {first.Id}");
        }
        capture.Commands.Clear();
        var corruptFallback = await service.GetAsync(query);
        Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(corruptFallback));
        Assert.Contains(capture.Commands, command => command.Sql.Contains("FROM \"ActivityExplorationCells\"", StringComparison.Ordinal));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ActivityExplorationIndexes SET InputVersion = InputVersion + 1 WHERE ActivityId = {first.Id}");
        }
        var incomplete = await service.GetAsync(query);
        Assert.False(incomplete.Index.IsComplete);
        Assert.Null(incomplete.Summary);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ActivityExplorationIndexes SET InputVersion = InputVersion - 1 WHERE ActivityId = {first.Id}");
            await ExplorationCellSetStore.EnsureAsync(db);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ActivityExplorationCells (ActivityId, CellId) VALUES ({first.Id}, {4})");
        }
        capture.Commands.Clear();
        var invalidated = await service.GetAsync(query);
        Assert.Contains(capture.Commands, command => command.Sql.Contains("FROM \"ActivityExplorationCells\"", StringComparison.Ordinal));
        await using (var db = await setup.Factory.CreateDbContextAsync())
            await db.Database.ExecuteSqlRawAsync("DROP TABLE ActivityExplorationCellSets");
        var authoritative = await service.GetAsync(query);
        Assert.Equal(JsonSerializer.Serialize(authoritative), JsonSerializer.Serialize(invalidated));
    }
    [Fact]
    public async Task Clustered_exploration_rows_follow_source_changes_and_fall_back_if_missing_or_corrupt()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var firstOwner = await setup.SeedOwnerAsync("First cluster owner");
        var secondOwner = await setup.SeedOwnerAsync("Second cluster owner");
        var activity = ExplorationActivity(firstOwner, ReportingInstant("2026-10-01T12:00:00Z"));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            SeedExploration(db, activity, 5, 6);
            await db.SaveChangesAsync();
            await ExplorationCellSetStore.EnsureAsync(db);
        }
        var capture = new ReportingCommandCapture();
        var factory = new TestDbFactory(new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(setup.DataDirectory, "test.db")}").AddInterceptors(capture).Options);
        var service = new ExplorationQueryService(factory);
        var query = ExplorationQueryFor(null);
        var original = await service.GetAsync(query);
        Assert.Contains(capture.Commands, command => command.Sql.Contains("ActivityExplorationAggregateRows", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Commands, command => command.Sql.Contains("\"CellsJson\"", StringComparison.Ordinal));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var row = await db.Activities.SingleAsync(value => value.Id == activity.Id);
            row.OwnerId = secondOwner;
            row.StartTimeUtc = row.StartTimeUtc.AddDays(1);
            await db.SaveChangesAsync();
        }
        var moved = await service.GetAsync(query);
        Assert.NotEqual(original.Summary!.FirstDate, moved.Summary!.FirstDate);
        Assert.Empty((await service.GetAsync(ExplorationQueryFor(firstOwner))).Cells.Items);
        Assert.Equal(2, (await service.GetAsync(ExplorationQueryFor(secondOwner))).Summary!.VisitedCells);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var malformed = "[broken]";
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ActivityExplorationAggregateRows SET CellsJson = {malformed} WHERE ActivityId = {activity.Id}");
        }
        capture.Commands.Clear();
        var corrupt = await service.GetAsync(query);
        Assert.Equal(JsonSerializer.Serialize(moved), JsonSerializer.Serialize(corrupt));
        Assert.Contains(capture.Commands, command => command.Sql.Contains("ActivityExplorationCellSets", StringComparison.Ordinal));
        await using (var db = await setup.Factory.CreateDbContextAsync())
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM ActivityExplorationAggregateRows WHERE ActivityId = {activity.Id}");
        capture.Commands.Clear();
        var missing = await service.GetAsync(query);
        Assert.Equal(JsonSerializer.Serialize(moved), JsonSerializer.Serialize(missing));
        Assert.Contains(capture.Commands, command => command.Sql.Contains("ActivityExplorationCellSets", StringComparison.Ordinal));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            await ExplorationAggregateStore.EnsureAsync(db);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ActivityExplorationIndexes SET InputVersion = InputVersion + 1 WHERE ActivityId = {activity.Id}");
        }
        var stale = await service.GetAsync(query);
        Assert.False(stale.Index.IsComplete);
        Assert.Null(stale.Summary);
        await using (var db = await setup.Factory.CreateDbContextAsync())
            await db.Database.ExecuteSqlRawAsync("DROP TABLE ActivityExplorationAggregateRows");
        var legacy = await service.GetAsync(query);
        Assert.Equal(JsonSerializer.Serialize(stale), JsonSerializer.Serialize(legacy));
    }

}
