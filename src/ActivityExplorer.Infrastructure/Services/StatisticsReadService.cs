using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed partial class StatisticsReadService(IDbContextFactory<ExplorerDbContext> contextFactory, TimeProvider? timeProvider = null) : IStatisticsReadService
{
    public async Task<IReadOnlyList<PersonalRecord>> GetRecordsAsync(
        Guid? ownerId, RecordScope scope = RecordScope.All, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await ReadRecordsAsync(db, ownerId, scope, cancellationToken);
    }

    private static async Task<IReadOnlyList<PersonalRecord>> ReadRecordsAsync(ExplorerDbContext db, Guid? ownerId, RecordScope scope, CancellationToken cancellationToken)
    {
        var query = db.StatisticSnapshots.AsNoTracking().Where(x => x.Scope == scope);
        if (ownerId.HasValue) query = query.Where(x => x.OwnerId == ownerId);
        var records = await query.Join(db.Owners.AsNoTracking(), s => s.OwnerId, o => o.Id, (s, o) => new { s, o })
            .Join(db.Activities.AsNoTracking(), x => x.s.ActivityId, a => a.Id, (x, a) => new { Snapshot = x.s, Owner = x.o, Activity = a })
            .Where(item => item.Snapshot.OwnerId == item.Activity.OwnerId && item.Snapshot.Sport == item.Activity.Sport)
            .Select(item => new PersonalRecord(item.Snapshot.Id, item.Snapshot.OwnerId, item.Owner.DisplayName,
                item.Snapshot.Sport, item.Snapshot.Kind, item.Snapshot.Key, item.Snapshot.Value,
                item.Activity.Id, item.Activity.Title, item.Snapshot.CoveragePercent, item.Activity.StartTimeUtc))
            .ToListAsync(cancellationToken);
        return records
            .OrderBy(x => x.Sport)
            .ThenBy(x => RecordCatalog.CategoryOrder(x.Kind))
            .ThenBy(x => RecordCatalog.TargetOrder(x.Sport, x.Kind, x.Key))
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .ToArray();
    }

}
