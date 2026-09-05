using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActivityExplorer.Tests;

public sealed class RowingLifecycleTests
{
    [Fact]
    public async Task Imports_deduplicate_and_transfer_and_delete_refresh_all_three_scopes()
    {
        await using var setup = await Setup.CreateAsync();
        var indoorOwner = await setup.AddOwnerAsync("Indoor rower");
        var otherOwner = await setup.AddOwnerAsync("Other rower");
        var input = TestSupport.RowingFit(TestSupport.NewDirectory());
        await setup.ImportAsync(indoorOwner, input);
        await setup.ImportAsync(indoorOwner, input);
        var activities = setup.Services.GetRequiredService<IActivityQueryService>();
        var statistics = setup.Services.GetRequiredService<IStatisticsService>();
        var listed = await activities.SearchAsync(new ActivityFilter(OwnerId: indoorOwner, Sport: SportKind.Rowing));
        var summary = Assert.Single(listed.Items);
        Assert.False(summary.HasGps);
        Assert.True(summary.HasPower);
        var detail = await activities.GetAsync(summary.Id);
        Assert.Single(detail!.Sources);
        Assert.Equal(2, detail.Laps.Count);
        Assert.Null(detail.Rich.PedalRevolutions);
        Assert.Contains(detail.Metrics, metric => metric.Key == "fit.total_strokes" && metric.NumericValue == 240);
        var all = await statistics.GetRecordsAsync(indoorOwner);
        var indoor = await statistics.GetRecordsAsync(indoorOwner, RecordScope.Indoor);
        Assert.Equal(all.Select(Key), indoor.Select(Key));
        Assert.Empty(await statistics.GetRecordsAsync(indoorOwner, RecordScope.Outdoor));
        Assert.Equal(["100 m", "500 m", "1 km"], all.Where(record => record.Kind == RecordKind.DistanceEffort).Select(record => record.Key));
        Assert.Equal(["1 min", "4 min"], all.Where(record => record.Kind == RecordKind.TimedDistanceEffort).Select(record => record.Key));
        Assert.DoesNotContain(all, record => record.Kind == RecordKind.Elevation);
        Assert.Contains(all, record => record.Kind == RecordKind.PowerCurve);
        Assert.Empty(await statistics.GetRecordsAsync(otherOwner, RecordScope.Indoor));

        await activities.UpdateAsync(summary.Id, new UpdateActivityRequest("Transferred row", "Notes", "Rower", otherOwner));
        foreach (var scope in Enum.GetValues<RecordScope>())
            Assert.Empty(await statistics.GetRecordsAsync(indoorOwner, scope));
        Assert.NotEmpty(await statistics.GetRecordsAsync(otherOwner, RecordScope.Indoor));
        Assert.Empty(await statistics.GetRecordsAsync(otherOwner, RecordScope.Outdoor));
        await activities.DeleteAsync([summary.Id]);
        foreach (var scope in Enum.GetValues<RecordScope>())
            Assert.Empty(await statistics.GetRecordsAsync(otherOwner, scope));
    }

    [Fact]
    public async Task Scope_winners_and_upgrade_repair_are_independent_and_idempotent()
    {
        await using var setup = await Setup.CreateAsync();
        var owner = await setup.AddOwnerAsync("Mixed rowing");
        await setup.ImportAsync(owner, TestSupport.RowingFit(TestSupport.NewDirectory()));
        var outdoorPath = TestSupport.RowingFit(TestSupport.NewDirectory(), Dynastream.Fit.Sport.Rowing, Dynastream.Fit.SubSport.Generic, withGps: true, durationSeconds: 1200);
        var renamed = Path.Combine(Path.GetDirectoryName(outdoorPath)!, "outdoor.fit");
        File.Move(outdoorPath, renamed);
        await setup.ImportAsync(owner, renamed);
        var factory = setup.Services.GetRequiredService<IDbContextFactory<ExplorerDbContext>>();
        Guid outdoorId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            outdoorId = (await db.Activities.SingleAsync(activity => !activity.IsIndoor)).Id;
        }
        var routes = setup.Services.GetRequiredService<IRouteService>();
        var routeId = await routes.CreateFromActivityAsync(new CreateRouteRequest(owner, outdoorId, "River row", null));
        Assert.Equal(SportKind.Rowing, (await routes.GetAsync(routeId))!.Summary.Sport);
        Assert.Contains("<rtept", await routes.ExportGpxAsync(routeId));
        var segments = setup.Services.GetRequiredService<ISegmentService>();
        var segmentId = await segments.CreateFromActivityAsync(new CreateSegmentRequest(owner, outdoorId, "River effort", 10, 100));
        var segment = await segments.GetAsync(segmentId);
        Assert.Equal(SportKind.Rowing, segment!.Summary.Sport);
        Assert.Equal(outdoorId, Assert.Single(segment.Efforts).ActivityId);
        var maps = setup.Services.GetRequiredService<IMapFeatureService>();
        Assert.Single((await maps.GetActivitiesAsync(new MapQuery(OwnerId: owner, Sport: SportKind.Rowing))).Features);
        var statistics = setup.Services.GetRequiredService<IStatisticsService>();
        await statistics.RecomputeAsync(owner);
        Assert.Equal(outdoorId, Assert.Single(await statistics.GetRecordsAsync(owner), record => record.Kind == RecordKind.Distance).ActivityId);
        Assert.Equal(outdoorId, Assert.Single(await statistics.GetRecordsAsync(owner, RecordScope.Outdoor), record => record.Kind == RecordKind.Distance).ActivityId);
        Assert.NotEqual(outdoorId, Assert.Single(await statistics.GetRecordsAsync(owner, RecordScope.Indoor), record => record.Kind == RecordKind.Distance).ActivityId);
        var worker = new StatisticsRepairWorker(factory, statistics, NullLogger<StatisticsRepairWorker>.Instance);
        await using (var db = await factory.CreateDbContextAsync())
            await db.StatisticSnapshots.ExecuteUpdateAsync(update => update.SetProperty(snapshot => snapshot.ComputationVersion, 6));
        await worker.RunOnceAsync();
        await using (var db = await factory.CreateDbContextAsync())
            Assert.All(await db.StatisticSnapshots.ToArrayAsync(), snapshot => Assert.Equal(7, snapshot.ComputationVersion));
        await using (var db = await factory.CreateDbContextAsync())
            await db.StatisticSnapshots.Where(snapshot => snapshot.Scope == RecordScope.Indoor).ExecuteDeleteAsync();
        await worker.RunOnceAsync();
        var repaired = await statistics.GetRecordsAsync(owner, RecordScope.Indoor);
        Assert.NotEmpty(repaired);
        await worker.RunOnceAsync();
        Assert.Equal(repaired.Select(record => record.Id), (await statistics.GetRecordsAsync(owner, RecordScope.Indoor)).Select(record => record.Id));
    }

    private static (RecordKind, string, double) Key(PersonalRecord record) => (record.Kind, record.Key, record.Value);

    private sealed class Setup(ServiceProvider services) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;

        public static async Task<Setup> CreateAsync()
        {
            var previous = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_DATA");
            try
            {
                Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", TestSupport.NewDirectory());
                var services = new ServiceCollection().AddLogging().AddActivityExplorer().BuildServiceProvider();
                await services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
                return new Setup(services);
            }
            finally { Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", previous); }
        }

        public async Task<Guid> AddOwnerAsync(string name)
        {
            await using var db = await Services.GetRequiredService<IDbContextFactory<ExplorerDbContext>>().CreateDbContextAsync();
            var owner = new OwnerProfile { DisplayName = name };
            db.Owners.Add(owner);
            await db.SaveChangesAsync();
            return owner.Id;
        }

        public async Task ImportAsync(Guid owner, string input)
        {
            var paths = Services.GetRequiredService<AppDataPaths>();
            var directory = Path.Combine(paths.StagingPath, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var staged = Path.Combine(directory, Path.GetFileName(input));
            File.Copy(input, staged);
            var id = await Services.GetRequiredService<IImportQueue>().EnqueueAsync(new ImportRequest(owner, staged, Path.GetFileName(input), SourceKind.Fit));
            await Services.GetRequiredService<IImportProcessor>().ProcessAsync(id);
            await using var db = await Services.GetRequiredService<IDbContextFactory<ExplorerDbContext>>().CreateDbContextAsync();
            Assert.Equal(ImportStatus.Completed, (await db.ImportBatches.FindAsync(id))!.Status);
        }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }
}
