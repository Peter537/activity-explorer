using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task New_cell_display_filters_before_pagination_and_map_grouping_without_changing_summary_or_history()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("New-cell finder");
        var oldCells = Enumerable.Range(100, 60).ToArray();
        var newCells = Enumerable.Range(1000, 55).ToArray();
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            SeedExploration(db, ExplorationActivity(owner, ReportingInstant("2026-09-01T12:00:00Z")), oldCells);
            SeedExploration(db, ExplorationActivity(owner, ReportingInstant("2026-10-01T12:00:00Z")), oldCells.Concat(newCells).ToArray());
            SeedExploration(db, ExplorationActivity(owner, ReportingInstant("2026-10-02T12:00:00Z")), oldCells);
            SeedExploration(db, ExplorationActivity(owner, ReportingInstant("2026-10-02T13:00:00Z")), oldCells);
            await db.SaveChangesAsync();
        }
        var service = new ExplorationQueryService(setup.Factory);
        var query = ExplorationQueryFor(owner) with { Dates = new(ReportingPreset.Custom, new(2026, 10, 1), new(2026, 10, 2)) };
        var all = await service.GetAsync(query);
        var filtered = await service.GetAsync(query with { NewOnly = true });
        var pageTwo = await service.GetAsync(query with { NewOnly = true, Page = 2 });
        var oldDetail = await service.GetCellAsync(query with { NewOnly = true }, oldCells[0]);
        var newDetail = await service.GetCellAsync(query with { NewOnly = true }, newCells[0]);
        var allMap = await service.GetViewportAsync(query, new(Zoom: 0));
        var filteredMap = await service.GetViewportAsync(query with { NewOnly = true }, new(Zoom: 0));

        Assert.Equal(115, all.Summary!.VisitedCells);
        Assert.Equal(55, all.Summary.NewCells);
        Assert.Equal(3, all.Summary.MaximumFrequency);
        Assert.Equal(3, all.Summary.MatchingActivities);
        Assert.DoesNotContain(all.Cells.Items, cell => cell.IsNew);
        Assert.Equal(all.Summary, filtered.Summary);
        Assert.Equal(all.Months, filtered.Months);
        Assert.Equal(55, filtered.Cells.Total);
        Assert.Equal(50, filtered.Cells.Items.Count);
        Assert.Equal(newCells.Take(50), filtered.Cells.Items.Select(cell => cell.CellId));
        Assert.Equal(5, pageTwo.Cells.Items.Count);
        Assert.Equal(newCells.Skip(50), pageTwo.Cells.Items.Select(cell => cell.CellId));
        Assert.All(filtered.Cells.Items.Concat(pageTwo.Cells.Items), cell => Assert.True(cell.IsNew));
        Assert.Null(oldDetail);
        Assert.Equal(1, newDetail!.Activities.Total);
        Assert.Equal(all.Summary, filteredMap.Summary);
        Assert.Equal(115, allMap.Cells.Sum(cell => cell.VisitedCells));
        Assert.Equal(55, filteredMap.Cells.Sum(cell => cell.VisitedCells));
        Assert.Equal(55, filteredMap.Cells.Sum(cell => cell.NewCells));
        Assert.All(filteredMap.Cells, cell => Assert.Equal(1, cell.ActivityCount));

        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.Activities.Where(activity => activity.OwnerId == owner).OrderBy(activity => activity.StartTimeUtc).FirstAsync())
                .ExplorationInputVersion++;
            await db.SaveChangesAsync();
        }
        var staleHistory = await service.GetAsync(query with { NewOnly = true });
        Assert.False(staleHistory.Index.IsComplete);
        Assert.Null(staleHistory.Summary);
        Assert.Empty(staleHistory.Cells.Items);
    }

    [Fact]
    public async Task New_only_cumulative_cells_mean_first_discovered_on_through_date()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Cumulative new-cell finder");
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            SeedExploration(db, ExplorationActivity(owner, ReportingInstant("2026-09-30T12:00:00Z")), 1, 2);
            SeedExploration(db, ExplorationActivity(owner, ReportingInstant("2026-10-01T12:00:00Z")), 1, 3, 4);
            SeedExploration(db, ExplorationActivity(owner, ReportingInstant("2026-10-02T12:00:00Z")), 1, 3, 5);
            await db.SaveChangesAsync();
        }
        var service = new ExplorationQueryService(setup.Factory);
        var query = ExplorationQueryFor(owner) with
        {
            Cumulative = true,
            Through = new(2026, 10, 1),
            NewOnly = true,
            Dates = new(ReportingPreset.Custom, new(2026, 10, 2), new(2026, 10, 2))
        };
        var first = await service.GetAsync(query);
        var next = await service.GetAsync(query with { Through = new(2026, 10, 2) });
        var nextMap = await service.GetViewportAsync(query with { Through = new(2026, 10, 2) }, new(Zoom: 14));

        Assert.Equal([3, 4], first.Cells.Items.Select(cell => cell.CellId));
        Assert.Equal(4, first.Summary!.VisitedCells);
        Assert.Equal(2, first.Summary.NewCells);
        Assert.Equal(5, next.Summary!.VisitedCells);
        Assert.Equal(1, next.Summary.NewCells);
        Assert.Equal(5, Assert.Single(next.Cells.Items).CellId);
        Assert.Equal(1, nextMap.Cells.Sum(cell => cell.VisitedCells));
        Assert.Equal(1, nextMap.Cells.Sum(cell => cell.NewCells));
        Assert.Equal(5, next.Months.Sum(month => month.NewCells));
        Assert.Null(await service.GetCellAsync(query with { Through = new(2026, 10, 2) }, 3));
        Assert.NotNull(await service.GetCellAsync(query, 3));
    }

    [Fact]
    public async Task New_cell_filter_preserves_owner_local_first_discovery_and_sport_scope()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var east = await setup.SeedOwnerAsync("East new-cell finder");
        var west = await setup.SeedOwnerAsync("West new-cell finder");
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.Owners.SingleAsync(owner => owner.Id == west)).TimeZoneId = "America/Los_Angeles";
            SeedExploration(db, ExplorationActivity(east, ReportingInstant("2026-10-01T21:59:59Z")), 1);
            SeedExploration(db, ExplorationActivity(east, ReportingInstant("2026-10-01T22:00:00Z")), 1, 2);
            SeedExploration(db, ExplorationActivity(west, ReportingInstant("2026-10-02T06:59:59Z")), 3);
            SeedExploration(db, ExplorationActivity(west, ReportingInstant("2026-10-02T07:00:00Z")), 2, 3, 4);
            var running = ExplorationActivity(east, ReportingInstant("2020-01-01T12:00:00Z"));
            running.Sport = SportKind.Running;
            SeedExploration(db, running, 2, 4);
            await db.SaveChangesAsync();
        }
        var service = new ExplorationQueryService(setup.Factory);
        var query = new ExplorationQuery(new(Sport: SportKind.Cycling),
            new(ReportingPreset.Custom, new(2026, 10, 2), new(2026, 10, 2)),
            ReportingInstant("2026-10-03T12:00:00Z"), NewOnly: true);
        var combined = await service.GetAsync(query);
        var western = await service.GetAsync(query with { Scope = new(west, SportKind.Cycling) });

        Assert.Equal([2, 4], combined.Cells.Items.Select(cell => cell.CellId));
        Assert.Equal(4, combined.Summary!.VisitedCells);
        Assert.Equal(2, combined.Summary.NewCells);
        Assert.Equal([2, 4], western.Cells.Items.Select(cell => cell.CellId));
        Assert.Equal(3, western.Summary!.VisitedCells);
    }
}
