using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class StatisticsRepairWorker(
    IDbContextFactory<ExplorerDbContext> contextFactory,
    IStatisticsService statistics,
    ILogger<StatisticsRepairWorker> logger,
    ISegmentService? segments = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(exception, "Automatic projection repair will retry after a failed scan.");
                }
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var pendingRecords = ProjectionVersions.PendingRecords(db);
        var ownerIds = await db.Owners.AsNoTracking()
            .Where(owner => !db.StatisticSnapshots.Any(snapshot =>
                                snapshot.OwnerId == owner.Id &&
                                snapshot.ComputationVersion > RecordCatalog.ComputationVersion) &&
                            (pendingRecords.Any(state => state.OwnerId == owner.Id) ||
                             !db.OwnerProjectionVersions.Any(state => state.OwnerId == owner.Id) &&
                             db.Activities.Any(activity => activity.OwnerId == owner.Id) ||
                             db.StatisticSnapshots.Any(snapshot =>
                                 snapshot.OwnerId == owner.Id &&
                                 snapshot.ComputationVersion < RecordCatalog.ComputationVersion)))
            .OrderBy(owner => owner.CreatedAtUtc)
            .Select(owner => owner.Id)
            .ToListAsync(cancellationToken);

        foreach (var ownerId in ownerIds)
        {
            try { await statistics.RecomputeAsync(ownerId, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Record repair for profile {OwnerId} will retry.", ownerId);
            }
        }

        if (segments is null) return;
        var segmentIds = await ProjectionVersions.PendingSegments(db)
            .OrderBy(segment => segment.OwnerId).ThenBy(segment => segment.Id)
            .Select(segment => segment.Id).ToArrayAsync(cancellationToken);
        foreach (var segmentId in segmentIds)
        {
            try { await segments.RecomputeAsync(segmentId, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Effort repair for segment {SegmentId} will retry.", segmentId);
            }
        }
    }
}
