using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActivityExplorer.Tests;

public sealed class TimedDistanceStatisticsTests
{
    [Fact]
    public void Record_catalog_has_the_expected_timed_distance_targets_and_order()
    {
        Assert.Equal(7, (int)RecordKind.TimedDistanceEffort);
        Assert.Equal(7, RecordCatalog.ComputationVersion);
        Assert.Equal(
            [
                ("5 min", 300d, 0),
                ("10 min", 600d, 1),
                ("20 min", 1_200d, 2),
                ("30 min", 1_800d, 3),
                ("1 hour", 3_600d, 4),
                ("2 hours", 7_200d, 5),
                ("4 hours", 14_400d, 6)
            ],
            RecordCatalog.TimedDistanceTargets(SportKind.Cycling)
                .Select(target => (target.Key, target.Target, target.DisplayOrder)));

        (string, double, int)[] runningAndWalking =
        [
            ("5 min", 300, 0),
            ("10 min", 600, 1),
            ("15 min", 900, 2),
            ("30 min", 1_800, 3),
            ("1 hour", 3_600, 4),
            ("2 hours", 7_200, 5)
        ];
        Assert.Equal(
            runningAndWalking,
            RecordCatalog.TimedDistanceTargets(SportKind.Running)
                .Select(target => (target.Key, target.Target, target.DisplayOrder)));
        Assert.Equal(
            runningAndWalking,
            RecordCatalog.TimedDistanceTargets(SportKind.Walking)
                .Select(target => (target.Key, target.Target, target.DisplayOrder)));
        Assert.Empty(RecordCatalog.TimedDistanceTargets((SportKind)int.MaxValue));
        Assert.True(
            RecordCatalog.CategoryOrder(RecordKind.DistanceEffort) <
            RecordCatalog.CategoryOrder(RecordKind.TimedDistanceEffort));
        Assert.True(
            RecordCatalog.CategoryOrder(RecordKind.TimedDistanceEffort) <
            RecordCatalog.CategoryOrder(RecordKind.PowerCurve));
    }

    [Fact]
    public async Task Timed_distance_records_store_only_achieved_targets_and_choose_independent_winners()
    {
        var setup = await Setup.CreateAsync();
        var firstOwner = await setup.AddOwnerAsync("First cyclist");
        var secondOwner = await setup.AddOwnerAsync("Second cyclist");
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        await setup.AddActivityAsync(
            firstOwner,
            SportKind.Cycling,
            DistanceTrack(start, 300, 1_000, withGps: true),
            "Slower ride",
            isIndoor: false);
        var fasterId = await setup.AddActivityAsync(
            firstOwner,
            SportKind.Cycling,
            DistanceTrack(start.AddDays(1), 300, 1_200, withGps: true),
            "Faster ride",
            isIndoor: false);
        await setup.AddActivityAsync(
            secondOwner,
            SportKind.Cycling,
            DistanceTrack(start, 300, 1_500, withGps: true),
            "Other owner's ride",
            isIndoor: false);
        var statistics = new StatisticsService(setup.Factory);

        await statistics.RecomputeAsync(firstOwner);
        await statistics.RecomputeAsync(secondOwner);
        var all = await statistics.GetRecordsAsync(firstOwner, RecordScope.All);
        var outdoor = await statistics.GetRecordsAsync(firstOwner, RecordScope.Outdoor);
        var combined = await statistics.GetRecordsAsync(ownerId: null, RecordScope.All);

        var allTimed = Assert.Single(all, record => record.Kind == RecordKind.TimedDistanceEffort);
        var outdoorTimed = Assert.Single(outdoor, record => record.Kind == RecordKind.TimedDistanceEffort);
        Assert.Equal("5 min", allTimed.Key);
        Assert.Equal(1_200, allTimed.Value, precision: 6);
        Assert.Equal(100, allTimed.CoveragePercent);
        Assert.Equal(fasterId, allTimed.ActivityId);
        Assert.Equal(fasterId, outdoorTimed.ActivityId);
        Assert.DoesNotContain(all, record =>
            record.Kind == RecordKind.TimedDistanceEffort && record.Key == "10 min");
        Assert.Equal(
            2,
            combined.Count(record => record.Kind == RecordKind.TimedDistanceEffort && record.Key == "5 min"));
    }

    [Fact]
    public async Task Indoor_recorded_distance_qualifies_for_all_and_indoor_timed_distance_records()
    {
        var setup = await Setup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync("Indoor cyclist");
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var activityId = await setup.AddActivityAsync(
            ownerId,
            SportKind.Cycling,
            DistanceTrack(start, 600, 6_000, withGps: false),
            "Indoor ride",
            isIndoor: true);
        var statistics = new StatisticsService(setup.Factory);

        await statistics.RecomputeAsync(ownerId);
        var all = await statistics.GetRecordsAsync(ownerId, RecordScope.All);
        var outdoor = await statistics.GetRecordsAsync(ownerId, RecordScope.Outdoor);

        var timed = all.Where(record => record.Kind == RecordKind.TimedDistanceEffort).ToArray();
        Assert.Equal(["5 min", "10 min"], timed.Select(record => record.Key));
        Assert.Equal([3_000d, 6_000d], timed.Select(record => record.Value));
        Assert.All(timed, record => Assert.Equal(activityId, record.ActivityId));
        Assert.All(timed, record => Assert.Equal(100, record.CoveragePercent));
        Assert.DoesNotContain(all, record => record.Kind == RecordKind.DistanceEffort);
        Assert.Empty(outdoor);
        var indoor = await statistics.GetRecordsAsync(ownerId, RecordScope.Indoor);
        Assert.Equal(all.Select(record => (record.Key, record.Value)), indoor.Select(record => (record.Key, record.Value)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Repair_replaces_complete_stale_snapshots_and_is_idempotent(int staleVersion)
    {
        var setup = await Setup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync("Upgrade cyclist");
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var activityId = await setup.AddActivityAsync(
            ownerId,
            SportKind.Cycling,
            DistanceTrack(start, 300, 1_200, withGps: true),
            "Upgrade ride",
            isIndoor: false);
        Guid legacyAllId;
        Guid legacyOutdoorId;
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var legacyAll = Snapshot(ownerId, activityId, RecordScope.All, staleVersion);
            var legacyOutdoor = Snapshot(ownerId, activityId, RecordScope.Outdoor, staleVersion);
            db.StatisticSnapshots.AddRange(legacyAll, legacyOutdoor);
            await db.SaveChangesAsync();
            legacyAllId = legacyAll.Id;
            legacyOutdoorId = legacyOutdoor.Id;
        }

        var statistics = new StatisticsService(setup.Factory);
        var worker = new StatisticsRepairWorker(
            setup.Factory,
            statistics,
            NullLogger<StatisticsRepairWorker>.Instance);

        await worker.RunOnceAsync();
        await using var firstDb = await setup.Factory.CreateDbContextAsync();
        var first = await firstDb.StatisticSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.OwnerId == ownerId)
            .OrderBy(snapshot => snapshot.Scope)
            .ThenBy(snapshot => snapshot.Kind)
            .ThenBy(snapshot => snapshot.Key)
            .Select(snapshot => new
            {
                snapshot.Id,
                snapshot.Scope,
                snapshot.Kind,
                snapshot.Key,
                snapshot.Value,
                snapshot.ComputationVersion
            })
            .ToArrayAsync();
        Assert.DoesNotContain(first, snapshot => snapshot.Id == legacyAllId || snapshot.Id == legacyOutdoorId);
        Assert.All(first, snapshot => Assert.Equal(RecordCatalog.ComputationVersion, snapshot.ComputationVersion));
        Assert.Contains(first, snapshot =>
            snapshot.Scope == RecordScope.All &&
            snapshot.Kind == RecordKind.TimedDistanceEffort &&
            snapshot.Key == "5 min");
        Assert.Contains(first, snapshot =>
            snapshot.Scope == RecordScope.Outdoor &&
            snapshot.Kind == RecordKind.TimedDistanceEffort &&
            snapshot.Key == "5 min");

        await worker.RunOnceAsync();
        await using var secondDb = await setup.Factory.CreateDbContextAsync();
        var second = await secondDb.StatisticSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.OwnerId == ownerId)
            .OrderBy(snapshot => snapshot.Scope)
            .ThenBy(snapshot => snapshot.Kind)
            .ThenBy(snapshot => snapshot.Key)
            .Select(snapshot => new
            {
                snapshot.Id,
                snapshot.Scope,
                snapshot.Kind,
                snapshot.Key,
                snapshot.Value,
                snapshot.ComputationVersion
            })
            .ToArrayAsync();

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Repair_does_not_fill_missing_scope_or_recompute_future_version_snapshots()
    {
        var setup = await Setup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync("Future-version cyclist");
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var activityId = await setup.AddActivityAsync(
            ownerId,
            SportKind.Cycling,
            DistanceTrack(start, 300, 1_200, withGps: true),
            "Future-version ride",
            isIndoor: false);
        var futureVersion = RecordCatalog.ComputationVersion + 1;
        Guid futureAllId;
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var futureAll = Snapshot(ownerId, activityId, RecordScope.All, futureVersion);
            db.StatisticSnapshots.Add(futureAll);
            await db.SaveChangesAsync();
            futureAllId = futureAll.Id;
        }

        var statistics = new StatisticsService(setup.Factory);
        var worker = new StatisticsRepairWorker(
            setup.Factory,
            statistics,
            NullLogger<StatisticsRepairWorker>.Instance);

        await worker.RunOnceAsync();
        await using var resultDb = await setup.Factory.CreateDbContextAsync();
        var snapshots = await resultDb.StatisticSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.OwnerId == ownerId)
            .OrderBy(snapshot => snapshot.Scope)
            .ToArrayAsync();

        var snapshot = Assert.Single(snapshots);
        Assert.Equal(futureAllId, snapshot.Id);
        Assert.Equal(RecordScope.All, snapshot.Scope);
        Assert.Equal(futureVersion, snapshot.ComputationVersion);
        Assert.Equal(RecordKind.Distance, snapshot.Kind);
        Assert.DoesNotContain(snapshots, snapshot => snapshot.Kind == RecordKind.TimedDistanceEffort);
    }

    private static StatisticSnapshot Snapshot(
        Guid ownerId,
        Guid activityId,
        RecordScope scope,
        int computationVersion) => new()
        {
            OwnerId = ownerId,
            Sport = SportKind.Cycling,
            Kind = RecordKind.Distance,
            Scope = scope,
            Key = "Longest distance",
            Value = 1_200,
            ActivityId = activityId,
            CoveragePercent = 100,
            ComputationVersion = computationVersion
        };

    private static IReadOnlyList<TrackPoint> DistanceTrack(
        DateTimeOffset start,
        int durationSeconds,
        double distanceMeters,
        bool withGps) =>
    [
        new TrackPoint(start, withGps ? 0 : null, withGps ? 0 : null, 0, null, null, null, null, null, null),
        new TrackPoint(
            start.AddSeconds(durationSeconds),
            withGps ? 0 : null,
            withGps ? 0.01 : null,
            distanceMeters,
            null,
            null,
            null,
            null,
            null,
            null)
    ];

    private sealed class Setup(TestDbFactory factory)
    {
        public TestDbFactory Factory { get; } = factory;

        public static async Task<Setup> CreateAsync()
        {
            var directory = TestSupport.NewDirectory();
            var options = new DbContextOptionsBuilder<ExplorerDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "timed-distance-statistics.db")}")
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
            bool isIndoor)
        {
            var duration = (points[^1].Timestamp!.Value - points[0].Timestamp!.Value).TotalSeconds;
            var activity = new Activity
            {
                OwnerId = ownerId,
                Sport = sport,
                Title = title,
                NaturalFingerprint = Guid.NewGuid().ToString("N"),
                StartTimeUtc = points[0].Timestamp!.Value,
                DistanceMeters = points[^1].DistanceMeters!.Value - points[0].DistanceMeters!.Value,
                MovingTimeSeconds = duration,
                ElapsedTimeSeconds = duration,
                HasGps = points.Any(point => point.Latitude.HasValue && point.Longitude.HasValue),
                IsIndoor = isIndoor,
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

    private sealed class TestDbFactory(DbContextOptions<ExplorerDbContext> options)
        : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);

        public Task<ExplorerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExplorerDbContext(options));
    }
}
