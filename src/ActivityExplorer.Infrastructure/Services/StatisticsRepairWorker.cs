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
    ILogger<StatisticsRepairWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Automatic statistics repair stopped unexpectedly.");
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var ownerIds = await db.Owners.AsNoTracking()
            .Where(owner => db.Activities.Any(activity => activity.OwnerId == owner.Id) &&
                            (!db.StatisticSnapshots.Any(snapshot =>
                                 snapshot.OwnerId == owner.Id && snapshot.Scope == RecordScope.All) ||
                             db.Activities.Any(activity => activity.OwnerId == owner.Id && !activity.IsIndoor) &&
                             !db.StatisticSnapshots.Any(snapshot =>
                                 snapshot.OwnerId == owner.Id && snapshot.Scope == RecordScope.Outdoor)))
            .OrderBy(owner => owner.CreatedAtUtc)
            .Select(owner => owner.Id)
            .ToListAsync(cancellationToken);

        foreach (var ownerId in ownerIds)
        {
            logger.LogInformation("Repairing missing record snapshots for owner {OwnerId}.", ownerId);
            await statistics.RecomputeAsync(ownerId, cancellationToken);
        }
    }
}
