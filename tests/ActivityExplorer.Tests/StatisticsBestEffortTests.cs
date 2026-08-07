using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActivityExplorer.Tests;

public sealed class StatisticsBestEffortTests
{
    [Fact]
    public void Record_catalog_has_the_expected_ordered_targets()
    {
        Assert.Equal(
            ["5 km", "5 miles", "10 km", "10 miles", "20 km", "30 km", "40 km", "50 km", "80 km", "50 miles", "90 km", "100 km", "100 miles", "180 km", "200 km"],
            RecordCatalog.DistanceTargets(SportKind.Cycling).Select(target => target.Key));

        string[] running = ["400 m", "1 km", "1/2 mile", "1 mile", "2 miles", "5 km", "10 km", "15 km", "10 miles", "20 km", "Half marathon", "30 km", "Marathon", "50 km"];
        Assert.Equal(running, RecordCatalog.DistanceTargets(SportKind.Running).Select(target => target.Key));
        Assert.Equal(running, RecordCatalog.DistanceTargets(SportKind.Walking).Select(target => target.Key));

        Assert.Equal(
            ["5 s", "15 s", "30 s", "1 min", "2 min", "3 min", "5 min", "8 min", "10 min", "15 min", "20 min", "30 min", "45 min", "1 hour", "2 hours"],
            RecordCatalog.PowerTargets.Select(target => target.Key));
    }

    [Fact]
    public void Distance_effort_interpolates_the_exact_crossing()
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var points = new[]
        {
            Point(start, latitude: 0, longitude: 0),
            Point(start.AddSeconds(20), latitude: 0, longitude: 0.009)
        };
        var target = GeometryCodec.HaversineMeters(0, 0, 0, 0.009) / 2;

        var effort = BestEffortCalculator.BestDistance(points, target, SportKind.Cycling);

        Assert.NotNull(effort);
        Assert.Equal(10, effort.Value.Value, precision: 6);
        Assert.Equal(100, effort.Value.CoveragePercent);
    }

    [Fact]
    public void Distance_effort_bridges_geometry_fallback_gaps_and_counts_elapsed_time()
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var points = new[]
        {
            Point(start, latitude: 0, longitude: 0),
            Point(start.AddSeconds(10), latitude: 0, longitude: 0.004),
            Point(start.AddSeconds(70), latitude: 0, longitude: 0.004),
            Point(start.AddSeconds(80), latitude: 0, longitude: 0.008)
        };
        var target = GeometryCodec.HaversineMeters(0, 0, 0, 0.008);

        var effort = BestEffortCalculator.BestDistance(points, target, SportKind.Cycling);

        Assert.NotNull(effort);
        Assert.Equal(80, effort.Value.Value, precision: 6);
    }

    [Fact]
    public void Distance_effort_bridges_recorded_zero_distance_pauses_and_counts_elapsed_time()
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var points = new[]
        {
            Point(start, latitude: 0, longitude: 0, distance: 0),
            Point(start.AddSeconds(10), latitude: 0, longitude: 0.004, distance: 500),
            Point(start.AddSeconds(70), latitude: 0, longitude: 0.004, distance: 500),
            Point(start.AddSeconds(80), latitude: 0, longitude: 0.008, distance: 1_000)
        };

        var effort = BestEffortCalculator.BestDistance(points, 1_000, SportKind.Cycling);

        Assert.NotNull(effort);
        Assert.Equal(80, effort.Value.Value, precision: 6);
    }

    [Fact]
    public void Distance_effort_prefers_monotonic_recorded_distance()
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var points = new[]
        {
            Point(start, latitude: 0, longitude: 0, distance: 0),
            Point(start.AddSeconds(30), latitude: 0, longitude: 0.01, distance: 1_000)
        };

        var effort = BestEffortCalculator.BestDistance(points, 500, SportKind.Cycling);

        Assert.NotNull(effort);
        Assert.Equal(15, effort.Value.Value, precision: 6);
    }

    [Fact]
    public void Distance_effort_splits_at_a_distance_counter_reset()
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var points = new[]
        {
            Point(start, latitude: 0, longitude: 0, distance: 0),
            Point(start.AddSeconds(10), latitude: 0, longitude: 0, distance: 500),
            Point(start.AddSeconds(20), latitude: 0, longitude: 0, distance: 100),
            Point(start.AddSeconds(30), latitude: 0, longitude: 0, distance: 600)
        };

        Assert.Null(BestEffortCalculator.BestDistance(points, 750, SportKind.Cycling));
    }

    [Theory]
    [InlineData(SportKind.Cycling, 200)]
    [InlineData(SportKind.Running, 60)]
    [InlineData(SportKind.Walking, 30)]
    public void Distance_effort_rejects_edges_above_the_sport_speed_cap(SportKind sport, double capKmh)
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var edgeDistance = (capKmh + 1) / 3.6 * 10;
        var points = new[]
        {
            Point(start, latitude: 0, longitude: 0, distance: 0),
            Point(start.AddSeconds(10), latitude: 0, longitude: 0, distance: edgeDistance)
        };

        Assert.Null(BestEffortCalculator.BestDistance(points, 50, sport));
    }

    [Fact]
    public void Distance_effort_accepts_an_edge_at_the_speed_cap()
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var points = new[]
        {
            Point(start, latitude: 0, longitude: 0, distance: 0),
            Point(start.AddSeconds(10), latitude: 0, longitude: 0, distance: 200d / 3.6 * 10)
        };

        var effort = BestEffortCalculator.BestDistance(points, 100, SportKind.Cycling);

        Assert.NotNull(effort);
        Assert.Equal(1.8, effort.Value.Value, precision: 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Distance_effort_splits_at_nonpositive_timestamps(int seconds)
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var points = new[]
        {
            Point(start, latitude: 0, longitude: 0, distance: 0),
            Point(start.AddSeconds(seconds), latitude: 0, longitude: 0, distance: 500)
        };

        Assert.Null(BestEffortCalculator.BestDistance(points, 100, SportKind.Cycling));
    }

    [Fact]
    public void Distance_effort_splits_at_invalid_coordinates()
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var points = new[]
        {
            Point(start, latitude: 0, longitude: 0, distance: 0),
            Point(start.AddSeconds(10), latitude: 91, longitude: 0, distance: 500),
            Point(start.AddSeconds(20), latitude: 0, longitude: 0, distance: 1_000)
        };

        Assert.Null(BestEffortCalculator.BestDistance(points, 500, SportKind.Cycling));
    }

    [Fact]
    public void Power_effort_is_time_weighted_for_irregular_samples()
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var points = new[]
        {
            Point(start, power: 100),
            Point(start.AddSeconds(2), power: 200),
            Point(start.AddSeconds(5), power: 200)
        };

        var effort = BestEffortCalculator.BestPower(points, 5);

        Assert.NotNull(effort);
        Assert.Equal(160, effort.Value.Value, precision: 6);
        Assert.Equal(100, effort.Value.CoveragePercent);
    }

    [Fact]
    public void Two_hour_power_effort_is_supported_without_gps()
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var points = Enumerable.Range(0, 7_201)
            .Select(index => Point(start.AddSeconds(index), power: 250))
            .ToArray();

        var effort = BestEffortCalculator.BestPower(points, 7_200);

        Assert.NotNull(effort);
        Assert.Equal(250, effort.Value.Value, precision: 6);
        Assert.Equal(100, effort.Value.CoveragePercent);
    }

    [Fact]
    public void Power_effort_rejects_sample_gaps_over_five_seconds()
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var points = new[]
        {
            Point(start, power: 300),
            Point(start.AddSeconds(1), power: 300),
            Point(start.AddSeconds(7), power: 300),
            Point(start.AddSeconds(8), power: 300)
        };

        Assert.Null(BestEffortCalculator.BestPower(points, 5));
    }

    [Theory]
    [InlineData(SportKind.Cycling)]
    [InlineData(SportKind.Running)]
    [InlineData(SportKind.Walking)]
    public async Task Every_supported_sport_gets_power_records_without_gps(SportKind sport)
    {
        var setup = await Setup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync($"{sport} athlete");
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var points = Enumerable.Range(0, 7_201)
            .Select(index => Point(start.AddSeconds(index), power: 225))
            .ToArray();
        await setup.AddActivityAsync(ownerId, sport, points, "Indoor power");
        var statistics = new StatisticsService(setup.Factory);

        await statistics.RecomputeAsync(ownerId);
        var records = await statistics.GetRecordsAsync(ownerId);
        var powerRecords = records.Where(record => record.Kind == RecordKind.PowerCurve).ToArray();

        Assert.Equal(15, powerRecords.Length);
        Assert.Equal(RecordCatalog.PowerTargets.Select(target => target.Key), powerRecords.Select(record => record.Key));
        Assert.All(powerRecords, record => Assert.Equal(225, record.Value, precision: 6));
    }

    [Fact]
    public async Task All_training_and_outdoor_scopes_choose_complete_independent_winners()
    {
        var setup = await Setup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync("Mixed training athlete");
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var indoorPower = Enumerable.Range(0, 6)
            .Select(index => Point(start.AddSeconds(index), power: 350))
            .ToArray();
        var outdoorPower = Enumerable.Range(0, 6)
            .Select(index => Point(start.AddSeconds(index), latitude: 0, longitude: index * 0.00001, power: 250))
            .ToArray();
        var indoorPowerId = await setup.AddActivityAsync(ownerId, SportKind.Cycling, indoorPower, "Indoor power", isIndoor: true);
        var outdoorPowerId = await setup.AddActivityAsync(ownerId, SportKind.Cycling, outdoorPower, "Outdoor power", isIndoor: false);
        Guid indoorSummaryId;
        Guid outdoorSummaryId;
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var indoorSummary = new Activity
            {
                OwnerId = ownerId,
                Sport = SportKind.Cycling,
                Title = "Indoor long ride",
                NaturalFingerprint = Guid.NewGuid().ToString("N"),
                StartTimeUtc = start,
                DistanceMeters = 20_000,
                MovingTimeSeconds = 1_000,
                ElapsedTimeSeconds = 1_000,
                IsIndoor = true
            };
            var outdoorSummary = new Activity
            {
                OwnerId = ownerId,
                Sport = SportKind.Cycling,
                Title = "Outdoor long ride",
                NaturalFingerprint = Guid.NewGuid().ToString("N"),
                StartTimeUtc = start.AddDays(1),
                DistanceMeters = 15_000,
                MovingTimeSeconds = 1_200,
                ElapsedTimeSeconds = 1_200,
                IsIndoor = false
            };
            db.Activities.AddRange(indoorSummary, outdoorSummary);
            await db.SaveChangesAsync();
            indoorSummaryId = indoorSummary.Id;
            outdoorSummaryId = outdoorSummary.Id;
        }

        var statistics = new StatisticsService(setup.Factory);
        await statistics.RecomputeAsync(ownerId);
        var all = await statistics.GetRecordsAsync(ownerId, RecordScope.All);
        var outdoor = await statistics.GetRecordsAsync(ownerId, RecordScope.Outdoor);

        Assert.Equal(indoorSummaryId, Assert.Single(all, record => record.Kind == RecordKind.Distance).ActivityId);
        Assert.Equal(outdoorSummaryId, Assert.Single(outdoor, record => record.Kind == RecordKind.Distance).ActivityId);
        Assert.Equal(indoorPowerId, Assert.Single(all, record => record.Kind == RecordKind.PowerCurve && record.Key == "5 s").ActivityId);
        Assert.Equal(outdoorPowerId, Assert.Single(outdoor, record => record.Kind == RecordKind.PowerCurve && record.Key == "5 s").ActivityId);
        Assert.DoesNotContain(all, record => record.Kind == RecordKind.DistanceEffort && record.ActivityId == indoorPowerId);
        await using var verification = await setup.Factory.CreateDbContextAsync();
        Assert.Contains(await verification.StatisticSnapshots.ToArrayAsync(), snapshot => snapshot.Scope == RecordScope.All);
        Assert.Contains(await verification.StatisticSnapshots.ToArrayAsync(), snapshot => snapshot.Scope == RecordScope.Outdoor);
    }
    [Fact]
    public async Task Recorded_stream_power_is_used_even_when_the_summary_flag_is_stale()
    {
        var setup = await Setup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync("Power athlete");
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var points = Enumerable.Range(0, 6)
            .Select(index => Point(start.AddSeconds(index), power: 300))
            .ToArray();
        var activityId = await setup.AddActivityAsync(ownerId, SportKind.Cycling, points, "Power ride");
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            await db.Activities.Where(activity => activity.Id == activityId)
                .ExecuteUpdateAsync(update => update.SetProperty(activity => activity.HasPower, false));
        }
        var statistics = new StatisticsService(setup.Factory);

        await statistics.RecomputeAsync(ownerId);
        var records = await statistics.GetRecordsAsync(ownerId);

        Assert.Contains(records, record => record.Kind == RecordKind.PowerCurve && record.Key == "5 s");
    }

    [Fact]
    public async Task Statistics_repair_finishes_when_an_owner_has_no_qualifying_record()
    {
        var setup = await Setup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync("No-record athlete");
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.Add(new Activity
            {
                OwnerId = ownerId,
                Sport = SportKind.Walking,
                Title = "Empty activity",
                NaturalFingerprint = Guid.NewGuid().ToString("N"),
                StartTimeUtc = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero),
                DistanceMeters = 0,
                MovingTimeSeconds = 0,
                ElapsedTimeSeconds = 0,
                HasGps = false,
                HasPower = false
            });
            await db.SaveChangesAsync();
        }
        var statistics = new StatisticsService(setup.Factory);
        var worker = new StatisticsRepairWorker(setup.Factory, statistics, NullLogger<StatisticsRepairWorker>.Instance);

        await worker.RunOnceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        await using var verification = await setup.Factory.CreateDbContextAsync();
        Assert.Empty(await verification.StatisticSnapshots.Where(snapshot => snapshot.OwnerId == ownerId).ToListAsync());
    }

    [Fact]
    public async Task Recompute_and_queries_keep_owner_records_isolated()
    {
        var setup = await Setup.CreateAsync();
        var firstOwner = await setup.AddOwnerAsync("First power athlete");
        var secondOwner = await setup.AddOwnerAsync("Second power athlete");
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var firstPoints = Enumerable.Range(0, 6)
            .Select(index => Point(start.AddSeconds(index), power: 300))
            .ToArray();
        var secondPoints = Enumerable.Range(0, 6)
            .Select(index => Point(start.AddSeconds(index), power: 200))
            .ToArray();
        await setup.AddActivityAsync(firstOwner, SportKind.Cycling, firstPoints, "First ride");
        await setup.AddActivityAsync(secondOwner, SportKind.Cycling, secondPoints, "Second ride");
        var statistics = new StatisticsService(setup.Factory);

        await statistics.RecomputeAsync(firstOwner);
        await statistics.RecomputeAsync(secondOwner);
        var firstRecords = await statistics.GetRecordsAsync(firstOwner);
        var secondRecords = await statistics.GetRecordsAsync(secondOwner);
        var combinedRecords = await statistics.GetRecordsAsync(ownerId: null);

        Assert.NotEmpty(firstRecords);
        Assert.All(firstRecords, record => Assert.Equal(firstOwner, record.OwnerId));
        Assert.NotEmpty(secondRecords);
        Assert.All(secondRecords, record => Assert.Equal(secondOwner, record.OwnerId));
        Assert.Equal(2, combinedRecords.Count(record => record.Kind == RecordKind.PowerCurve && record.Key == "5 s"));
    }

    [Fact]
    public async Task Only_achieved_distance_targets_are_stored_and_fastest_activity_wins()
    {
        var setup = await Setup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync("Cyclist");
        var slowerId = await setup.AddActivityAsync(ownerId, SportKind.Cycling, GpsTrack(601, 2), "Slower ride");
        var fasterId = await setup.AddActivityAsync(ownerId, SportKind.Cycling, GpsTrack(601, 1), "Faster ride");
        var statistics = new StatisticsService(setup.Factory);

        await statistics.RecomputeAsync(ownerId);
        var records = await statistics.GetRecordsAsync(ownerId);
        var distanceRecords = records.Where(record => record.Kind == RecordKind.DistanceEffort).ToArray();

        Assert.Equal(["5 km"], distanceRecords.Select(record => record.Key));
        Assert.DoesNotContain(distanceRecords, record => record.Key == "5 miles");
        Assert.All(distanceRecords, record => Assert.Equal(fasterId, record.ActivityId));
        Assert.DoesNotContain(distanceRecords, record => record.ActivityId == slowerId);
    }

    [Fact]
    public async Task Two_hundred_kilometer_effort_is_stored_when_achieved()
    {
        var setup = await Setup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync("Long-distance cyclist");
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var points = new[]
        {
            Point(start, latitude: 0, longitude: 0, distance: 0),
            Point(start.AddHours(4), latitude: 0, longitude: 1.8, distance: 200_000)
        };
        var activityId = await setup.AddActivityAsync(ownerId, SportKind.Cycling, points, "200 km ride");
        var statistics = new StatisticsService(setup.Factory);

        await statistics.RecomputeAsync(ownerId);
        var records = await statistics.GetRecordsAsync(ownerId);

        var effort = Assert.Single(records, record => record.Kind == RecordKind.DistanceEffort && record.Key == "200 km");
        Assert.Equal(activityId, effort.ActivityId);
        Assert.Equal(TimeSpan.FromHours(4).TotalSeconds, effort.Value, precision: 6);
    }

    [Fact]
    public async Task Statistics_repair_restores_missing_scopes_and_is_idempotent()
    {
        var setup = await Setup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync("Repair athlete");
        await setup.AddActivityAsync(ownerId, SportKind.Cycling, GpsTrack(601, 1), "Ride");
        var statistics = new StatisticsService(setup.Factory);
        var worker = new StatisticsRepairWorker(setup.Factory, statistics, NullLogger<StatisticsRepairWorker>.Instance);

        await worker.RunOnceAsync();
        await using var firstDb = await setup.Factory.CreateDbContextAsync();
        var first = await firstDb.StatisticSnapshots
            .Where(snapshot => snapshot.OwnerId == ownerId)
            .OrderBy(snapshot => snapshot.Scope)
            .ThenBy(snapshot => snapshot.Kind)
            .ThenBy(snapshot => snapshot.Key)
            .Select(snapshot => new { snapshot.Scope, snapshot.Kind, snapshot.Key, snapshot.Value })
            .ToArrayAsync();
        Assert.Contains(first, snapshot => snapshot.Scope == RecordScope.All);
        Assert.Contains(first, snapshot => snapshot.Scope == RecordScope.Outdoor);

        await worker.RunOnceAsync();
        await using var secondDb = await setup.Factory.CreateDbContextAsync();
        var second = await secondDb.StatisticSnapshots
            .Where(snapshot => snapshot.OwnerId == ownerId)
            .OrderBy(snapshot => snapshot.Scope)
            .ThenBy(snapshot => snapshot.Kind)
            .ThenBy(snapshot => snapshot.Key)
            .Select(snapshot => new { snapshot.Scope, snapshot.Kind, snapshot.Key, snapshot.Value })
            .ToArrayAsync();

        Assert.Equal(first, second);
    }

    private static TrackPoint Point(
        DateTimeOffset timestamp,
        double? latitude = null,
        double? longitude = null,
        double? distance = null,
        double? power = null) =>
        new(timestamp, latitude, longitude, distance, null, null, null, null, power, null);

    private static IReadOnlyList<TrackPoint> GpsTrack(int count, int secondsPerPoint)
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, count)
            .Select(index => new TrackPoint(
                start.AddSeconds(index * secondsPerPoint),
                0,
                index * 0.0001,
                index * 11.1195,
                null,
                null,
                null,
                null,
                null,
                null))
            .ToArray();
    }

    private sealed class Setup(TestDbFactory factory)
    {
        public TestDbFactory Factory { get; } = factory;

        public static async Task<Setup> CreateAsync()
        {
            var directory = TestSupport.NewDirectory();
            var options = new DbContextOptionsBuilder<ExplorerDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "statistics.db")}")
                .Options;
            var factory = new TestDbFactory(options);
            await using var db = await factory.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            return new Setup(factory);
        }

        public async Task<Guid> AddOwnerAsync(string name)
        {
            await using var db = await Factory.CreateDbContextAsync();
            var owner = new OwnerProfile { DisplayName = name };
            db.Owners.Add(owner);
            await db.SaveChangesAsync();
            return owner.Id;
        }

        public async Task<Guid> AddActivityAsync(
            Guid ownerId,
            SportKind sport,
            IReadOnlyList<TrackPoint> points,
            string title,
            bool? isIndoor = null)
        {
            var bounds = GeometryCodec.Bounds(points);
            var distance = GeometryCodec.DistanceMeters(points);
            var hasGps = points.Any(point => point.Latitude.HasValue && point.Longitude.HasValue);
            var activity = new Activity
            {
                OwnerId = ownerId,
                Sport = sport,
                Title = title,
                NaturalFingerprint = Guid.NewGuid().ToString("N"),
                StartTimeUtc = points[0].Timestamp!.Value,
                DistanceMeters = distance,
                MovingTimeSeconds = (points[^1].Timestamp!.Value - points[0].Timestamp!.Value).TotalSeconds,
                ElapsedTimeSeconds = (points[^1].Timestamp!.Value - points[0].Timestamp!.Value).TotalSeconds,
                HasGps = hasGps,
                IsIndoor = isIndoor ?? !hasGps,
                HasPower = points.Any(point => point.PowerWatts.HasValue),
                MinLatitude = bounds.MinLat,
                MinLongitude = bounds.MinLon,
                MaxLatitude = bounds.MaxLat,
                MaxLongitude = bounds.MaxLon,
                GeometryWkb = GeometryCodec.ToWkb(points),
                Stream = new ActivityStream
                {
                    OwnerId = ownerId,
                    SchemaVersion = 1,
                    PointCount = points.Count,
                    CompressedPayload = TrackCodec.Encode(points)
                }
            };
            await using var db = await Factory.CreateDbContextAsync();
            db.Activities.Add(activity);
            await db.SaveChangesAsync();
            return activity.Id;
        }
    }

    private sealed class TestDbFactory(DbContextOptions<ExplorerDbContext> options) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);
        public Task<ExplorerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExplorerDbContext(options));
    }
}
