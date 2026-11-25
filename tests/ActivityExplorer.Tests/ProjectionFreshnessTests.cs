using System.Data.Common;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActivityExplorer.Tests;

public sealed class ProjectionFreshnessTests
{
    [Fact]
    public async Task Upgrade_marks_legacy_projections_pending_once_and_repair_keeps_them_current_after_restart()
    {
        await using var fixture = await Fixture.CreateAsync(installProjectors: false);
        var owner = await fixture.AddOwnerAsync("Legacy profile");
        var activity = await fixture.AddActivityAsync(owner, 1_000);
        await fixture.Statistics.RecomputeAsync(owner);
        var segment = await fixture.AddSegmentAsync(owner);
        var effort = Assert.Single((await fixture.Segments.GetAsync(segment))!.Efforts);

        // Reproduce the pre-upgrade schema while retaining the source inputs and persisted results.
        await using (var db = fixture.Factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("DROP TABLE SegmentProjectionVersions; DROP TABLE OwnerProjectionVersions;");
        await fixture.InstallProjectorsAsync();

        var recordRead = await fixture.ReadRecordsAsync(owner);
        Assert.False(recordRead.Available);
        Assert.Empty(recordRead.Overview.Records);
        var dashboard = await new ActivityReadService(fixture.Factory).GetDashboardAsync(new(OwnerId: owner));
        Assert.True(dashboard.HighlightsPending);
        Assert.Empty(dashboard.Highlights);
        var pending = (await fixture.Segments.GetAsync(segment))!;
        Assert.True(pending.Summary.EffortsPending);
        Assert.Empty(pending.Efforts);
        await Assert.ThrowsAsync<ProjectionPendingException>(() => fixture.SegmentReader.GetEffortsAsync(owner, segment, new(), new()));
        await using (var db = fixture.Factory.CreateDbContext())
        {
            Assert.True(await db.StatisticSnapshots.AnyAsync(record => record.ActivityId == activity));
            Assert.True(await db.SegmentEfforts.AnyAsync(row => row.Id == effort.Id));
        }

        await fixture.NewWorker().RunOnceAsync();
        var current = await fixture.ReadRecordsAsync(owner);
        Assert.True(current.Available);
        Assert.Contains(current.Overview.Records, record => record.ActivityId == activity);
        Assert.Equal(effort.Id, Assert.Single((await fixture.Segments.GetAsync(segment))!.Efforts).Id);

        await fixture.InstallProjectorsAsync();
        await fixture.NewWorker().RunOnceAsync();
        Assert.Equal(current.Overview.Records, (await fixture.ReadRecordsAsync(owner)).Overview.Records);
        Assert.False((await fixture.SegmentReader.GetAsync(owner, segment))!.Summary.EffortsPending);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_new_committed_input_fences_in_flight_projection_publication(bool records)
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.AddOwnerAsync("Concurrent profile");
        var first = await fixture.AddActivityAsync(owner, 1_000);
        await fixture.Statistics.RecomputeAsync(owner);
        var segment = await fixture.AddSegmentAsync(owner);
        var pause = new PauseActivityRead();
        var readingFactory = fixture.CreateFactory(pause);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var computation = records
            ? new StatisticsService(readingFactory).RecomputeAsync(owner, deadline.Token)
            : new SegmentService(readingFactory, new SegmentMatcher(), fixture.MutationLock).RecomputeAsync(segment, deadline.Token);

        Guid second;
        try
        {
            await pause.Paused.Task.WaitAsync(deadline.Token);
            second = await fixture.AddActivityAsync(owner, 2_000);
        }
        finally
        {
            pause.Resume.TrySetResult();
            await computation.WaitAsync(deadline.Token);
        }

        Assert.False((await fixture.ReadRecordsAsync(owner)).Available);
        Assert.True((await fixture.SegmentReader.GetAsync(owner, segment))!.Summary.EffortsPending);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            Assert.All(await db.StatisticSnapshots.ToArrayAsync(), record => Assert.Equal(first, record.ActivityId));
            Assert.DoesNotContain(await db.SegmentEfforts.ToArrayAsync(), effort => effort.ActivityId == second);
        }

        await fixture.NewWorker().RunOnceAsync();
        var recordsAfterRepair = await fixture.ReadRecordsAsync(owner);
        Assert.True(recordsAfterRepair.Available);
        Assert.Equal(second, Assert.Single(recordsAfterRepair.Overview.Records, record => record.Kind == RecordKind.Distance).ActivityId);
        var effortsAfterRepair = await fixture.SegmentReader.GetEffortsAsync(owner, segment, new(), new());
        Assert.Contains(effortsAfterRepair!.Items, effort => effort.ActivityId == second);
    }

    [Fact]
    public async Task Transfer_invalidates_both_profiles_and_repair_failures_are_isolated_and_retryable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.AddOwnerAsync("First profile");
        var second = await fixture.AddOwnerAsync("Second profile");
        var transfer = await fixture.AddActivityAsync(first, 1_000);
        var corrupted = await fixture.AddActivityAsync(first, 800);
        var firstSegment = await fixture.AddSegmentAsync(first);
        var secondSegment = await fixture.AddSegmentAsync(second);
        await fixture.NewWorker().RunOnceAsync();

        byte[] intact;
        await using (var db = fixture.Factory.CreateDbContext())
        {
            intact = await db.ActivityStreams.Where(stream => stream.ActivityId == corrupted)
                .Select(stream => stream.CompressedPayload).SingleAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Activities.Where(activity => activity.Id == transfer)
                .ExecuteUpdateAsync(setters => setters.SetProperty(activity => activity.OwnerId, second));
            await db.ActivityStreams.Where(stream => stream.ActivityId == transfer)
                .ExecuteUpdateAsync(setters => setters.SetProperty(stream => stream.OwnerId, second));
            await db.ActivityStreams.Where(stream => stream.ActivityId == corrupted)
                .ExecuteUpdateAsync(setters => setters.SetProperty(stream => stream.CompressedPayload, new byte[] { 0, 1 }));
            Assert.Equal(2, await ProjectionVersions.PendingRecords(db).CountAsync());
            await transaction.CommitAsync();
        }

        Assert.False((await fixture.ReadRecordsAsync(first)).Available);
        Assert.False((await fixture.ReadRecordsAsync(second)).Available);
        Assert.True((await fixture.SegmentReader.GetAsync(first, firstSegment))!.Summary.EffortsPending);
        Assert.True((await fixture.SegmentReader.GetAsync(second, secondSegment))!.Summary.EffortsPending);
        using (var cancellation = new CancellationTokenSource())
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.NewWorker().RunOnceAsync(cancellation.Token));
        }

        await fixture.NewWorker().RunOnceAsync();
        Assert.False((await fixture.ReadRecordsAsync(first)).Available);
        Assert.True((await fixture.SegmentReader.GetAsync(first, firstSegment))!.Summary.EffortsPending);
        Assert.True((await fixture.ReadRecordsAsync(second)).Available);
        Assert.False((await fixture.SegmentReader.GetAsync(second, secondSegment))!.Summary.EffortsPending);

        await using (var db = fixture.Factory.CreateDbContext())
            await db.ActivityStreams.Where(stream => stream.ActivityId == corrupted)
                .ExecuteUpdateAsync(setters => setters.SetProperty(stream => stream.CompressedPayload, intact));
        await fixture.NewWorker().RunOnceAsync();
        Assert.True((await fixture.ReadRecordsAsync(first)).Available);
        var firstEfforts = (await fixture.SegmentReader.GetEffortsAsync(first, firstSegment, new(), new()))!.Items;
        Assert.Equal(corrupted, Assert.Single(firstEfforts).ActivityId);
        var secondEfforts = (await fixture.SegmentReader.GetEffortsAsync(second, secondSegment, new(), new()))!.Items;
        Assert.Equal(transfer, Assert.Single(secondEfforts).ActivityId);
    }

    private sealed class PauseActivityRead : DbCommandInterceptor
    {
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Activities\"", StringComparison.Ordinal) &&
                command.CommandText.Contains("JOIN \"ActivityStreams\"", StringComparison.Ordinal) &&
                Paused.TrySetResult())
                await Resume.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly IReadOnlyList<TrackPoint> Points = TestSupport.Track();
        private readonly string _directory;
        private readonly string _connectionString;
        public FactoryImpl Factory { get; }
        public OwnerMutationLock MutationLock { get; } = new();
        public StatisticsService Statistics => new(Factory);
        public SegmentService Segments => new(Factory, new SegmentMatcher(), MutationLock);
        public SegmentReadService SegmentReader => new(Factory);

        private Fixture(string directory, string connectionString)
        {
            _directory = directory;
            _connectionString = connectionString;
            Factory = new(connectionString);
        }

        public static async Task<Fixture> CreateAsync(bool installProjectors = true)
        {
            var directory = TestSupport.NewDirectory();
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "projections.db"),
                Pooling = false,
                ForeignKeys = true,
                DefaultTimeout = 5
            }.ToString();
            var fixture = new Fixture(directory, connectionString);
            await using (var db = fixture.Factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL");
            }
            if (installProjectors) await fixture.InstallProjectorsAsync();
            return fixture;
        }

        public async Task InstallProjectorsAsync()
        {
            await using var db = Factory.CreateDbContext();
            await ProjectionVersions.EnsureAsync(db, CancellationToken.None);
        }

        public FactoryImpl CreateFactory(DbCommandInterceptor interceptor) => new(_connectionString, interceptor);

        public async Task<Guid> AddOwnerAsync(string name)
        {
            await using var db = Factory.CreateDbContext();
            var owner = new OwnerProfile { DisplayName = name, TimeZoneId = "UTC" };
            db.Owners.Add(owner);
            await db.SaveChangesAsync();
            return owner.Id;
        }

        public async Task<Guid> AddActivityAsync(Guid owner, double distance)
        {
            await using var db = Factory.CreateDbContext();
            var bounds = GeometryCodec.Bounds(Points);
            var activity = new Core.Domain.Activity
            {
                OwnerId = owner,
                Title = "Test activity",
                Sport = SportKind.Cycling,
                NaturalFingerprint = Guid.NewGuid().ToString("N"),
                StartTimeUtc = Points[0].Timestamp!.Value,
                MovingTimeSeconds = 29,
                ElapsedTimeSeconds = 29,
                DistanceMeters = distance,
                HasGps = true,
                MinLatitude = bounds.MinLat,
                MinLongitude = bounds.MinLon,
                MaxLatitude = bounds.MaxLat,
                MaxLongitude = bounds.MaxLon,
                Stream = new ActivityStream { OwnerId = owner, CompressedPayload = TrackCodec.Encode(Points), PointCount = Points.Count }
            };
            db.Activities.Add(activity);
            await db.SaveChangesAsync();
            return activity.Id;
        }

        public Task<Guid> AddSegmentAsync(Guid owner) =>
            Segments.CreateAsync(new(owner, "Test segment", SportKind.Cycling, Points.Skip(5).Take(20).ToArray()));

        public StatisticsRepairWorker NewWorker() => new(Factory, Statistics, NullLogger<StatisticsRepairWorker>.Instance, Segments);

        public Task<ReadRecordOverview> ReadRecordsAsync(Guid owner) =>
            new StatisticsReadService(Factory).ReadOverviewAsync(new(owner, SportKind.Cycling));

        public ValueTask DisposeAsync()
        {
            MutationLock.Dispose();
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FactoryImpl(string connectionString, DbCommandInterceptor? interceptor = null)
        : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite(connectionString);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new ExplorerDbContext(options.Options);
        }
    }
}
