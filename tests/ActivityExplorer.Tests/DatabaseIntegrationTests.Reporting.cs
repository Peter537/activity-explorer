using System.Data.Common;
using System.Globalization;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Reporting_totals_cover_all_matches_for_combined_filters_independent_of_sort_and_page()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var first = await setup.SeedOwnerAsync("Reporting first");
        var second = await setup.SeedOwnerAsync("Reporting second");
        var start = ReportingInstant("2026-10-09T08:00:00Z");
        var matches = Enumerable.Range(1, 36).Select(index => ReportingActivity(
            index <= 31 ? first : second, start.AddMinutes(index), index)).ToArray();
        var wrongSport = ReportingActivity(first, start, 100); wrongSport.Sport = SportKind.Running;
        var wrongText = ReportingActivity(first, start, 101); wrongText.Title = "Other title"; wrongText.Description = null;
        var wrongPower = ReportingActivity(first, start, 102); wrongPower.HasPower = false;
        var wrongDevice = ReportingActivity(first, start, 103); wrongDevice.DeviceName = "Other recorder";
        var wrongDate = ReportingActivity(first, start.AddDays(-1), 104);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.AddRange(matches);
            db.Activities.AddRange(wrongSport, wrongText, wrongPower, wrongDevice, wrongDate);
            await db.SaveChangesAsync();
        }
        var service = ReportingService(setup);
        var filter = new ActivityFilter(
            Sport: SportKind.Cycling, From: new(2026, 10, 9), To: new(2026, 10, 9),
            Search: " Target ", HasPower: true, Device: " Synthetic ");
        var sorts = new (string Name, Func<Activity, double> Key, bool Descending)[]
        {
            ("start-desc", activity => activity.StartTimeUtc.ToUnixTimeSeconds(), true),
            ("start-asc", activity => activity.StartTimeUtc.ToUnixTimeSeconds(), false),
            ("distance-desc", activity => activity.DistanceMeters, true),
            ("distance-asc", activity => activity.DistanceMeters, false),
            ("duration-desc", activity => activity.MovingTimeSeconds, true),
            ("elevation-desc", activity => activity.ElevationGainMeters, true)
        };

        foreach (var sort in sorts)
        {
            var expectedOrder = (sort.Descending ? matches.OrderByDescending(sort.Key) : matches.OrderBy(sort.Key))
                .Select(activity => activity.Id).ToArray();
            var pageOne = await service.SearchAsync(filter with { Sort = sort.Name });
            var pageTwo = await service.SearchAsync(pageOne.EffectiveFilter with { Page = 2 });
            Assert.Equal(expectedOrder[..25], pageOne.Items.Select(activity => activity.Id));
            Assert.Equal(expectedOrder[25..], pageTwo.Items.Select(activity => activity.Id));
            Assert.Equal(2, pageOne.TotalPages);
            Assert.Equal(36, pageOne.Total);
            Assert.Equal(new ActivityTotals(36, 666000, 666000, 6660), pageOne.Totals);
            Assert.Equal(pageOne.Totals, pageTwo.Totals);
            Assert.Equal(expectedOrder, await service.GetMatchingActivityIdsAsync(pageTwo.EffectiveFilter));
        }

        var ownerOnly = await service.SearchAsync(filter with { OwnerId = first });
        Assert.Equal(new ActivityTotals(31, 496000, 651000, 5880), ownerOnly.Totals);
        var empty = await service.SearchAsync(filter with { Search = "There is no matching activity" });
        Assert.Empty(empty.Items);
        Assert.Equal(0, empty.Total);
        Assert.Equal(new ActivityTotals(0, 0, 0, 0), empty.Totals);
        Assert.Empty(await service.GetMatchingActivityIdsAsync(empty.EffectiveFilter));
    }

    [Theory]
    [InlineData("2026-03-29", "2026-03-28T23:00:00Z", "2026-03-29T22:00:00Z")]
    [InlineData("2026-10-25", "2026-10-24T22:00:00Z", "2026-10-25T23:00:00Z")]
    public async Task Reporting_rows_totals_IDs_and_map_lines_share_DST_boundaries(
        string date, string lower, string upper)
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("DST reporter");
        var lowerUtc = ReportingInstant(lower);
        var upperUtc = ReportingInstant(upper);
        var before = ReportingActivity(owner, lowerUtc.AddTicks(-1), 1);
        var first = ReportingActivity(owner, lowerUtc, 2);
        var last = ReportingActivity(owner, upperUtc.AddTicks(-1), 3);
        var after = ReportingActivity(owner, upperUtc, 4);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.AddRange(before, first, last, after);
            await db.SaveChangesAsync();
        }
        var day = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var query = new ActivityFilter(OwnerId: owner, From: day, To: day);
        var service = ReportingService(setup);
        var result = await service.SearchAsync(query);
        var maps = new MapFeatureService(setup.Factory);
        var lines = await maps.GetActivitiesAsync(new MapQuery(owner, From: day, To: day));
        var expected = new[] { first.Id, last.Id }.Order().ToArray();

        Assert.Equal(expected, result.Items.Select(activity => activity.Id).Order());
        Assert.Equal(new ActivityTotals(2, 5000, 69000, 480), result.Totals);
        Assert.Equal(expected, (await service.GetMatchingActivityIdsAsync(result.EffectiveFilter)).Order());
        Assert.Equal(expected, lines.Features.Select(feature => (Guid)feature.Properties["id"]!).Order());
        Assert.Equal(lowerUtc, result.Items.Single(activity => activity.Id == first.Id).StartTime);
        await using var verification = await setup.Factory.CreateDbContextAsync();
        Assert.All(await verification.Activities.ToListAsync(), activity => Assert.Equal(TimeSpan.FromHours(12), activity.OriginalUtcOffset));
    }

    [Fact]
    public async Task Reporting_midnight_filter_matches_native_local_dates_at_a_fractional_transition()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Midnight reporter");
        const string zoneId = "America/Sao_Paulo";
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var date = new DateOnly(2018, 11, 4);
        var nominal = ReportingInstant("2018-11-04T03:00:00Z");
        var activities = new[] { -TimeSpan.TicksPerMillisecond - 1, -TimeSpan.TicksPerMillisecond, -1, 0 }
            .Select((ticks, index) => ReportingActivity(owner, nominal.AddTicks(ticks), index + 1)).ToArray();
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.Owners.SingleAsync(profile => profile.Id == owner)).TimeZoneId = zoneId;
            db.Activities.AddRange(activities);
            await db.SaveChangesAsync();
        }
        var expected = activities.Where(activity =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(activity.StartTimeUtc, zone).DateTime) == date).ToArray();
        var ids = expected.Select(activity => activity.Id).Order().ToArray();
        var service = ReportingService(setup);

        var result = await service.SearchAsync(new ActivityFilter(OwnerId: owner, From: date, To: date));
        var map = await new MapFeatureService(setup.Factory).GetActivitiesAsync(new MapQuery(owner, From: date, To: date));

        Assert.Equal(ids, result.Items.Select(activity => activity.Id).Order());
        Assert.Equal(new ActivityTotals(expected.Length, expected.Sum(activity => activity.DistanceMeters),
            expected.Sum(activity => activity.MovingTimeSeconds), expected.Sum(activity => activity.ElevationGainMeters)), result.Totals);
        Assert.Equal(ids, (await service.GetMatchingActivityIdsAsync(result.EffectiveFilter)).Order());
        Assert.Equal(ids, map.Features.Select(feature => (Guid)feature.Properties["id"]!).Order());
    }

    [Fact]
    public async Task Relative_filters_use_each_owners_today_and_hold_the_displayed_window_until_reapplied()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var east = await setup.SeedOwnerAsync("East reporter");
        var west = await setup.SeedOwnerAsync("West reporter");
        var clock = new ReportingClock(ReportingInstant("2026-10-08T22:30:00Z"));
        var eastToday = ReportingActivity(east, ReportingInstant("2026-10-08T22:00:00Z"), 1);
        var eastYesterday = ReportingActivity(east, ReportingInstant("2026-10-08T21:59:59Z"), 2);
        var westToday = ReportingActivity(west, ReportingInstant("2026-10-08T07:00:00Z"), 3);
        var westTomorrow = ReportingActivity(west, ReportingInstant("2026-10-09T07:00:00Z"), 4);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.Owners.SingleAsync(owner => owner.Id == west)).TimeZoneId = "America/Los_Angeles";
            db.Activities.AddRange(eastToday, eastYesterday, westToday, westTomorrow);
            await db.SaveChangesAsync();
        }
        var service = ReportingService(setup, clock);
        var result = await service.SearchAsync(new ActivityFilter(Period: ReportingPreset.Today));
        var originalIds = new[] { eastToday.Id, westToday.Id }.Order().ToArray();
        Assert.Equal(originalIds, result.Items.Select(activity => activity.Id).Order());
        Assert.Equal(new DateOnly(2026, 10, 9), result.EffectiveFilter.ResolvedPeriods!.Single(period => period.OwnerId == east).From);
        Assert.Equal(new DateOnly(2026, 10, 8), result.EffectiveFilter.ResolvedPeriods!.Single(period => period.OwnerId == west).From);
        var capturedNow = result.EffectiveFilter.AsOfUtc;
        clock.Now = ReportingInstant("2026-10-09T22:30:00Z");

        var held = await service.SearchAsync(result.EffectiveFilter with { Sort = "start-asc" });
        Assert.Equal(capturedNow, held.EffectiveFilter.AsOfUtc);
        Assert.Equal(originalIds, held.Items.Select(activity => activity.Id).Order());
        Assert.Equal(originalIds, (await service.GetMatchingActivityIdsAsync(result.EffectiveFilter)).Order());
        var maps = new MapFeatureService(setup.Factory, clock);
        var heldLines = await maps.GetActivitiesAsync(new MapQuery(Period: ReportingPreset.Today, AsOfUtc: capturedNow));
        Assert.Equal(originalIds, heldLines.Features.Select(feature => (Guid)feature.Properties["id"]!).Order());

        var refreshed = await service.SearchAsync(new ActivityFilter(Period: ReportingPreset.Today));
        Assert.Equal(westTomorrow.Id, Assert.Single(refreshed.Items).Id);
        Assert.Equal(clock.Now, refreshed.EffectiveFilter.AsOfUtc);
    }

    [Fact]
    public async Task Reporting_queries_aggregate_in_SQL_without_streams_and_read_within_one_transaction()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Summary reporter");
        var activity = ReportingActivity(owner, ReportingInstant("2026-10-09T12:00:00Z"), 1);
        activity.Stream = new ActivityStream { OwnerId = owner, CompressedPayload = [1, 2, 3], PointCount = 100000 };
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.Add(activity);
            await db.SaveChangesAsync();
        }
        var capture = new ReportingCommandCapture();
        var factory = new TestDbFactory(new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(setup.DataDirectory, "test.db")}")
            .AddInterceptors(capture).Options);
        var service = ReportingService(setup, factory: factory);

        var result = await service.SearchAsync(new ActivityFilter(OwnerId: owner));

        Assert.Equal(new ActivityTotals(1, 1000, 36000, 170), result.Totals);
        Assert.NotEmpty(capture.Commands);
        Assert.Contains(capture.Commands, command => command.Sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(capture.Commands, command => command.Sql.Contains("ActivityStreams", StringComparison.Ordinal) || command.Sql.Contains("CompressedPayload", StringComparison.Ordinal));
        var transaction = capture.Commands[0].Transaction;
        Assert.NotNull(transaction);
        Assert.All(capture.Commands, command => Assert.Same(transaction, command.Transaction));
    }

    [Fact]
    public async Task Fresh_reporting_reads_reflect_timezone_changes_transfers_new_activities_and_exact_snapshot_deletion()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var firstOwner = await setup.SeedOwnerAsync("Changing reporter");
        var secondOwner = await setup.SeedOwnerAsync("Receiving reporter");
        var midnightActivity = ReportingActivity(firstOwner, ReportingInstant("2026-10-08T22:30:00Z"), 1);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.Add(midnightActivity);
            await db.SaveChangesAsync();
        }
        var service = ReportingService(setup);
        var today = new ActivityFilter(OwnerId: firstOwner, From: new(2026, 10, 9), To: new(2026, 10, 9));
        Assert.Equal(1, (await service.SearchAsync(today)).Total);
        var storage = CreateStorageServices(setup);
        var profiles = new ProfileService(setup.Factory, new AppDataPaths(), storage.FileOperations, storage.OwnerLock);
        await profiles.UpdateTimeZoneAsync(firstOwner, "UTC");
        Assert.Equal(0, (await service.SearchAsync(today)).Total);
        await service.UpdateAsync(midnightActivity.Id, new UpdateActivityRequest(midnightActivity.Title, midnightActivity.Description, null, secondOwner));
        var receivingFilter = today with { OwnerId = secondOwner };
        var beforeImport = await service.SearchAsync(receivingFilter);
        Assert.Equal(1, beforeImport.Total);
        var snapshot = await service.GetMatchingActivityIdsAsync(beforeImport.EffectiveFilter);
        var imported = ReportingActivity(secondOwner, ReportingInstant("2026-10-09T10:00:00Z"), 2);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.Add(imported);
            await db.SaveChangesAsync();
        }
        Assert.Equal(2, (await service.SearchAsync(receivingFilter)).Total);

        var deletion = await service.DeleteAsync(snapshot);
        var afterDeletion = await service.SearchAsync(receivingFilter);

        Assert.Equal(1, deletion.DeletedCount);
        Assert.Equal(imported.Id, Assert.Single(afterDeletion.Items).Id);
        Assert.Equal(new ActivityTotals(1, 2000, 35000, 340), afterDeletion.Totals);
    }

    private static ActivityQueryService ReportingService(
        DatabaseSetup setup, TimeProvider? clock = null, TestDbFactory? factory = null)
    {
        var storage = CreateStorageServices(setup);
        var contexts = factory ?? setup.Factory;
        return new ActivityQueryService(contexts, new StatisticsService(contexts),
            new SegmentService(contexts, new SegmentMatcher(), storage.OwnerLock),
            storage.Originals, storage.FileOperations, storage.OwnerLock,
            NullLogger<ActivityQueryService>.Instance, clock);
    }

    private static Activity ReportingActivity(Guid owner, DateTimeOffset start, int index)
    {
        var points = TestSupport.Track(3);
        return new Activity
        {
            OwnerId = owner,
            Title = index % 2 == 0 ? $"Target activity {index}" : $"Fictional activity {index}",
            Description = index % 2 == 0 ? null : "Target activity description",
            Sport = SportKind.Cycling,
            StartTimeUtc = start,
            OriginalUtcOffset = TimeSpan.FromHours(12),
            NaturalFingerprint = Guid.NewGuid().ToString("N"),
            DistanceMeters = index * 1000,
            MovingTimeSeconds = Math.Max(1, 37 - index) * 1000,
            ElapsedTimeSeconds = 100000,
            ElevationGainMeters = index * 17 % 37 * 10,
            DeviceName = "Synthetic recorder",
            HasPower = true,
            HasGps = true,
            GeometryWkb = GeometryCodec.ToWkb(points),
            SimplifiedGeometryWkb = GeometryCodec.ToWkb(points)
        };
    }

    private static DateTimeOffset ReportingInstant(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private sealed class ReportingClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ReportingCommandCapture : DbCommandInterceptor
    {
        public List<(string Sql, DbTransaction? Transaction)> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add((command.CommandText, command.Transaction));
            return ValueTask.FromResult(result);
        }
    }
}
