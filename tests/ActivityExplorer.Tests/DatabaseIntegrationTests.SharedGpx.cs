using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Watched_folder_accepts_a_route_only_source_but_skips_an_existing_activity_source()
    {
        await using var fixture = await ImportRecoveryFixture.CreateAsync();
        var setup = fixture.Setup;
        Guid owner;
        await using (var db = await setup.Factory.CreateDbContextAsync())
            owner = await db.Owners.Select(x => x.Id).SingleAsync();
        var paths = new AppDataPaths();
        var watched = Path.Combine(setup.DataDirectory, "watched");
        Directory.CreateDirectory(watched);
        var file = TestSupport.Write(watched, "same.gpx", TestSupport.Gpx());
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));
        var hash = await Fingerprint.Sha256Async(file, CancellationToken.None);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var batch = new ImportBatch
            {
                OwnerId = owner,
                SourceKind = SourceKind.Gpx,
                Status = ImportStatus.Completed,
                DisplayName = "route",
                StagedPath = string.Empty
            };
            var route = new Route { OwnerId = owner, Name = "Route", Sport = SportKind.Running };
            db.ImportBatches.Add(batch);
            db.Routes.Add(route);
            db.SourceFiles.Add(new SourceFile
            {
                OwnerId = owner,
                ImportBatchId = batch.Id,
                RouteId = route.Id,
                SourceKind = SourceKind.Gpx,
                OriginalName = "same.gpx",
                StoredPath = "synthetic",
                Sha256 = hash
            });
            await db.SaveChangesAsync();
        }
        var queue = new RecordingWatchedQueue();
        var service = new WatchedFolderService(setup.Factory, queue, paths);
        await service.AddAsync(owner, watched);
        await service.ReconcileAsync();
        Assert.Single(queue.Requests);

        var activity = await setup.SeedActivityAsync(owner, "Imported", SportKind.Running);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var batchId = await db.ImportBatches.Select(x => x.Id).SingleAsync();
            db.SourceFiles.Add(new SourceFile
            {
                OwnerId = owner,
                ImportBatchId = batchId,
                ActivityId = activity,
                SourceKind = SourceKind.Gpx,
                OriginalName = "same.gpx",
                StoredPath = "synthetic",
                Sha256 = hash
            });
            await db.SaveChangesAsync();
        }
        await service.ReconcileAsync();
        Assert.Single(queue.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_gpx_can_be_imported_as_activity_and_independent_routes_in_either_order(bool routeFirst)
    {
        await using var fixture = await ImportRecoveryFixture.CreateAsync();
        var setup = fixture.Setup;
        Guid owner;
        await using (var db = await setup.Factory.CreateDbContextAsync())
            owner = await db.Owners.Select(x => x.Id).SingleAsync();
        var storage = CreateStorageServices(setup);
        var paths = new AppDataPaths();
        var routes = new RouteService(setup.Factory, storage.Originals, storage.FileOperations, storage.OwnerLock);
        var content = TestSupport.Gpx();

        async Task<Guid> ImportRoute(Guid routeOwner, string name)
        {
            var directory = Path.Combine(paths.StagingPath, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var staged = TestSupport.Write(directory, "same.gpx", content);
            var points = await new GpxRouteReader().ReadAsync(staged);
            var hash = await Fingerprint.Sha256Async(staged, CancellationToken.None);
            return (await routes.ImportGpxAsync(new CreateRoutePathRequest(routeOwner, name, null, SportKind.Running, points),
                staged, "same.gpx", hash, new FileInfo(staged).Length)).RouteId;
        }

        Guid firstRoute;
        var activityBatch = await fixture.AddBatchAsync("activity", content);
        if (routeFirst)
        {
            firstRoute = await ImportRoute(owner, "First route");
            await fixture.Processor.ProcessAsync(activityBatch);
        }
        else
        {
            await fixture.Processor.ProcessAsync(activityBatch);
            firstRoute = await ImportRoute(owner, "First route");
        }
        var secondRoute = await ImportRoute(owner, "Second route");

        Guid activity;
        string sharedPath;
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            Assert.Equal(ImportStatus.Completed, (await db.ImportBatches.SingleAsync(x => x.Id == activityBatch)).Status);
            activity = (await db.Activities.SingleAsync()).Id;
            var sources = await db.SourceFiles.ToArrayAsync();
            Assert.Equal(3, sources.Length);
            Assert.Equal(activityBatch, Assert.Single(sources, x => x.ActivityId == activity).ImportBatchId);
            Assert.Equal(2, sources.Count(x => x.RouteId == firstRoute || x.RouteId == secondRoute));
            Assert.Equal(3, sources.Select(x => x.ImportBatchId).Distinct().Count());
            sharedPath = Assert.Single(sources.Select(x => x.StoredPath).Distinct());
            Assert.True(File.Exists(storage.Originals.ResolveStoredPath(sharedPath)));
        }

        var activities = new ActivityQueryService(setup.Factory, new StatisticsService(setup.Factory),
            new SegmentService(setup.Factory, new SegmentMatcher(), storage.OwnerLock), storage.Originals,
            storage.FileOperations, storage.OwnerLock, NullLogger<ActivityQueryService>.Instance);
        if (routeFirst)
        {
            var destination = await setup.SeedOwnerAsync("Transfer destination");
            await ImportRoute(destination, "Destination route");
            await activities.UpdateAsync(activity, new UpdateActivityRequest("Transferred", null, null, destination));
        }
        else
        {
            await activities.DeleteAsync([activity]);
        }

        await using var verification = await setup.Factory.CreateDbContextAsync();
        Assert.Equal(2, await verification.Routes.CountAsync(x => x.OwnerId == owner));
        Assert.Equal(2, await verification.SourceFiles.CountAsync(x => x.OwnerId == owner && x.RouteId != null));
        Assert.True(File.Exists(storage.Originals.ResolveStoredPath(sharedPath)));
        if (routeFirst)
        {
            var moved = await verification.SourceFiles.SingleAsync(x => x.ActivityId == activity);
            Assert.NotEqual(owner, moved.OwnerId);
            Assert.True(File.Exists(storage.Originals.ResolveStoredPath(moved.StoredPath)));
        }
        else Assert.False(await verification.Activities.AnyAsync(x => x.Id == activity));
    }

    private sealed class RecordingWatchedQueue : IImportQueue
    {
        public List<ImportRequest> Requests { get; } = [];
        public ValueTask<Guid> EnqueueAsync(ImportRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(Guid.NewGuid());
        }
        public IAsyncEnumerable<ImportProgress> WatchAsync(Guid importId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
