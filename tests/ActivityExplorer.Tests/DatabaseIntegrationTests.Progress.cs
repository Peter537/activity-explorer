using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Progress_calendar_trends_and_activity_search_reconcile_owner_local_dates_and_sports()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var east = await setup.SeedOwnerAsync("Progress east");
        var west = await setup.SeedOwnerAsync("Progress west");
        var rows = new[]
        {
            ReportingActivity(east, ReportingInstant("2026-09-30T22:30:00Z"), 1),
            ReportingActivity(east, ReportingInstant("2026-10-01T08:00:00Z"), 2),
            ReportingActivity(west, ReportingInstant("2026-10-02T01:00:00Z"), 3),
            ReportingActivity(east, ReportingInstant("2026-10-25T00:30:00Z"), 4),
            ReportingActivity(east, ReportingInstant("2026-10-25T01:30:00Z"), 5),
            ReportingActivity(west, ReportingInstant("2026-11-01T06:30:00Z"), 6)
        };
        rows[0].MovingTimeSeconds = 0;
        rows[0].MovingTimeSource = MovingTimeSource.Unavailable;
        var excluded = ReportingActivity(east, ReportingInstant("2026-10-31T23:00:00Z"), 20);
        var otherSport = ReportingActivity(east, ReportingInstant("2026-10-01T12:00:00Z"), 30);
        otherSport.Sport = SportKind.Running;
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.Owners.SingleAsync(owner => owner.Id == west)).TimeZoneId = "America/Los_Angeles";
            db.Activities.AddRange(rows);
            db.Activities.AddRange(excluded, otherSport);
            await db.SaveChangesAsync();
        }
        var service = ReportingService(setup);
        var period = new ReportingDateSelection(ReportingPreset.Custom, new(2026, 10, 1), new(2026, 10, 31));
        var query = new DashboardQuery(Sport: SportKind.Cycling, Period: period, Granularity: TrendGranularity.Weekly);
        var dashboard = await service.GetDashboardAsync(query);
        var calendar = await service.GetTrainingCalendarAsync(new(Sport: SportKind.Cycling, Month: new(2026, 10, 1)));
        var search = await service.SearchAsync(new(Sport: SportKind.Cycling, From: period.From, To: period.To));

        Assert.Equal(new ReportingTotals(6, rows.Sum(row => row.DistanceMeters), rows.Sum(row => row.MovingTimeSeconds),
            rows.Sum(row => row.ElevationGainMeters), 3), dashboard.Totals);
        Assert.Equal(dashboard.Totals, calendar.Totals);
        Assert.Equal(search.Totals, new ActivityTotals(dashboard.Totals.ActivityCount, dashboard.Totals.DistanceMeters,
            dashboard.Totals.MovingSeconds, dashboard.Totals.ElevationMeters));
        Assert.Equal(31, calendar.Days.Count);
        Assert.Equal(3, calendar.Days.Single(day => day.Date == new DateOnly(2026, 10, 1)).Totals.ActivityCount);
        Assert.Equal(ReportingTotals.Empty, calendar.Days.Single(day => day.Date == new DateOnly(2026, 10, 2)).Totals);
        Assert.Equal(new DateOnly(2026, 10, 1), calendar.Weeks[0].Start);
        Assert.Equal(new DateOnly(2026, 10, 31), calendar.Weeks[^1].End);
        Assert.Equal(dashboard.Trend, calendar.Weeks);
        Assert.Equal(dashboard.Totals.ActivityCount, dashboard.Trend.Sum(bucket => bucket.Totals.ActivityCount));
        Assert.Equal(dashboard.Totals.DistanceMeters, dashboard.Trend.Sum(bucket => bucket.Totals.DistanceMeters));
        Assert.Equal(dashboard.Totals.MovingSeconds, calendar.Days.Sum(day => day.Totals.MovingSeconds));
        Assert.Equal(dashboard.Totals.ElevationMeters, calendar.Weeks.Sum(week => week.Totals.ElevationMeters));
        Assert.Equal(3, dashboard.Trend.Sum(bucket => bucket.Totals.ActiveDays));
        Assert.Equal(SportKind.Cycling, Assert.Single(dashboard.Sports).Sport);
        foreach (var day in calendar.Days.Where(day => day.Totals.ActivityCount > 0))
            Assert.Equal(day.Totals.ActivityCount, (await service.SearchAsync(new(Sport: SportKind.Cycling, From: day.Date, To: day.Date))).Total);
        var allSports = await service.GetDashboardAsync(query with { Sport = null, Granularity = TrendGranularity.Monthly });
        Assert.Equal(7, allSports.Totals.ActivityCount);
        Assert.Equal(3, allSports.Totals.ActiveDays);
        Assert.Equal(allSports.Totals, Assert.Single(allSports.Trend).Totals);
    }

    [Fact]
    public async Task Progress_relative_periods_and_comparisons_keep_each_owners_calendar_and_one_reference_instant()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var east = await setup.SeedOwnerAsync("Period east");
        var west = await setup.SeedOwnerAsync("Period west");
        var clock = new ReportingClock(ReportingInstant("2026-10-08T22:30:00Z"));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.Owners.SingleAsync(owner => owner.Id == west)).TimeZoneId = "America/Los_Angeles";
            db.Activities.AddRange(
                ReportingActivity(east, ReportingInstant("2026-10-08T22:15:00Z"), 1),
                ReportingActivity(west, ReportingInstant("2026-10-08T22:15:00Z"), 2),
                ReportingActivity(east, ReportingInstant("2026-10-07T22:15:00Z"), 3),
                ReportingActivity(west, ReportingInstant("2026-10-07T22:15:00Z"), 4));
            await db.SaveChangesAsync();
        }
        var dashboard = await ReportingService(setup, clock).GetDashboardAsync(new(Period: new(ReportingPreset.Today),
            Comparison: PeriodComparison.PreviousPeriod));

        Assert.Equal(2, dashboard.Totals.ActivityCount);
        Assert.Equal(2, dashboard.Totals.ActiveDays);
        Assert.Equal(3000, dashboard.Totals.DistanceMeters);
        Assert.Equal(7000, dashboard.ComparisonTotals!.DistanceMeters);
        Assert.Equal(new DateOnly(2026, 10, 9), dashboard.Periods.Single(period => period.OwnerId == east).From);
        Assert.Equal(new DateOnly(2026, 10, 8), dashboard.Periods.Single(period => period.OwnerId == west).From);
        Assert.Equal(new DateOnly(2026, 10, 8), dashboard.ComparisonPeriods.Single(period => period.OwnerId == east).From);
        Assert.Equal(new DateOnly(2026, 10, 7), dashboard.ComparisonPeriods.Single(period => period.OwnerId == west).From);
    }

    [Fact]
    public async Task Progress_queries_read_summary_columns_and_record_metadata_in_one_transaction_without_streams_or_geometry()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Progress summary");
        var activity = ReportingActivity(owner, ReportingInstant("2026-10-09T12:00:00Z"), 1);
        activity.Stream = new ActivityStream { OwnerId = owner, CompressedPayload = [1, 2, 3], PointCount = 100000 };
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.Add(activity);
            await db.SaveChangesAsync();
        }
        var capture = new ReportingCommandCapture();
        var factory = new TestDbFactory(new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(setup.DataDirectory, "test.db")}").AddInterceptors(capture).Options);
        var service = ReportingService(setup, factory: factory);

        var dashboard = await service.GetDashboardAsync(new(owner, Period: new(ReportingPreset.Custom, new(2026, 10, 1), new(2026, 10, 31))));

        Assert.Equal(1, dashboard.Totals.ActiveDays);
        Assert.NotEmpty(capture.Commands);
        Assert.DoesNotContain(capture.Commands, command => command.Sql.Contains("ActivityStreams", StringComparison.Ordinal) ||
            command.Sql.Contains("CompressedPayload", StringComparison.Ordinal) || command.Sql.Contains("GeometryWkb", StringComparison.Ordinal));
        var transaction = capture.Commands[0].Transaction;
        Assert.NotNull(transaction);
        Assert.All(capture.Commands, command => Assert.Same(transaction, command.Transaction));
        capture.Commands.Clear();
        await service.GetTrainingCalendarAsync(new(owner, Month: new(2026, 10, 1)));
        Assert.DoesNotContain(capture.Commands, command => command.Sql.Contains("ActivityStreams", StringComparison.Ordinal) ||
            command.Sql.Contains("GeometryWkb", StringComparison.Ordinal));
        Assert.NotNull(capture.Commands[0].Transaction);
        Assert.All(capture.Commands, command => Assert.Same(capture.Commands[0].Transaction, command.Transaction));
    }

    [Fact]
    public async Task Progress_unbounded_and_empty_periods_have_truthful_comparisons_and_zero_filled_bounded_trends()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Empty progress");
        var service = ReportingService(setup);
        foreach (var selection in new ReportingDateSelection[] { new(ReportingPreset.AllTime), new(ReportingPreset.Custom, new(2026, 1, 1)) })
        {
            var result = await service.GetDashboardAsync(new(owner, Period: selection));
            Assert.Equal(ReportingTotals.Empty, result.Totals);
            Assert.Null(result.ComparisonTotals);
            Assert.NotNull(result.ComparisonUnavailableReason);
            Assert.Empty(result.Trend);
            Assert.False(result.HasAnyActivities);
        }
        var bounded = await service.GetDashboardAsync(new(owner, Period: new(ReportingPreset.Custom, new(2024, 1, 1), new(2024, 2, 29))));
        Assert.Equal(2, bounded.Trend.Count);
        Assert.All(bounded.Trend, bucket => Assert.Equal(ReportingTotals.Empty, bucket.Totals));
        Assert.Equal(ReportingTotals.Empty, bounded.ComparisonTotals);
        Assert.Equal(new DateOnly(2023, 2, 28), Assert.Single(bounded.ComparisonPeriods).To);
        var calendar = await service.GetTrainingCalendarAsync(new(owner, Month: new(2024, 2, 1)));
        Assert.Equal(29, calendar.Days.Count);
        Assert.Equal(ReportingTotals.Empty, calendar.Totals);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetDashboardAsync(new(owner), cancellation.Token));
    }

    [Fact]
    public async Task Progress_fresh_reads_follow_timezone_transfer_import_and_deletion_without_a_derived_cache()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var first = await setup.SeedOwnerAsync("Changing progress");
        var second = await setup.SeedOwnerAsync("Receiving progress");
        var activity = ReportingActivity(first, ReportingInstant("2026-09-30T22:30:00Z"), 1);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.Add(activity);
            await db.SaveChangesAsync();
        }
        var service = ReportingService(setup);
        var query = new TrainingCalendarQuery(first, Month: new(2026, 10, 1));
        Assert.Equal(1, (await service.GetTrainingCalendarAsync(query)).Totals.ActivityCount);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.Owners.SingleAsync(owner => owner.Id == first)).TimeZoneId = "UTC";
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, (await service.GetTrainingCalendarAsync(query)).Totals.ActivityCount);
        await service.UpdateAsync(activity.Id, new(activity.Title, null, "Updated gear", second));
        Assert.Equal(1, (await service.GetTrainingCalendarAsync(query with { OwnerId = second })).Totals.ActivityCount);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.Add(ReportingActivity(second, ReportingInstant("2026-10-02T10:00:00Z"), 2));
            await db.SaveChangesAsync();
        }
        var current = await service.GetDashboardAsync(new(second, Period: new(ReportingPreset.Custom, new(2026, 10, 1), new(2026, 10, 31))));
        Assert.Equal(2, current.Totals.ActivityCount);
        Assert.Equal("Updated gear", Assert.Single(current.Gear).Name);
        await service.DeleteAsync([activity.Id]);
        Assert.Equal(1, (await service.GetTrainingCalendarAsync(query with { OwnerId = second })).Totals.ActivityCount);
    }
}
