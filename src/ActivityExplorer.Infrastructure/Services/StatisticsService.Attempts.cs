using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed partial class StatisticsService
{
    public async Task<RecordAttemptPage?> GetAttemptsAsync(RecordAttemptQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var benchmark = RecordCatalog.Find(query.Sport, query.Kind, query.Key);
        if (benchmark is null || !Enum.IsDefined(query.Scope)) return null;
        const int pageSize = 50;
        var requestedPage = Math.Max(1, query.Page);
        var capacity = (long)requestedPage * pageSize;
        var comparison = Comparer<RecordAttempt>.Create((left, right) =>
        {
            var value = left.Value.CompareTo(right.Value) * (benchmark.LowerIsBetter ? 1 : -1);
            if (value != 0) return value;
            value = left.ActivityDate.CompareTo(right.ActivityDate);
            if (value != 0) return value;
            value = left.ActivityId.CompareTo(right.ActivityId);
            if (value != 0) return value;
            value = Nullable.Compare(left.StartPosition, right.StartPosition);
            return value != 0 ? value : Nullable.Compare(left.FinishPosition, right.FinishPosition);
        });
        var retained = new PriorityQueue<RecordAttempt, RecordAttempt>(Comparer<RecordAttempt>.Create((left, right) => comparison.Compare(right, left)));
        var total = 0;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var activities = db.Activities.AsNoTracking().Where(activity => activity.Sport == query.Sport);
        if (query.OwnerId.HasValue) activities = activities.Where(activity => activity.OwnerId == query.OwnerId.Value);
        if (query.Scope != RecordScope.All) activities = activities.Where(activity => activity.IsIndoor == (query.Scope == RecordScope.Indoor));
        // Only the requested category needs streams; whole-activity benchmarks never read payloads.
        var withOwner = activities.Include(activity => activity.Owner).AsQueryable();
        if (!benchmark.IsWholeActivity) withOwner = withOwner.Include(activity => activity.Stream);
        for (var offset = 0; ; offset += 16)
        {
            var batch = await withOwner.OrderBy(activity => activity.Id).Skip(offset).Take(16).ToArrayAsync(cancellationToken);
            if (batch.Length == 0) break;
            foreach (var activity in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (benchmark.IsWholeActivity)
                {
                    var value = query.Kind switch
                    {
                        RecordKind.Distance => activity.DistanceMeters,
                        RecordKind.Duration => activity.MovingTimeSeconds,
                        RecordKind.Elevation => activity.ElevationGainMeters,
                        RecordKind.AverageSpeed when activity.DistanceMeters >= 1_000 && activity.MovingTimeSeconds > 0
                            => activity.DistanceMeters / activity.MovingTimeSeconds,
                        _ => 0
                    };
                    if (double.IsFinite(value) && value > 0) Add(activity, value, 100, null);
                }
                else if (activity.Stream is not null)
                {
                    // Stream evaluation is CPU-bound; let the Blazor circuit render progress and process cancellation.
                    var windows = await Task.Run(() => BestEffortCalculator.Attempts(TrackCodec.Decode(activity.Stream.CompressedPayload),
                        query.Sport, query.Kind, benchmark.Target!.Value, query.MultiplePerActivity, cancellationToken), cancellationToken);
                    foreach (var window in windows) Add(activity, window.Value, window.CoveragePercent, window);
                }
            }
        }
        var page = Math.Min(requestedPage, Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)));
        var items = retained.UnorderedItems.Select(item => item.Element).Order(comparison)
            .Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        return new RecordAttemptPage(benchmark, new(items, total, page, pageSize));

        void Add(Activity activity, double value, double coverage, EffortWindow? window)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = new RecordAttempt(activity.Id, activity.Title, activity.OwnerId, activity.Owner!.DisplayName,
                activity.StartTimeUtc, value, coverage,
                window is null ? null : (window.StartTime - activity.StartTimeUtc).TotalSeconds,
                window is null ? null : (window.FinishTime - activity.StartTimeUtc).TotalSeconds,
                window?.StartPosition, window?.FinishPosition);
            total++;
            retained.Enqueue(attempt, attempt);
            if (retained.Count > capacity) retained.Dequeue();
        }
    }
}
