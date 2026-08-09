using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class SegmentMatcher : ISegmentMatcher
{
    private const double SampleIntervalMeters = 10;
    private const double RequiredCoverage = 0.95;
    internal const int MaximumAlignmentSamples = 50_001;

    public Task<IReadOnlyList<SegmentMatch>> MatchAsync(
        IReadOnlyList<TrackPoint> activity,
        IReadOnlyList<TrackPoint> segment,
        double toleranceMeters,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var activityGps = activity.Select((point, index) => new IndexedPoint(point, index))
            .Where(value => HasPosition(value.Point))
            .ToArray();
        var segmentGps = segment.Where(HasPosition).ToArray();
        if (activityGps.Length < 2 || segmentGps.Length < 2)
            return Task.FromResult<IReadOnlyList<SegmentMatch>>([]);

        var startRuns = FindProximityRuns(activityGps, segmentGps[0], toleranceMeters, cancellationToken);
        var endRuns = FindProximityRuns(activityGps, segmentGps[^1], toleranceMeters, cancellationToken);
        if (startRuns.Count == 0 || endRuns.Count == 0)
            return Task.FromResult<IReadOnlyList<SegmentMatch>>([]);

        var segmentPath = MeasuredPath.Create(segmentGps, cancellationToken);
        if (segmentPath.TotalDistanceMeters <= 0)
            return Task.FromResult<IReadOnlyList<SegmentMatch>>([]);

        var segmentSamples = segmentPath.SampleNormalized(
            AlignmentSampleCount(segmentPath.TotalDistanceMeters),
            cancellationToken);

        var matches = new List<SegmentMatch>();
        var searchFrom = 0;
        for (var startRunIndex = 0; startRunIndex < startRuns.Count; startRunIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startRun = startRuns[startRunIndex];
            if (startRun.LastIndex < searchFrom) continue;

            var nextStart = startRunIndex + 1 < startRuns.Count
                ? startRuns[startRunIndex + 1].FirstIndex
                : int.MaxValue;
            var candidates = new List<AlignmentCandidate>();
            foreach (var endRun in endRuns)
            {
                if (endRun.LastIndex <= Math.Max(startRun.FirstIndex, searchFrom)) continue;
                if (endRun.FirstIndex >= nextStart) break;
                var endpoints = SelectClosestOrderedEndpoints(
                    activityGps,
                    startRun,
                    endRun,
                    searchFrom,
                    segmentGps[0],
                    segmentGps[^1],
                    cancellationToken);
                if (endpoints is null) continue;

                var candidate = EvaluateCandidate(
                    activityGps,
                    endpoints.Value,
                    segmentSamples,
                    toleranceMeters,
                    cancellationToken);
                if (candidate.CoveragePercent + 1e-9 < RequiredCoverage * 100) continue;
                if (candidate.EndGpsIndex < nextStart) candidates.Add(candidate);
            }

            if (candidates.Count == 0) continue;
            candidates.Sort(CompareCandidates);
            var accepted = candidates[0];
            matches.Add(new SegmentMatch(
                activityGps[accepted.StartGpsIndex].SourceIndex,
                activityGps[accepted.EndGpsIndex].SourceIndex,
                accepted.CoveragePercent,
                accepted.MeanDistanceMeters));
            searchFrom = accepted.EndGpsIndex + 1;
        }

        return Task.FromResult<IReadOnlyList<SegmentMatch>>(matches);
    }

    private static IReadOnlyList<ProximityRun> FindProximityRuns(
        IReadOnlyList<IndexedPoint> activity,
        TrackPoint endpoint,
        double toleranceMeters,
        CancellationToken cancellationToken)
    {
        var runs = new List<ProximityRun>();
        var first = -1;
        for (var index = 0; index < activity.Count; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (Distance(activity[index].Point, endpoint) <= toleranceMeters)
            {
                if (first < 0) first = index;
            }
            else if (first >= 0)
            {
                runs.Add(new ProximityRun(first, index - 1));
                first = -1;
            }
        }

        if (first >= 0) runs.Add(new ProximityRun(first, activity.Count - 1));
        return runs;
    }

    private static EndpointPair? SelectClosestOrderedEndpoints(
        IReadOnlyList<IndexedPoint> activity,
        ProximityRun startRun,
        ProximityRun endRun,
        int searchFrom,
        TrackPoint segmentStart,
        TrackPoint segmentEnd,
        CancellationToken cancellationToken)
    {
        var firstStart = Math.Max(startRun.FirstIndex, searchFrom);
        var lastStart = startRun.LastIndex;
        if (firstStart > lastStart || endRun.LastIndex <= firstStart) return null;

        var startCursor = firstStart;
        var closestStartIndex = -1;
        var closestStartDistance = double.MaxValue;
        EndpointPair? best = null;
        for (var endIndex = endRun.FirstIndex; endIndex <= endRun.LastIndex; endIndex++)
        {
            if ((endIndex & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            while (startCursor <= lastStart && startCursor < endIndex)
            {
                var distance = Distance(activity[startCursor].Point, segmentStart);
                if (distance < closestStartDistance - 1e-9 ||
                    Math.Abs(distance - closestStartDistance) <= 1e-9 && startCursor < closestStartIndex)
                {
                    closestStartDistance = distance;
                    closestStartIndex = startCursor;
                }

                startCursor++;
            }

            if (closestStartIndex < 0) continue;
            var endDistance = Distance(activity[endIndex].Point, segmentEnd);
            var candidate = new EndpointPair(closestStartIndex, endIndex, closestStartDistance, endDistance);
            if (best is null || CompareEndpoints(candidate, best.Value) < 0) best = candidate;
        }

        return best;
    }

    private static int CompareEndpoints(EndpointPair left, EndpointPair right)
    {
        var comparison = (left.StartDistanceMeters + left.EndDistanceMeters)
            .CompareTo(right.StartDistanceMeters + right.EndDistanceMeters);
        if (comparison != 0) return comparison;
        comparison = left.StartDistanceMeters.CompareTo(right.StartDistanceMeters);
        if (comparison != 0) return comparison;
        comparison = left.EndDistanceMeters.CompareTo(right.EndDistanceMeters);
        if (comparison != 0) return comparison;
        comparison = left.StartGpsIndex.CompareTo(right.StartGpsIndex);
        return comparison != 0 ? comparison : left.EndGpsIndex.CompareTo(right.EndGpsIndex);
    }

    private static AlignmentCandidate EvaluateCandidate(
        IReadOnlyList<IndexedPoint> activity,
        EndpointPair endpoints,
        IReadOnlyList<PathCoordinate> segmentSamples,
        double toleranceMeters,
        CancellationToken cancellationToken)
    {
        var candidatePoints = new PathCoordinate[endpoints.EndGpsIndex - endpoints.StartGpsIndex + 1];
        for (var index = 0; index < candidatePoints.Length; index++)
            candidatePoints[index] = PathCoordinate.From(activity[endpoints.StartGpsIndex + index].Point);

        var candidatePath = MeasuredPath.Create(candidatePoints, cancellationToken);
        if (candidatePath.TotalDistanceMeters <= 0)
            return new AlignmentCandidate(
                endpoints.StartGpsIndex,
                endpoints.EndGpsIndex,
                0,
                double.MaxValue,
                endpoints.StartDistanceMeters + endpoints.EndDistanceMeters);

        var candidateSamples = candidatePath.SampleNormalized(segmentSamples.Count, cancellationToken);
        var matched = 0;
        var distanceSum = 0d;
        for (var index = 0; index < segmentSamples.Count; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var distance = Distance(segmentSamples[index], candidateSamples[index]);
            distanceSum += distance;
            if (distance <= toleranceMeters) matched++;
        }

        return new AlignmentCandidate(
            endpoints.StartGpsIndex,
            endpoints.EndGpsIndex,
            matched * 100d / segmentSamples.Count,
            distanceSum / segmentSamples.Count,
            endpoints.StartDistanceMeters + endpoints.EndDistanceMeters);
    }

    private static int CompareCandidates(AlignmentCandidate left, AlignmentCandidate right)
    {
        var comparison = right.CoveragePercent.CompareTo(left.CoveragePercent);
        if (comparison != 0) return comparison;
        comparison = left.MeanDistanceMeters.CompareTo(right.MeanDistanceMeters);
        if (comparison != 0) return comparison;
        comparison = left.EndpointDistanceMeters.CompareTo(right.EndpointDistanceMeters);
        if (comparison != 0) return comparison;
        comparison = left.StartGpsIndex.CompareTo(right.StartGpsIndex);
        return comparison != 0 ? comparison : left.EndGpsIndex.CompareTo(right.EndGpsIndex);
    }

    internal static int AlignmentSampleCount(double totalDistanceMeters)
    {
        if (!double.IsFinite(totalDistanceMeters) || totalDistanceMeters <= 0) return 2;
        var requested = Math.Ceiling(totalDistanceMeters / SampleIntervalMeters) + 1;
        return (int)Math.Clamp(requested, 2, MaximumAlignmentSamples);
    }

    private static bool HasPosition(TrackPoint point) =>
        point.Latitude.HasValue && point.Longitude.HasValue &&
        double.IsFinite(point.Latitude.Value) && double.IsFinite(point.Longitude.Value);

    private static double Distance(TrackPoint a, TrackPoint b) =>
        GeometryCodec.HaversineMeters(a.Latitude!.Value, a.Longitude!.Value, b.Latitude!.Value, b.Longitude!.Value);

    private static double Distance(PathCoordinate a, PathCoordinate b) =>
        GeometryCodec.HaversineMeters(a.Latitude, a.Longitude, b.Latitude, b.Longitude);

    private static double InterpolateLongitude(double from, double to, double ratio)
    {
        var delta = (to - from + 540) % 360 - 180;
        var result = from + delta * ratio;
        return (result + 540) % 360 - 180;
    }

    private readonly record struct IndexedPoint(TrackPoint Point, int SourceIndex);
    private readonly record struct PathCoordinate(double Latitude, double Longitude)
    {
        public static PathCoordinate From(TrackPoint point) =>
            new(point.Latitude!.Value, point.Longitude!.Value);
    }
    private readonly record struct ProximityRun(int FirstIndex, int LastIndex);
    private readonly record struct EndpointPair(
        int StartGpsIndex,
        int EndGpsIndex,
        double StartDistanceMeters,
        double EndDistanceMeters);
    private sealed record AlignmentCandidate(
        int StartGpsIndex,
        int EndGpsIndex,
        double CoveragePercent,
        double MeanDistanceMeters,
        double EndpointDistanceMeters);

    private sealed class MeasuredPath
    {
        private MeasuredPath(IReadOnlyList<PathCoordinate> points, IReadOnlyList<double> cumulativeDistanceMeters)
        {
            Points = points;
            CumulativeDistanceMeters = cumulativeDistanceMeters;
        }

        private IReadOnlyList<PathCoordinate> Points { get; }
        private IReadOnlyList<double> CumulativeDistanceMeters { get; }
        public double TotalDistanceMeters => CumulativeDistanceMeters[^1];

        public static MeasuredPath Create(IReadOnlyList<TrackPoint> points, CancellationToken cancellationToken)
        {
            var coordinates = new PathCoordinate[points.Count];
            for (var index = 0; index < points.Count; index++)
                coordinates[index] = PathCoordinate.From(points[index]);
            return Create(coordinates, cancellationToken);
        }

        public static MeasuredPath Create(IReadOnlyList<PathCoordinate> points, CancellationToken cancellationToken)
        {
            var cumulative = new double[points.Count];
            for (var index = 1; index < points.Count; index++)
            {
                if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                cumulative[index] = cumulative[index - 1] + Distance(points[index - 1], points[index]);
            }

            return new MeasuredPath(points, cumulative);
        }

        public IReadOnlyList<PathCoordinate> SampleNormalized(int count, CancellationToken cancellationToken)
        {
            var result = new PathCoordinate[count];
            var cursor = 1;
            for (var sampleIndex = 0; sampleIndex < count; sampleIndex++)
            {
                if ((sampleIndex & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var target = TotalDistanceMeters * sampleIndex / (count - 1d);
                while (cursor < CumulativeDistanceMeters.Count - 1 && CumulativeDistanceMeters[cursor] < target)
                    cursor++;

                var left = Math.Max(0, cursor - 1);
                var span = CumulativeDistanceMeters[cursor] - CumulativeDistanceMeters[left];
                var ratio = span <= 0 ? 0 : (target - CumulativeDistanceMeters[left]) / span;
                var from = Points[left];
                var to = Points[cursor];
                result[sampleIndex] = new PathCoordinate(
                    from.Latitude + (to.Latitude - from.Latitude) * ratio,
                    InterpolateLongitude(from.Longitude, to.Longitude, ratio));
            }

            return result;
        }
    }
}
