using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed partial class StatisticsReadService
{
    private sealed record OverviewActivity(Guid Id, Guid OwnerId, string OwnerName, string Title, SportKind Sport,
        DateTimeOffset StartTimeUtc, double DistanceMeters, double MovingTimeSeconds, double ElevationGainMeters, byte[]? Payload);

    public async Task<RecordOverviewResult> GetOverviewAsync(RecordOverviewQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(query.Scope)) throw new ArgumentException("Choose a valid record scope.", nameof(query));
        if (query.Sport.HasValue && !Enum.IsDefined(query.Sport.Value))
            throw new ArgumentException("Choose a valid record sport.", nameof(query));
        var selection = query.Period ?? new(ReportingPreset.AllTime);
        ReportingDates.Validate(selection);
        var asOf = query.AsOfUtc ?? (timeProvider ?? TimeProvider.System).GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await Storage.ReadSnapshot.BeginAsync(db, cancellationToken);
        if (selection.Preset == ReportingPreset.AllTime)
        {
            var periods = await ReportingDateQuery.ResolveAsync(db, query.OwnerId, selection, asOf, cancellationToken);
            var records = await ReadRecordsAsync(db, query.OwnerId, query.Scope, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(records.Where(record => !query.Sport.HasValue || record.Sport == query.Sport).ToArray(), periods);
        }

        var resolved = await ReportingDateQuery.ResolveAsync(db, query.OwnerId, selection, asOf, cancellationToken);
        var source = ReportingDateQuery.Apply(db.Activities.AsNoTracking(), resolved);
        if (query.Sport.HasValue) source = source.Where(activity => activity.Sport == query.Sport);
        if (query.Scope != RecordScope.All) source = source.Where(activity => activity.IsIndoor == (query.Scope == RecordScope.Indoor));
        var benchmarks = Enum.GetValues<SportKind>().ToDictionary(sport => sport, OverviewBenchmarks);
        var winners = new Dictionary<(Guid Owner, SportKind Sport, RecordKind Kind, string Key), (RecordBenchmark Benchmark, RecordAttempt Attempt)>();
        for (var offset = 0; ; offset += 16)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await source.OrderBy(activity => activity.Id).Skip(offset).Take(16)
                .Select(activity => new OverviewActivity(activity.Id, activity.OwnerId,
                    activity.Owner!.DisplayName, activity.Title, activity.Sport, activity.StartTimeUtc,
                    activity.DistanceMeters, activity.MovingTimeSeconds, activity.ElevationGainMeters,
                    activity.Stream == null ? null : activity.Stream.CompressedPayload)).ToArrayAsync(cancellationToken);
            if (batch.Length == 0) break;
            foreach (var activity in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidates = await Task.Run(async () =>
                {
                    var results = new List<(RecordBenchmark Benchmark, RecordAttempt Attempt)>();
                    IReadOnlyList<TrackPoint>? points = null;
                    if (activity.Payload is not null)
                        points = await TrackCodec.DecodeAsync(activity.Payload, cancellationToken);
                    foreach (var benchmark in benchmarks[activity.Sport])
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (benchmark.IsWholeActivity)
                        {
                            var value = benchmark.Kind switch
                            {
                                RecordKind.Distance => activity.DistanceMeters,
                                RecordKind.Duration => activity.MovingTimeSeconds,
                                RecordKind.Elevation => activity.ElevationGainMeters,
                                RecordKind.AverageSpeed when activity.DistanceMeters >= 1000 && activity.MovingTimeSeconds > 0 =>
                                    activity.DistanceMeters / activity.MovingTimeSeconds,
                                _ => 0
                            };
                            if (double.IsFinite(value) && value > 0)
                                results.Add((benchmark, OverviewAttempt(activity, value, 100, null)));
                        }
                        else if (points is not null)
                        {
                            foreach (var window in BestEffortCalculator.Attempts(points, activity.Sport, benchmark.Kind,
                                         benchmark.Target!.Value, false, cancellationToken))
                                results.Add((benchmark, OverviewAttempt(activity, window.Value, window.CoveragePercent, window)));
                        }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    return results;
                }, cancellationToken);
                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = (activity.OwnerId, activity.Sport, candidate.Benchmark.Kind, candidate.Benchmark.Key);
                    if (!winners.TryGetValue(key, out var current) ||
                        CompareOverviewAttempts(candidate.Attempt, current.Attempt, candidate.Benchmark.LowerIsBetter) < 0)
                        winners[key] = candidate;
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        var output = winners.Values.Select(winner => new PersonalRecord(
                OverviewRecordId(winner.Attempt.OwnerId, winner.Benchmark), winner.Attempt.OwnerId, winner.Attempt.OwnerName,
                winner.Benchmark.Sport, winner.Benchmark.Kind, winner.Benchmark.Key, winner.Attempt.Value,
                winner.Attempt.ActivityId, winner.Attempt.ActivityTitle, winner.Attempt.CoveragePercent, winner.Attempt.ActivityDate))
            .OrderBy(record => record.Sport).ThenBy(record => RecordCatalog.CategoryOrder(record.Kind))
            .ThenBy(record => RecordCatalog.TargetOrder(record.Sport, record.Kind, record.Key))
            .ThenBy(record => record.Key, StringComparer.Ordinal).ThenBy(record => record.OwnerId).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new(output, resolved);
    }

    private static RecordBenchmark[] OverviewBenchmarks(SportKind sport)
    {
        var summary = new[]
        {
            RecordCatalog.Find(sport, RecordKind.Distance, "Longest distance"),
            RecordCatalog.Find(sport, RecordKind.Duration, "Longest moving time"),
            RecordCatalog.Find(sport, RecordKind.Elevation, "Most elevation gain"),
            RecordCatalog.Find(sport, RecordKind.AverageSpeed, sport == SportKind.Rowing
                ? "Best average split (activities >= 1 km)" : "Best average speed (activities >= 1 km)")
        };
        return summary.Where(benchmark => benchmark is not null).Select(benchmark => benchmark!)
            .Concat(RecordCatalog.DistanceTargets(sport).Select(target => RecordCatalog.Find(sport, RecordKind.DistanceEffort, target.Key)!))
            .Concat(RecordCatalog.TimedDistanceTargets(sport).Select(target => RecordCatalog.Find(sport, RecordKind.TimedDistanceEffort, target.Key)!))
            .Concat(RecordCatalog.PowerTargets.Select(target => RecordCatalog.Find(sport, RecordKind.PowerCurve, target.Key)!)).ToArray();
    }

    private static RecordAttempt OverviewAttempt(OverviewActivity activity, double value, double coverage, EffortWindow? window) => new(
        activity.Id, activity.Title, activity.OwnerId, activity.OwnerName, activity.StartTimeUtc, value, coverage,
        window is null ? null : (window.StartTime - activity.StartTimeUtc).TotalSeconds,
        window is null ? null : (window.FinishTime - activity.StartTimeUtc).TotalSeconds, window?.StartPosition, window?.FinishPosition);

    private static int CompareOverviewAttempts(RecordAttempt left, RecordAttempt right, bool lowerIsBetter)
    {
        var value = left.Value.CompareTo(right.Value) * (lowerIsBetter ? 1 : -1);
        if (value != 0) return value;
        value = left.ActivityDate.CompareTo(right.ActivityDate);
        if (value != 0) return value;
        value = left.ActivityId.CompareTo(right.ActivityId);
        if (value != 0) return value;
        value = Nullable.Compare(left.StartPosition, right.StartPosition);
        return value != 0 ? value : Nullable.Compare(left.FinishPosition, right.FinishPosition);
    }

    private static Guid OverviewRecordId(Guid owner, RecordBenchmark benchmark)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{owner:N}|{(int)benchmark.Sport}|{(int)benchmark.Kind}|{benchmark.Key}");
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
    }
}
