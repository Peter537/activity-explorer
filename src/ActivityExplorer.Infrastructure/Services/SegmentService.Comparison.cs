using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed partial class SegmentService
{
    public async Task<SegmentComparisonResult?> GetComparisonAsync(
        Guid segmentId, Guid baselineEffortId, Guid comparisonEffortId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SegmentDetail detail;
        SegmentEffortSummary baseline;
        SegmentEffortSummary comparison;
        Dictionary<Guid, byte[]> payloads;
        IReadOnlyList<SegmentEffort> childEfforts;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            // The two passes and their streams must come from the same read snapshot. Release it before alignment.
            await db.Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await using var enlisted = await db.Database.UseTransactionAsync(transaction, cancellationToken);
            var loaded = await GetDetailAsync(db, segmentId, null, includeSelectedStream: false, cancellationToken);
            if (loaded is null) return null;
            detail = loaded;
            var requested = new[] { baselineEffortId, comparisonEffortId };
            var validIds = await db.SegmentEfforts.AsNoTracking()
                .Where(effort => requested.Contains(effort.Id) && effort.SegmentId == segmentId &&
                    effort.OwnerId == detail.Summary.OwnerId && effort.Activity != null &&
                    effort.Activity.OwnerId == detail.Summary.OwnerId && effort.Activity.Sport == detail.Summary.Sport)
                .Select(effort => effort.Id).ToArrayAsync(cancellationToken);
            var selectedBaseline = detail.Efforts.FirstOrDefault(effort => effort.Id == baselineEffortId && validIds.Contains(effort.Id));
            var selectedComparison = detail.Efforts.FirstOrDefault(effort => effort.Id == comparisonEffortId && validIds.Contains(effort.Id));
            if (baselineEffortId == comparisonEffortId || selectedBaseline is null || selectedComparison is null)
                return new(detail, selectedBaseline, selectedComparison, null, null, [], [],
                    SegmentComparisonUnavailableReason.InvalidEfforts,
                    "Choose two different available efforts from this segment and profile.");
            baseline = selectedBaseline;
            comparison = selectedComparison;
            var activityIds = new[] { baseline.ActivityId, comparison.ActivityId }.Distinct().ToArray();
            payloads = await db.ActivityStreams.AsNoTracking()
                .Where(stream => activityIds.Contains(stream.ActivityId) && stream.OwnerId == detail.Summary.OwnerId)
                .ToDictionaryAsync(stream => stream.ActivityId, stream => stream.CompressedPayload, cancellationToken);
            var childIds = detail.Children.Select(child => child.Summary.Id).ToArray();
            childEfforts = await db.SegmentEfforts.AsNoTracking()
                .Where(effort => childIds.Contains(effort.SegmentId) && activityIds.Contains(effort.ActivityId) &&
                    effort.OwnerId == detail.Summary.OwnerId && effort.Segment != null &&
                    effort.Segment.OwnerId == detail.Summary.OwnerId && effort.Segment.Sport == detail.Summary.Sport)
                .OrderBy(effort => effort.StartPointIndex).ThenBy(effort => effort.EndPointIndex).ThenBy(effort => effort.Id)
                .ToArrayAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return await Task.Run(() =>
        {
            var sources = payloads.ToDictionary(pair => pair.Key, pair => TrackCodec.Decode(pair.Value));
            var baselineAlignment = Align(baseline);
            var comparisonAlignment = Align(comparison);
            var unavailable = !baselineAlignment.IsAvailable ? baselineAlignment : !comparisonAlignment.IsAvailable ? comparisonAlignment : null;
            var children = CompareChildren(detail, baseline, comparison, baselineAlignment, comparisonAlignment, childEfforts, cancellationToken);
            var samples = unavailable is null
                ? SegmentComparisonCalculator.BuildSamples(baselineAlignment, comparisonAlignment, cancellationToken) : [];
            if (samples.Count > 0 && Math.Abs(samples[^1].DeltaSeconds - (comparison.ElapsedSeconds - baseline.ElapsedSeconds)) >
                SegmentComparisonCalculator.DurationToleranceSeconds)
                return new SegmentComparisonResult(detail, baseline, comparison, baselineAlignment, comparisonAlignment, [], children,
                    SegmentComparisonUnavailableReason.DurationMismatch,
                    "Recorded timing differs from these saved efforts. Recompute the segment efforts.");
            return new SegmentComparisonResult(detail, baseline, comparison, baselineAlignment, comparisonAlignment,
                samples, children, unavailable?.UnavailableReason ?? SegmentComparisonUnavailableReason.None, unavailable?.Message);

            SegmentEffortAlignment Align(SegmentEffortSummary effort)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return sources.TryGetValue(effort.ActivityId, out var points)
                    ? SegmentComparisonCalculator.Align(detail.Points, points, effort, detail.Summary.ToleranceMeters, cancellationToken)
                    : new(effort.Id, [], new([]), SegmentComparisonUnavailableReason.MissingStream,
                        "The detailed recording for one of these efforts is unavailable.");
            }
        }, cancellationToken);
    }

    private static IReadOnlyList<SegmentChildComparison> CompareChildren(
        SegmentDetail detail, SegmentEffortSummary baseline, SegmentEffortSummary comparison,
        SegmentEffortAlignment baselineAlignment, SegmentEffortAlignment comparisonAlignment,
        IReadOnlyList<SegmentEffort> efforts, CancellationToken cancellationToken)
    {
        var result = new List<SegmentChildComparison>();
        foreach (var child in detail.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baselineMatches = MatchChildPlacements(child, baseline, baselineAlignment, efforts, detail.Summary.ToleranceMeters);
            var comparisonMatches = MatchChildPlacements(child, comparison, comparisonAlignment, efforts, detail.Summary.ToleranceMeters);
            for (var index = 0; index < child.Placements.Count; index++)
                result.Add(new(child.Summary, index + 1, child.Placements[index],
                    baselineMatches[index].Effort, baselineMatches[index].Status,
                    comparisonMatches[index].Effort, comparisonMatches[index].Status));
        }
        return result;
    }

    private static ChildPlacementMatch[] MatchChildPlacements(
        SubsegmentDetail child, SegmentEffortSummary parent, SegmentEffortAlignment alignment,
        IReadOnlyList<SegmentEffort> efforts, double parentTolerance)
    {
        if (!alignment.IsAvailable)
            return child.Placements.Select(_ => new ChildPlacementMatch(null, SegmentChildComparisonStatus.AlignmentUnavailable)).ToArray();
        var contained = efforts.Where(effort => effort.SegmentId == child.Summary.Id && effort.ActivityId == parent.ActivityId &&
            effort.StartPointIndex >= parent.StartPointIndex && effort.EndPointIndex <= parent.EndPointIndex &&
            effort.EndPointIndex > effort.StartPointIndex).ToArray();
        var tolerance = parentTolerance + child.Summary.ToleranceMeters;
        var candidates = child.Placements.Select(placement => contained.Where(effort =>
            DistanceAtSource(alignment.Points, effort.StartPointIndex) is { } start &&
            DistanceAtSource(alignment.Points, effort.EndPointIndex) is { } end &&
            Math.Abs(start - placement.StartDistanceMeters) <= tolerance &&
            Math.Abs(end - placement.EndDistanceMeters) <= tolerance).ToArray()).ToArray();
        var candidateCounts = candidates.SelectMany(values => values).GroupBy(effort => effort.Id)
            .ToDictionary(group => group.Key, group => group.Count());
        var matches = candidates.Select(values => values.Length switch
        {
            0 => new ChildPlacementMatch(null, SegmentChildComparisonStatus.Missing),
            1 when candidateCounts[values[0].Id] == 1 => new ChildPlacementMatch(ToEffortSummary(values[0], child.Summary.Name), SegmentChildComparisonStatus.Matched),
            _ => new ChildPlacementMatch(null, SegmentChildComparisonStatus.Ambiguous)
        }).ToArray();
        // Even individually unique endpoint matches must retain placement order on overlapping paths.
        var crossed = new HashSet<int>();
        for (var first = 0; first < matches.Length; first++)
        {
            if (matches[first].Effort is not { } earlier) continue;
            for (var second = first + 1; second < matches.Length; second++)
            {
                if (matches[second].Effort is not { } later || earlier.StartPointIndex < later.StartPointIndex) continue;
                crossed.Add(first);
                crossed.Add(second);
            }
        }
        foreach (var index in crossed) matches[index] = new(null, SegmentChildComparisonStatus.Ambiguous);
        return matches;
    }

    private static double? DistanceAtSource(IReadOnlyList<SegmentAlignedPoint> points, double sourcePosition)
    {
        if (points.Count == 0 || sourcePosition < points[0].Source.Position || sourcePosition > points[^1].Source.Position) return null;
        var lower = 0;
        var upper = points.Count - 1;
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            if (points[middle].Source.Position < sourcePosition) lower = middle + 1;
            else upper = middle;
        }
        var after = points[lower];
        if (after.Source.Position == sourcePosition) return after.DistanceMeters;
        if (lower == 0) return null;
        var before = points[lower - 1];
        var fraction = (sourcePosition - before.Source.Position) / (after.Source.Position - before.Source.Position);
        return before.DistanceMeters + (after.DistanceMeters - before.DistanceMeters) * fraction;
    }

    private sealed record ChildPlacementMatch(SegmentEffortSummary? Effort, SegmentChildComparisonStatus Status);
}
