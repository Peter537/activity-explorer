using System.Collections.Concurrent;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed partial class StatisticsService(IDbContextFactory<ExplorerDbContext> contextFactory, TimeProvider? timeProvider = null) : IStatisticsService
{
    private static readonly RecordScope[] AllAndIndoorScopes = [RecordScope.All, RecordScope.Indoor];
    private static readonly RecordScope[] AllAndOutdoorScopes = [RecordScope.All, RecordScope.Outdoor];
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _ownerGates = new();

    public async Task RecomputeAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        var gate = _ownerGates.GetOrAdd(ownerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var activities = await db.Activities.AsNoTracking().Include(x => x.Stream)
                .Where(x => x.OwnerId == ownerId).ToListAsync(cancellationToken);
            var snapshots = new Dictionary<(RecordScope Scope, SportKind Sport, RecordKind Kind, string Key), StatisticSnapshot>();

            foreach (var activity in activities)
            {
                var scopes = activity.IsIndoor ? AllAndIndoorScopes : AllAndOutdoorScopes;
                foreach (var scope in scopes)
                {
                    KeepHigher(snapshots, activity, scope, RecordKind.Distance, "Longest distance", activity.DistanceMeters, 100);
                    KeepHigher(snapshots, activity, scope, RecordKind.Duration, "Longest moving time", activity.MovingTimeSeconds, 100);
                    if (activity.Sport != SportKind.Rowing)
                        KeepHigher(snapshots, activity, scope, RecordKind.Elevation, "Most elevation gain", activity.ElevationGainMeters, 100);
                    if (activity.DistanceMeters >= 1_000 && activity.MovingTimeSeconds > 0)
                        KeepHigher(snapshots, activity, scope, RecordKind.AverageSpeed,
                            activity.Sport == SportKind.Rowing ? "Best average split (activities >= 1 km)" : "Best average speed (activities >= 1 km)",
                            activity.DistanceMeters / activity.MovingTimeSeconds, 100);
                }

                if (activity.Stream is null) continue;
                var points = TrackCodec.Decode(activity.Stream.CompressedPayload);
                foreach (var scope in scopes)
                {
                    foreach (var target in RecordCatalog.DistanceTargets(activity.Sport))
                    {
                        var effort = BestEffortCalculator.BestDistance(points, target.Target, activity.Sport);
                        if (effort.HasValue)
                            KeepLower(snapshots, activity, scope, RecordKind.DistanceEffort, target.Key,
                                effort.Value.Value, effort.Value.CoveragePercent);
                    }

                    foreach (var target in RecordCatalog.TimedDistanceTargets(activity.Sport))
                    {
                        var effort = BestEffortCalculator.BestTimedDistance(points, target.Target, activity.Sport);
                        if (effort.HasValue)
                            KeepHigher(snapshots, activity, scope, RecordKind.TimedDistanceEffort, target.Key,
                                effort.Value.Value, effort.Value.CoveragePercent);
                    }

                    foreach (var target in RecordCatalog.PowerTargets)
                    {
                        var curve = BestEffortCalculator.BestPower(points, target.Target);
                        if (curve.HasValue)
                            KeepHigher(snapshots, activity, scope, RecordKind.PowerCurve, target.Key,
                                curve.Value.Value, curve.Value.CoveragePercent);
                    }
                }
            }

            var previous = await db.StatisticSnapshots.Where(x => x.OwnerId == ownerId).ToListAsync(cancellationToken);
            db.StatisticSnapshots.RemoveRange(previous);
            db.StatisticSnapshots.AddRange(snapshots.Values);
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private readonly StatisticsReadService _reader = new(contextFactory, timeProvider);
    public Task<IReadOnlyList<PersonalRecord>> GetRecordsAsync(Guid? ownerId, RecordScope scope = RecordScope.All, CancellationToken cancellationToken = default) => _reader.GetRecordsAsync(ownerId, scope, cancellationToken);
    public Task<RecordOverviewResult> GetOverviewAsync(RecordOverviewQuery query, CancellationToken cancellationToken = default) => _reader.GetOverviewAsync(query, cancellationToken);
    public Task<RecordAttemptPage?> GetAttemptsAsync(RecordAttemptQuery query, CancellationToken cancellationToken = default) => _reader.GetAttemptsAsync(query, cancellationToken);
    public Task<RecordHistory?> GetHistoryAsync(RecordHistoryQuery query, CancellationToken cancellationToken = default) => _reader.GetHistoryAsync(query, cancellationToken);

    private static void KeepHigher(
        IDictionary<(RecordScope, SportKind, RecordKind, string), StatisticSnapshot> records,
        Activity activity, RecordScope scope, RecordKind kind, string key, double value, double coverage)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return;
        var mapKey = (scope, activity.Sport, kind, key);
        if (!records.TryGetValue(mapKey, out var current) || value > current.Value)
            records[mapKey] = Snapshot(activity, scope, kind, key, value, coverage);
    }

    private static void KeepLower(
        IDictionary<(RecordScope, SportKind, RecordKind, string), StatisticSnapshot> records,
        Activity activity, RecordScope scope, RecordKind kind, string key, double value, double coverage)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return;
        var mapKey = (scope, activity.Sport, kind, key);
        if (!records.TryGetValue(mapKey, out var current) || value < current.Value)
            records[mapKey] = Snapshot(activity, scope, kind, key, value, coverage);
    }

    private static StatisticSnapshot Snapshot(Activity activity, RecordScope scope, RecordKind kind, string key, double value, double coverage) => new()
    {
        OwnerId = activity.OwnerId,
        Sport = activity.Sport,
        Kind = kind,
        Scope = scope,
        Key = key,
        Value = value,
        ActivityId = activity.Id,
        CoveragePercent = Math.Clamp(coverage, 0, 100),
        ComputationVersion = RecordCatalog.ComputationVersion
    };
}
