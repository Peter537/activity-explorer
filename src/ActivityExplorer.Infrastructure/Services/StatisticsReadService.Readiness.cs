using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed partial class StatisticsReadService
{
    public IReadOnlyList<RecordBenchmark> GetBenchmarks(SportKind sport)
    {
        if (!Enum.IsDefined(sport)) throw new ArgumentException("Choose a valid sport.");
        return OverviewBenchmarks(sport);
    }

    public async Task<ReadRecordOverview> ReadOverviewAsync(RecordOverviewQuery query, CancellationToken cancellationToken = default)
    {
        if (query.OwnerId is null || query.Sport is null || !Enum.IsDefined(query.Scope))
            throw new ArgumentException("Choose a profile, sport, and valid record scope.");
        var benchmarks = GetBenchmarks(query.Sport.Value);
        var selection = query.Period ?? new(ReportingPreset.AllTime);
        if (selection.Preset != ReportingPreset.AllTime)
            return new(await GetOverviewAsync(query, cancellationToken), benchmarks, "calculated", true, null, RecordCatalog.ComputationVersion);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var snapshot = await ReadSnapshot.BeginAsync(db, cancellationToken);
        var periods = await ReportingDateQuery.ResolveAsync(db, query.OwnerId, selection,
            query.AsOfUtc ?? (timeProvider ?? TimeProvider.System).GetUtcNow(), cancellationToken);
        var activities = db.Activities.AsNoTracking().Where(activity => activity.OwnerId == query.OwnerId && activity.Sport == query.Sport);
        if (query.Scope != RecordScope.All) activities = activities.Where(activity => activity.IsIndoor == (query.Scope == RecordScope.Indoor));
        var stored = db.StatisticSnapshots.AsNoTracking().Where(record => record.OwnerId == query.OwnerId && record.Sport == query.Sport && record.Scope == query.Scope);
        var hasActivities = await activities.AnyAsync(cancellationToken);
        var versions = await stored.Select(record => record.ComputationVersion).Distinct().ToArrayAsync(cancellationToken);
        var invalidReferences = await stored.AnyAsync(record => !db.Activities.Any(activity => activity.Id == record.ActivityId &&
            activity.OwnerId == record.OwnerId && activity.Sport == record.Sport), cancellationToken);
        var available = !invalidReferences && (!hasActivities || versions.Length > 0 && versions.All(version => version == RecordCatalog.ComputationVersion));
        var records = available ? (await ReadRecordsAsync(db, query.OwnerId, query.Scope, cancellationToken)).Where(record => record.Sport == query.Sport).ToArray() : [];
        return new(new(records, periods), benchmarks, "persisted_snapshot", available,
            available ? null : "Record snapshots are missing or use a different calculation version. Refresh records in the matching web application.", RecordCatalog.ComputationVersion);
    }
}
