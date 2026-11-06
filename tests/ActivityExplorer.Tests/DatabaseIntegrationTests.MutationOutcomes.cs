using System.Reflection;
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
    public async Task Profile_deletion_reports_committed_cleanup_failure()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Delete outcome");
        var storage = CreateStorageServices(setup);
        var paths = new AppDataPaths();
        File.WriteAllText(Path.Combine(paths.GetOwnerOriginalsPath(owner), "source.fit"), "synthetic");
        var files = new FailingCommitCoordinator(storage.FileOperations);
        var service = new ProfileService(setup.Factory, paths, files, storage.OwnerLock);

        var result = await service.DeleteAsync(owner, "DELETE Delete outcome");

        Assert.Equal(new ProfileDeletionResult(owner, true), result);
        Assert.Equal(1, files.CommitCount);
        Assert.Equal(0, files.RollbackCount);
        await using var db = await setup.Factory.CreateDbContextAsync();
        Assert.False(await db.Owners.AnyAsync(x => x.Id == owner));
    }

    [Fact]
    public async Task Route_import_reports_committed_cancellation_during_cleanup()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Route outcome");
        var storage = CreateStorageServices(setup);
        var paths = new AppDataPaths();
        var staged = TestSupport.Write(paths.StagingPath, "outcome.gpx", TestSupport.Gpx());
        var points = await new GpxRouteReader().ReadAsync(staged);
        var hash = await Fingerprint.Sha256Async(staged, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var files = new FailingCommitCoordinator(storage.FileOperations, cancellation);
        var service = new RouteService(setup.Factory, storage.Originals, files, storage.OwnerLock);

        var result = await service.ImportGpxAsync(
            new CreateRoutePathRequest(owner, "Committed route", null, SportKind.Running, points),
            staged, "outcome.gpx", hash, new FileInfo(staged).Length, cancellation.Token);

        Assert.True(result.FileCleanupPending);
        Assert.Equal(1, files.CommitCount);
        Assert.Equal(0, files.RollbackCount);
        await storage.FileOperations.RecoverAsync();
        await using var db = await setup.Factory.CreateDbContextAsync();
        Assert.Equal(result.RouteId, (await db.Routes.SingleAsync()).Id);
        Assert.Single(await db.ImportBatches.ToListAsync());
        var source = Assert.Single(await db.SourceFiles.ToListAsync());
        Assert.True(File.Exists(storage.Originals.ResolveStoredPath(source.StoredPath)));
        Assert.False(File.Exists(staged));
        Assert.Equal(FileOperationState.Completed, (await db.FileOperations.SingleAsync()).State);
    }

    [Fact]
    public async Task Activity_transfer_reports_all_postcommit_failures_and_metadata_update_is_normal()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var oldOwner = await setup.SeedOwnerAsync("Transfer source");
        var newOwner = await setup.SeedOwnerAsync("Transfer destination");
        var activityId = await setup.SeedActivityAsync(oldOwner, "Before", SportKind.Cycling);
        var storage = CreateStorageServices(setup);
        var sourcePath = Path.Combine(new AppDataPaths().GetOwnerOriginalsPath(oldOwner), "source.fit");
        await File.WriteAllTextAsync(sourcePath, "synthetic source");
        var hash = await Fingerprint.Sha256Async(sourcePath, CancellationToken.None);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var batch = new ImportBatch
            {
                OwnerId = oldOwner,
                SourceKind = SourceKind.Fit,
                Status = ImportStatus.Completed,
                DisplayName = "source.fit",
                StagedPath = string.Empty
            };
            db.ImportBatches.Add(batch);
            db.SourceFiles.Add(new SourceFile
            {
                OwnerId = oldOwner,
                ImportBatchId = batch.Id,
                ActivityId = activityId,
                SourceKind = SourceKind.Fit,
                OriginalName = "source.fit",
                StoredPath = storage.Originals.ToStoredPath(sourcePath),
                Sha256 = hash,
                Length = new FileInfo(sourcePath).Length
            });
            db.Segments.Add(new Segment { OwnerId = oldOwner, Name = "Segment", Sport = SportKind.Cycling });
            await db.SaveChangesAsync();
        }
        var files = new FailingCommitCoordinator(storage.FileOperations);
        var statistics = DispatchProxy.Create<IStatisticsService, FailingRefreshProxy>();
        var segments = DispatchProxy.Create<ISegmentService, FailingRefreshProxy>();
        var service = new ActivityQueryService(setup.Factory, statistics, segments, storage.Originals, files,
            storage.OwnerLock, NullLogger<ActivityQueryService>.Instance);

        var result = await service.UpdateAsync(activityId, new UpdateActivityRequest("Transferred", null, null, newOwner));

        Assert.Equal(new ActivityUpdateResult(activityId, newOwner, true, true, true), result);
        Assert.Equal(1, files.CommitCount);
        Assert.Equal(0, files.RollbackCount);
        Assert.Equal(2, ((FailingRefreshProxy)(object)statistics).Calls);
        Assert.Equal(1, ((FailingRefreshProxy)(object)segments).Calls);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var activity = await db.Activities.SingleAsync(x => x.Id == activityId);
            Assert.Equal(newOwner, activity.OwnerId);
            Assert.Equal(1, activity.MutationVersion);
            Assert.Single(await db.ImportBatches.Where(x => x.Kind == ImportBatchKind.ActivityTransfer).ToListAsync());
        }
        var metadata = await service.UpdateAsync(activityId, new UpdateActivityRequest("Edited", null, null, newOwner));
        Assert.Equal(new ActivityUpdateResult(activityId, newOwner, false, false, false), metadata);
    }

    public class FailingRefreshProxy : DispatchProxy
    {
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal("RecomputeAsync", targetMethod?.Name);
            Calls++;
            return Task.FromException(new IOException("Synthetic refresh failure"));
        }
    }

    private sealed class FailingCommitCoordinator(IFileOperationCoordinator inner, CancellationTokenSource? cancel = null) : IFileOperationCoordinator
    {
        public int CommitCount { get; private set; }
        public int RollbackCount { get; private set; }
        public Task<PreparedFileOperation> PrepareCopyAsync(Guid ownerId, Guid? entityId, string sourcePath, string targetPath,
            string expectedSha256, bool deleteSourceOnCommit = false, CancellationToken cancellationToken = default) =>
            inner.PrepareCopyAsync(ownerId, entityId, sourcePath, targetPath, expectedSha256, deleteSourceOnCommit, cancellationToken);
        public Task<Guid?> QuarantineOwnerAsync(Guid ownerId, CancellationToken cancellationToken = default) =>
            inner.QuarantineOwnerAsync(ownerId, cancellationToken);
        public Task<Guid?> QuarantineFileAsync(Guid ownerId, Guid? entityId, string storedPath, CancellationToken cancellationToken = default) =>
            inner.QuarantineFileAsync(ownerId, entityId, storedPath, cancellationToken);
        public Task CommitAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            CommitCount++;
            if (cancel is not null)
            {
                cancel.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return Task.FromException(new IOException("Synthetic cleanup failure"));
        }
        public Task RollbackAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            RollbackCount++;
            return inner.RollbackAsync(operationId, cancellationToken);
        }
        public Task RecoverAsync(CancellationToken cancellationToken = default) => inner.RecoverAsync(cancellationToken);
    }
}
