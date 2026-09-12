using System.Diagnostics;
using System.Data.Common;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Activity = ActivityExplorer.Core.Domain.Activity;

namespace ActivityExplorer.Tests;

public sealed class RecordAttemptTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Six_kilometres_has_only_one_five_kilometre_attempt()
    {
        var windows = Calculate(Track(6_000, 10), RecordKind.DistanceEffort, 5_000);
        Assert.Single(windows);
        Assert.Equal(500, windows[0].Value, 6);
    }

    [Fact]
    public void Adjacent_attempts_share_an_interpolated_endpoint_without_losing_distance()
    {
        var windows = Calculate([Point(0, 0), Point(1_000, 10_000)], RecordKind.DistanceEffort, 5_000);
        Assert.Equal(2, windows.Count);
        Assert.Equal(0.5, windows[0].FinishPosition, 10);
        Assert.Equal(windows[0].FinishPosition, windows[1].StartPosition);
        Assert.All(windows, window => Assert.Equal(500, window.Value, 6));
    }

    [Fact]
    public void Best_middle_attempt_wins_even_when_two_slower_attempts_could_fit()
    {
        var windows = Calculate([Point(0, 0), Point(500, 2_500), Point(750, 7_500), Point(1_250, 10_000)], RecordKind.DistanceEffort, 5_000);
        var best = Assert.Single(windows);
        Assert.Equal(250, best.Value);
        Assert.Equal(1, best.StartPosition);
        Assert.Equal(2, best.FinishPosition);
    }

    [Theory]
    [InlineData(RecordKind.TimedDistanceEffort, 7.3)]
    [InlineData(RecordKind.PowerCurve, 7.3)]
    public void Fractional_cut_anchors_fill_adjacent_time_windows(RecordKind kind, double target)
    {
        var points = Enumerable.Range(0, 31).Select(index => Point(index, index * 5, 200)).ToArray();
        var windows = Calculate(points, kind, target);
        Assert.Equal(4, windows.Count);
        AssertNonOverlapping(windows);
        Assert.All(windows, window => Assert.InRange(window.Value, kind == RecordKind.PowerCurve ? 199.999 : 36.499, kind == RecordKind.PowerCurve ? 200.001 : 36.501));
    }

    [Fact]
    public void Artificial_power_cuts_do_not_qualify_a_shortened_interior_window()
    {
        var points = Enumerable.Range(0, 21).Select(index => Point(index, index, index is >= 5 and < 15 ? 500 : 100)).ToArray();
        var windows = Calculate(points, RecordKind.PowerCurve, 10);
        Assert.Single(windows);
        Assert.Equal(500, windows[0].Value);
    }

    [Fact]
    public void Power_retains_natural_boundary_coverage_and_gap_rules()
    {
        var points = Enumerable.Range(0, 99).Select(index => Point(index, index, 100)).ToArray();
        Assert.Equal(98, Assert.Single(Calculate(points, RecordKind.PowerCurve, 100)).CoveragePercent);
        Assert.Empty(Calculate(points[..98], RecordKind.PowerCurve, 100));
        Assert.Empty(Calculate([Point(0, 0, 100), Point(6, 6, 100)], RecordKind.PowerCurve, 5));
    }

    [Fact]
    public void Non_overlap_uses_source_positions_even_when_timestamps_reset_or_roads_repeat()
    {
        var points = new[] { Point(0, 0), Point(100, 1_000), Point(0, 0), Point(100, 1_000) };
        var windows = Calculate(points, RecordKind.DistanceEffort, 1_000);
        Assert.Equal(2, windows.Count);
        AssertNonOverlapping(windows);
        Assert.Equal(windows[0].StartTime, windows[1].StartTime);
    }

    [Fact]
    public void Distance_attempts_preserve_pauses_resets_and_gps_eligibility()
    {
        Assert.Equal(120, Assert.Single(Calculate([Point(0, 0), Point(50, 500), Point(70, 500), Point(120, 1_000)], RecordKind.DistanceEffort, 1_000)).Value);
        Assert.Empty(Calculate([Point(0, 0), Point(50, 500), Point(60, 0), Point(110, 500)], RecordKind.DistanceEffort, 1_000));
        var indoor = Track(1_000, 5).Select(point => point with { Latitude = null, Longitude = null }).ToArray();
        Assert.Empty(Calculate(indoor, RecordKind.DistanceEffort, 500));
        Assert.Equal(2, Calculate(indoor, RecordKind.DistanceEffort, 500, SportKind.Rowing).Count);
        Assert.NotEmpty(Calculate(indoor, RecordKind.TimedDistanceEffort, 60));
    }

    [Theory]
    [InlineData(SportKind.Cycling)]
    [InlineData(SportKind.Running)]
    [InlineData(SportKind.Walking)]
    [InlineData(SportKind.Rowing)]
    public void First_attempt_matches_existing_winner_for_every_catalog_target(SportKind sport)
    {
        var points = Enumerable.Range(0, 901).Select(index => Point(index * 4, index * 20, 100 + index % 73)).ToArray();
        foreach (var kind in new[] { RecordKind.DistanceEffort, RecordKind.TimedDistanceEffort, RecordKind.PowerCurve })
        {
            var targets = kind switch
            {
                RecordKind.DistanceEffort => RecordCatalog.DistanceTargets(sport),
                RecordKind.TimedDistanceEffort => RecordCatalog.TimedDistanceTargets(sport),
                _ => RecordCatalog.PowerTargets
            };
            foreach (var target in targets)
            {
                var existing = kind switch
                {
                    RecordKind.DistanceEffort => BestEffortCalculator.BestDistance(points, target.Target, sport),
                    RecordKind.TimedDistanceEffort => BestEffortCalculator.BestTimedDistance(points, target.Target, sport),
                    _ => BestEffortCalculator.BestPower(points, target.Target)
                };
                var attempts = BestEffortCalculator.Attempts(points, sport, kind, target.Target, false);
                Assert.Equal(existing.HasValue, attempts.Count == 1);
                if (existing.HasValue)
                {
                    Assert.Equal(existing.Value.Value, attempts[0].Value, 6);
                    Assert.Equal(existing.Value.CoveragePercent, attempts[0].CoveragePercent, 6);
                    var multiple = Calculate(points, kind, target.Target, sport);
                    Assert.Equal(attempts[0], multiple[0]);
                }
            }
        }
    }

    [Fact]
    public void Many_short_attempts_are_deterministic_and_finish_without_quadratic_rescans()
    {
        var points = Enumerable.Range(0, 100_001).Select(index => Point(index, index * 5, 200)).ToArray();
        var timer = Stopwatch.StartNew();
        var windows = Calculate(points, RecordKind.PowerCurve, 5);
        Assert.Equal(20_000, windows.Count);
        AssertNonOverlapping(windows);
        Assert.Equal(0, windows[0].StartPosition);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), $"Attempt selection took {timer.Elapsed}.");
    }

    [Fact]
    public async Task Query_filters_profiles_and_scopes_paginates_and_reloads_current_data()
    {
        var factory = await Database();
        var owner = new OwnerProfile { DisplayName = "Attempt athlete" };
        var other = new OwnerProfile { DisplayName = "Other athlete" };
        await using (var db = factory.CreateDbContext())
        {
            db.Owners.AddRange(owner, other);
            for (var index = 0; index < 61; index++) db.Activities.Add(Activity(owner.Id, $"Ride {index:00}", index % 2 == 0));
            db.Activities.Add(Activity(other.Id, "Other ride", false));
            await db.SaveChangesAsync();
        }
        var service = new StatisticsService(factory);
        var query = new RecordAttemptQuery(SportKind.Cycling, RecordKind.DistanceEffort, "5 km", owner.Id);
        var first = (await service.GetAttemptsAsync(query))!;
        var second = (await service.GetAttemptsAsync(query with { Page = 2 }))!;
        Assert.Equal(61, first.Attempts.Total);
        Assert.Equal(50, first.Attempts.Items.Count);
        Assert.Equal(11, second.Attempts.Items.Count);
        Assert.Equal(61, first.Attempts.Items.Concat(second.Attempts.Items).Select(item => item.ActivityId).Distinct().Count());
        Assert.Equal(2, (await service.GetAttemptsAsync(query with { Page = int.MaxValue }))!.Attempts.Page);
        Assert.Equal(31, (await service.GetAttemptsAsync(query with { Scope = RecordScope.Indoor }))!.Attempts.Total);
        Assert.Equal(30, (await service.GetAttemptsAsync(query with { Scope = RecordScope.Outdoor }))!.Attempts.Total);
        Assert.Equal(62, (await service.GetAttemptsAsync(query with { OwnerId = null }))!.Attempts.Total);
        Assert.Equal(122, (await service.GetAttemptsAsync(query with { MultiplePerActivity = true }))!.Attempts.Total);
        Assert.Equal(0, (await service.GetAttemptsAsync(query with { OwnerId = Guid.NewGuid() }))!.Attempts.Total);
        Assert.Equal(0, (await service.GetAttemptsAsync(query with { Sport = SportKind.Running }))!.Attempts.Total);
        await using (var db = factory.CreateDbContext())
        {
            var activity = await db.Activities.Include(item => item.Stream).FirstAsync(item => item.OwnerId == owner.Id);
            activity.Title = "Corrected ride";
            activity.OwnerId = other.Id;
            activity.Stream!.OwnerId = other.Id;
            await db.SaveChangesAsync();
        }
        Assert.Equal(60, (await service.GetAttemptsAsync(query))!.Attempts.Total);
        Assert.Contains((await service.GetAttemptsAsync(query with { OwnerId = other.Id }))!.Attempts.Items, item => item.ActivityTitle == "Corrected ride");
        await using (var db = factory.CreateDbContext())
        {
            db.Activities.Remove(await db.Activities.FirstAsync(item => item.OwnerId == owner.Id));
            await db.SaveChangesAsync();
        }
        Assert.Equal(59, (await service.GetAttemptsAsync(query))!.Attempts.Total);
        Assert.Null(await service.GetAttemptsAsync(query with { Key = "Not a benchmark" }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAttemptsAsync(query, new CancellationToken(true)));
    }

    [Fact]
    public async Task Whole_activity_categories_do_not_decode_streams_and_follow_existing_eligibility()
    {
        var counter = new QueryCounter();
        var factory = await Database(counter);
        var owner = new OwnerProfile { DisplayName = "Summary athlete" };
        await using (var db = factory.CreateDbContext())
        {
            db.Owners.Add(owner);
            var activity = Activity(owner.Id, "Summary ride", false);
            activity.Stream!.CompressedPayload = [0, 1, 2];
            db.Activities.Add(activity);
            var shortActivity = Activity(owner.Id, "Short ride", false);
            shortActivity.DistanceMeters = 500;
            shortActivity.Stream = null;
            db.Activities.Add(shortActivity);
            await db.SaveChangesAsync();
        }
        var service = new StatisticsService(factory);
        counter.Commands.Clear();
        foreach (var (kind, key) in new[] { (RecordKind.Distance, "Longest distance"), (RecordKind.Duration, "Longest moving time"), (RecordKind.Elevation, "Most elevation gain"), (RecordKind.AverageSpeed, "Best average speed (activities >= 1 km)") })
        {
            var result = (await service.GetAttemptsAsync(new(SportKind.Cycling, kind, key, owner.Id, MultiplePerActivity: true)))!;
            Assert.True(result.Benchmark.IsWholeActivity);
            Assert.Equal(kind == RecordKind.AverageSpeed ? 1 : 2, result.Attempts.Total);
            Assert.All(result.Attempts.Items, item => Assert.Null(item.StartPosition));
        }
        Assert.Null(await service.GetAttemptsAsync(new(SportKind.Rowing, RecordKind.Elevation, "Most elevation gain")));
        Assert.DoesNotContain(counter.Commands, command => command.Contains("ActivityStreams", StringComparison.Ordinal));
    }

    private static IReadOnlyList<EffortWindow> Calculate(IReadOnlyList<TrackPoint> points, RecordKind kind, double target, SportKind sport = SportKind.Cycling) =>
        BestEffortCalculator.Attempts(points, sport, kind, target, true);
    private static TrackPoint Point(double seconds, double distance, double power = 200) =>
        new(Start.AddSeconds(seconds), 0, distance / 111_195, distance, null, null, null, null, power, null);
    private static TrackPoint[] Track(int distance, int speed) =>
        Enumerable.Range(0, distance / speed + 1).Select(index => Point(index, index * speed)).ToArray();
    private static void AssertNonOverlapping(IReadOnlyList<EffortWindow> windows)
    {
        var ordered = windows.OrderBy(window => window.StartPosition).ToArray();
        for (var index = 1; index < ordered.Length; index++) Assert.True(ordered[index - 1].FinishPosition <= ordered[index].StartPosition);
    }
    private static Activity Activity(Guid owner, string title, bool indoor) => new()
    {
        OwnerId = owner,
        Title = title,
        Sport = SportKind.Cycling,
        IsIndoor = indoor,
        HasGps = true,
        StartTimeUtc = Start,
        DistanceMeters = 10_000,
        MovingTimeSeconds = 1_000,
        ElevationGainMeters = 50,
        NaturalFingerprint = Guid.NewGuid().ToString("N"),
        Stream = new() { OwnerId = owner, PointCount = 1_001, CompressedPayload = TrackCodec.Encode(Track(10_000, 10)) }
    };
    private static async Task<TestDbFactory> Database(QueryCounter? counter = null)
    {
        var path = Path.Combine(TestSupport.NewDirectory(), "attempts.db");
        var builder = new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite($"Data Source={path}");
        if (counter is not null) builder.AddInterceptors(counter);
        var factory = new TestDbFactory(builder.Options);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        return factory;
    }
    private sealed class QueryCounter : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
    private sealed class TestDbFactory(DbContextOptions<ExplorerDbContext> options) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);
        public Task<ExplorerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
