using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Exploration_queries_include_more_than_2000_activities_without_reading_streams_and_hold_one_snapshot()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Exploration reporter");
        var start = ReportingInstant("2026-10-01T12:00:00Z");
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            for (var index = 0; index < 2101; index++)
            {
                var activity = ExplorationActivity(owner, start.AddSeconds(index));
                activity.Stream = new ActivityStream { OwnerId = owner, CompressedPayload = [1, 2, 3], PointCount = 50000 };
                SeedExploration(db, activity, 100, 101 + index);
            }
            SeedExploration(db, ExplorationActivity(owner, start.AddDays(1)));
            await db.SaveChangesAsync();
        }
        var capture = new ReportingCommandCapture();
        var factory = new TestDbFactory(new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(setup.DataDirectory, "test.db")}").AddInterceptors(capture).Options);
        var service = new ExplorationQueryService(factory);
        var query = ExplorationQueryFor(owner);

        var result = await service.GetAsync(query);
        var second = await service.GetAsync(query with { Page = 2 });
        var contributors = await service.GetCellAsync(query, 100, 2);
        var viewport = await service.GetViewportAsync(query, new(Zoom: 14));

        Assert.True(result.Index.IsComplete);
        Assert.Equal(2102, result.Index.TotalActivities);
        Assert.Equal(2101, result.Summary!.MatchingActivities);
        Assert.Equal(2102, result.Summary.VisitedCells);
        Assert.Equal(2101, result.Summary.MaximumFrequency);
        Assert.Equal(result.Summary, second.Summary);
        Assert.Equal(result.Summary, viewport.Summary);
        Assert.Equal(50, result.Cells.Items.Count);
        Assert.Equal(100, result.Cells.Items[0].CellId);
        Assert.Equal(2101, result.Cells.Items[0].ActivityCount);
        Assert.Empty(result.Cells.Items.Select(cell => cell.CellId).Intersect(second.Cells.Items.Select(cell => cell.CellId)));
        Assert.NotNull(contributors);
        Assert.Equal(2101, contributors.Activities.Total);
        Assert.Equal(25, contributors.Activities.Items.Count);
        Assert.InRange(viewport.Cells.Count, 1, 2048);
        Assert.True(viewport.RenderZoom < 14);
        Assert.Equal(2102, viewport.Cells.Sum(cell => cell.VisitedCells));
        Assert.DoesNotContain(capture.Commands, command => command.Sql.Contains("ActivityStreams", StringComparison.Ordinal) ||
            command.Sql.Contains("CompressedPayload", StringComparison.Ordinal) || command.Sql.Contains("GeometryWkb", StringComparison.Ordinal));
        Assert.All(capture.Commands, command => Assert.NotNull(command.Transaction));
        Assert.Equal(4, capture.Commands.Select(command => command.Transaction).Distinct().Count());
    }

    [Fact]
    public async Task Exploration_range_new_cells_use_all_owner_histories_and_local_boundaries()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var east = await setup.SeedOwnerAsync("East exploration");
        var west = await setup.SeedOwnerAsync("West exploration");
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.Owners.SingleAsync(owner => owner.Id == west)).TimeZoneId = "America/Los_Angeles";
            SeedExploration(db, ExplorationActivity(east, ReportingInstant("2026-10-07T22:00:00Z")), 1);
            SeedExploration(db, ExplorationActivity(east, ReportingInstant("2026-10-08T22:00:00Z")), 1, 2);
            SeedExploration(db, ExplorationActivity(west, ReportingInstant("2026-10-08T06:59:59Z")), 3);
            SeedExploration(db, ExplorationActivity(west, ReportingInstant("2026-10-08T07:00:00Z")), 2, 3, 4);
            SeedExploration(db, ExplorationActivity(west, ReportingInstant("2026-10-09T07:00:00Z")), 5);
            var otherSport = ExplorationActivity(east, ReportingInstant("2026-01-01T12:00:00Z"));
            otherSport.Sport = SportKind.Running;
            SeedExploration(db, otherSport, 2, 4);
            await db.SaveChangesAsync();
        }
        var query = new ExplorationQuery(new(Sport: SportKind.Cycling), new(ReportingPreset.Today), ReportingInstant("2026-10-08T22:30:00Z"));
        var service = new ExplorationQueryService(setup.Factory);

        var result = await service.GetAsync(query);
        var second = await service.GetAsync(query with { Scope = new(west, SportKind.Cycling) });

        Assert.Equal(4, result.Summary!.VisitedCells);
        Assert.Equal(2, result.Summary.NewCells);
        Assert.Equal(2, result.Summary.MatchingActivities);
        Assert.Equal(2, result.Summary.MaximumFrequency);
        Assert.Equal([2, 4], result.Cells.Items.Where(cell => cell.IsNew).Select(cell => cell.CellId).Order());
        Assert.Equal(new DateOnly(2026, 10, 7), result.Summary.FirstDate);
        Assert.Equal(new DateOnly(2026, 10, 9), result.Summary.LastDate);
        Assert.Equal(3, second.Summary!.VisitedCells);
        Assert.Equal(2, second.Summary.NewCells);
        Assert.Equal(5, result.Months.Single(month => month.Month == new DateOnly(2026, 10, 1)).NewCells);
        Assert.Equal(11, result.Months.Count(month => month.NewCells == 0));
        Assert.Equal(new DateOnly(2026, 10, 9), result.Periods.Single(period => period.OwnerId == east).From);
        Assert.Equal(new DateOnly(2026, 10, 8), result.Periods.Single(period => period.OwnerId == west).From);
    }

    [Theory]
    [InlineData("2026-03-29", "2026-03-28T23:00:00Z", "2026-03-29T22:00:00Z")]
    [InlineData("2026-10-25", "2026-10-24T22:00:00Z", "2026-10-25T23:00:00Z")]
    public async Task Exploration_ranges_follow_DST_and_keep_cell_first_last_dates_from_all_history(string date, string lower, string upper)
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Exploration DST");
        var lowerUtc = ReportingInstant(lower);
        var upperUtc = ReportingInstant(upper);
        var day = DateOnly.ParseExact(date, "yyyy-MM-dd");
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            SeedExploration(db, ExplorationActivity(owner, lowerUtc.AddTicks(-1)), 1);
            SeedExploration(db, ExplorationActivity(owner, lowerUtc), 1, 2);
            SeedExploration(db, ExplorationActivity(owner, upperUtc.AddTicks(-1)), 1, 3);
            SeedExploration(db, ExplorationActivity(owner, upperUtc), 1, 4);
            await db.SaveChangesAsync();
        }
        var query = ExplorationQueryFor(owner) with { Dates = new(ReportingPreset.Custom, day, day) };
        var service = new ExplorationQueryService(setup.Factory);

        var result = await service.GetAsync(query);
        var detail = await service.GetCellAsync(query, 1);

        Assert.Equal(3, result.Summary!.VisitedCells);
        Assert.Equal(2, result.Summary.NewCells);
        Assert.Equal(2, detail!.Activities.Total);
        Assert.Equal(day.AddDays(-1), detail.Cell.FirstVisit);
        Assert.Equal(day.AddDays(1), detail.Cell.LastVisit);
        Assert.All(detail.Activities.Items, activity => Assert.Equal(day, activity.Date));
    }

    [Fact]
    public async Task Exploration_cumulative_uses_shared_date_independent_of_range_with_new_cells_on_that_date()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Cumulative explorer");
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            SeedExploration(db, ExplorationActivity(owner, ReportingInstant("2026-08-20T12:00:00Z")), 1);
            SeedExploration(db, ExplorationActivity(owner, ReportingInstant("2026-09-10T12:00:00Z")), 1, 2);
            SeedExploration(db, ExplorationActivity(owner, ReportingInstant("2026-10-01T12:00:00Z")), 3);
            await db.SaveChangesAsync();
        }
        var service = new ExplorationQueryService(setup.Factory);
        var query = ExplorationQueryFor(owner) with
        {
            Cumulative = true,
            Dates = new(ReportingPreset.Custom, new(2026, 10, 1), new(2026, 10, 2)),
            Through = new(2026, 9, 10)
        };

        var result = await service.GetAsync(query);
        var defaultDate = await service.GetAsync(query with { Through = null });
        var earlyDefault = await service.GetAsync(query with { Through = null, Dates = new(ReportingPreset.Custom, To: new(2026, 1, 1)) });

        Assert.Equal(2, result.Summary!.VisitedCells);
        Assert.Equal(1, result.Summary.NewCells);
        Assert.Equal(2, result.Summary.MatchingActivities);
        Assert.Equal(new DateOnly(2026, 9, 10), result.Summary.Through);
        Assert.Equal(new DateOnly(2026, 8, 20), result.Summary.FirstDate);
        Assert.Equal(new DateOnly(2026, 10, 1), result.Summary.LastDate);
        Assert.Equal(new DateOnly(2026, 9, 1), result.HistoryMonth);
        Assert.Equal(2, result.Months.Sum(month => month.NewCells));
        Assert.Equal(2, Assert.Single(result.Cells.Items, cell => cell.IsNew).CellId);
        Assert.Equal(new DateOnly(2026, 10, 1), defaultDate.Summary!.Through);
        Assert.Equal(new DateOnly(2026, 8, 20), earlyDefault.Summary!.Through);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetAsync(query with { Through = new(2026, 1, 1) }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetAsync(query with { Through = new(2027, 1, 1) }));
    }

    [Fact]
    public async Task Exploration_current_metadata_and_membership_lifecycle_updates_discovery_without_rebuilding()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var first = await setup.SeedOwnerAsync("Original explorer");
        var second = await setup.SeedOwnerAsync("Receiving explorer");
        var early = ExplorationActivity(first, ReportingInstant("2026-09-30T22:30:00Z"));
        var late = ExplorationActivity(second, ReportingInstant("2026-10-02T10:00:00Z"));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            SeedExploration(db, early, 1);
            SeedExploration(db, late, 1, 2);
            await db.SaveChangesAsync();
        }
        var service = new ExplorationQueryService(setup.Factory);
        var query = ExplorationQueryFor(null);
        Assert.Equal(new DateOnly(2026, 10, 1), (await service.GetCellAsync(query, 1))!.Cell.FirstVisit);

        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.Owners.SingleAsync(owner => owner.Id == first)).TimeZoneId = "UTC";
            await db.SaveChangesAsync();
        }
        Assert.Equal(new DateOnly(2026, 9, 30), (await service.GetCellAsync(query, 1))!.Cell.FirstVisit);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var activity = await db.Activities.SingleAsync(activity => activity.Id == early.Id);
            activity.OwnerId = second;
            activity.Title = "Transferred expedition";
            activity.Sport = SportKind.Running;
            await db.SaveChangesAsync();
        }
        var received = await service.GetCellAsync(query with { Scope = new(second, SportKind.Running) }, 1);
        Assert.Equal(new DateOnly(2026, 10, 1), received!.Cell.FirstVisit);
        Assert.Equal("Transferred expedition", Assert.Single(received.Activities.Items).Title);
        var old = ExplorationActivity(second, ReportingInstant("2026-08-01T10:00:00Z"));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            SeedExploration(db, old, 1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(new DateOnly(2026, 8, 1), (await service.GetCellAsync(query, 1))!.Cell.FirstVisit);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.Remove(await db.Activities.SingleAsync(activity => activity.Id == old.Id));
            db.Activities.Remove(await db.Activities.SingleAsync(activity => activity.Id == early.Id));
            await db.SaveChangesAsync();
            Assert.False(await db.ActivityExplorationIndexes.AnyAsync(index => index.ActivityId == old.Id));
            Assert.False(await db.ActivityExplorationCells.AnyAsync(cell => cell.ActivityId == old.Id));
        }
        Assert.Equal(new DateOnly(2026, 10, 2), (await service.GetCellAsync(query, 1))!.Cell.FirstVisit);
    }

    [Fact]
    public async Task Exploration_completeness_includes_missing_or_stale_history_outside_date_and_viewport_filters()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Incomplete explorer");
        var old = ExplorationActivity(owner, ReportingInstant("2020-01-01T12:00:00Z"));
        var current = ExplorationActivity(owner, ReportingInstant("2026-10-01T12:00:00Z"));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.Add(old);
            SeedExploration(db, current, 1);
            db.ActivityExplorationIndexes.Local.Single(index => index.ActivityId == current.Id).IsLimited = true;
            await db.SaveChangesAsync();
        }
        var query = ExplorationQueryFor(owner) with { Dates = new(ReportingPreset.Custom, new(2026, 10, 1), new(2026, 10, 1)) };
        var service = new ExplorationQueryService(setup.Factory);
        var missing = await service.GetAsync(query);
        Assert.Equal(new ExplorationIndexStatus(2, 1, 1), missing.Index);
        Assert.Null(missing.Summary);
        Assert.Empty(missing.Cells.Items);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.ActivityExplorationIndexes.Add(new() { ActivityId = old.Id, InputVersion = old.ExplorationInputVersion, ComputationVersion = ExplorationIndexService.ComputationVersion });
            await db.SaveChangesAsync();
        }
        Assert.True((await service.GetAsync(query)).Index.IsComplete);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.Activities.SingleAsync(activity => activity.Id == old.Id)).ExplorationInputVersion++;
            await db.SaveChangesAsync();
        }
        var viewport = await service.GetViewportAsync(query, new(West: 170, East: -170, South: -5, North: 5));
        Assert.False(viewport.Index.IsComplete);
        Assert.Null(viewport.Summary);
        Assert.Empty(viewport.Cells);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.ActivityExplorationIndexes.SingleAsync(index => index.ActivityId == old.Id)).InputVersion++;
            (await db.ActivityExplorationIndexes.SingleAsync(index => index.ActivityId == current.Id)).ComputationVersion = 0;
            await db.SaveChangesAsync();
        }
        Assert.False((await service.GetAsync(query)).Index.IsComplete);
    }

    [Fact]
    public async Task Exploration_viewport_grouping_handles_dateline_and_preserves_scope_scale_and_exact_counts()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Dateline explorer");
        var y = ExplorationGrid.Size / 2;
        var west = y * ExplorationGrid.Size;
        var east = west + ExplorationGrid.Size - 1;
        var middle = west + ExplorationGrid.Size / 2;
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            SeedExploration(db, ExplorationActivity(owner, ReportingInstant("2026-10-01T12:00:00Z")), west, east, middle);
            SeedExploration(db, ExplorationActivity(owner, ReportingInstant("2026-10-02T12:00:00Z")), middle);
            await db.SaveChangesAsync();
        }
        var service = new ExplorationQueryService(setup.Factory);
        var query = ExplorationQueryFor(owner);
        var dateline = await service.GetViewportAsync(query, new(Zoom: 14, West: 179, East: -179, South: -1, North: 1));
        var center = await service.GetViewportAsync(query, new(Zoom: 14, West: -1, East: 1, South: -1, North: 1));
        var overview = await service.GetViewportAsync(query, new(Zoom: 0));

        Assert.Equal(2, dateline.Cells.Count);
        Assert.All(dateline.Cells, cell => Assert.Equal(1, cell.ActivityCount));
        Assert.Equal(2, dateline.Summary!.MaximumFrequency);
        Assert.Equal(dateline.Summary, center.Summary);
        Assert.Equal(2, Assert.Single(center.Cells).ActivityCount);
        Assert.Equal(3, overview.Cells.Sum(cell => cell.VisitedCells));
        Assert.Equal(3, overview.Cells.Sum(cell => cell.NewCells));
        Assert.All(dateline.Cells, cell => Assert.True(cell.West < cell.East && cell.South < cell.North));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetViewportAsync(query, new(West: 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetViewportAsync(query, new(West: double.NaN, East: 1, South: -1, North: 1)));
    }

    [Fact]
    public async Task Exploration_complete_no_GPS_scope_is_a_successful_empty_result()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Indoor explorer");
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            SeedExploration(db, ExplorationActivity(owner, ReportingInstant("2026-10-01T12:00:00Z")));
            await db.SaveChangesAsync();
        }
        var service = new ExplorationQueryService(setup.Factory);
        var result = await service.GetAsync(ExplorationQueryFor(owner) with { Cumulative = true });
        Assert.True(result.Index.IsComplete);
        Assert.Equal(0, result.Summary!.VisitedCells);
        Assert.Equal(0, result.Summary.MatchingActivities);
        Assert.Null(result.Summary.FirstDate);
        Assert.Null(result.Summary.LastDate);
        Assert.Null(result.Summary.Through);
        Assert.Empty(result.Cells.Items);
        Assert.Equal(12, result.Months.Count);
        Assert.All(result.Months, month => Assert.Equal(0, month.NewCells));
    }

    private static ExplorationQuery ExplorationQueryFor(Guid? owner) =>
        new(new(owner), new(ReportingPreset.AllTime), ReportingInstant("2026-10-10T12:00:00Z"));

    private static Activity ExplorationActivity(Guid owner, DateTimeOffset start) => new()
    {
        OwnerId = owner,
        Title = "Synthetic exploration activity",
        Sport = SportKind.Cycling,
        StartTimeUtc = start,
        NaturalFingerprint = Guid.NewGuid().ToString("N")
    };

    private static void SeedExploration(ExplorerDbContext db, Activity activity, params int[] cells)
    {
        db.Activities.Add(activity);
        db.ActivityExplorationIndexes.Add(new()
        {
            ActivityId = activity.Id,
            InputVersion = activity.ExplorationInputVersion,
            ComputationVersion = ExplorationIndexService.ComputationVersion,
            CellCount = cells.Length
        });
        db.ActivityExplorationCells.AddRange(cells.Select(cell => new ActivityExplorationCell { ActivityId = activity.Id, CellId = cell }));
    }
}
