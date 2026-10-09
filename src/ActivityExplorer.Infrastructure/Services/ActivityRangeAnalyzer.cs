using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class ActivityRangeAnalyzer : IActivityRangeAnalyzer
{
    public const double MaximumIntervalSeconds = 30;

    public ActivityRangeAnalysis Analyze(IReadOnlyList<TrackPoint> points, ActivityRange range, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(range);
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = ResolveRange(points, range);
        var (start, end, ambiguous) = Bounds(resolved);
        var duration = 0d;
        var durationComplete = !ambiguous;
        var distance = new Accumulator();
        var gain = new Accumulator();
        var loss = new Accumulator();
        var heartRate = new Accumulator();
        var power = new Accumulator();
        var cadence = new Accumulator();
        var usesGpsDistance = false;
        var useGpsDistance = !points.Any(point => Finite(point.DistanceMeters));
        for (var index = Math.Max(1, (int)Math.Floor(start) + 1); index < points.Count && index - 1 < end; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var from = Math.Max(start, index - 1) - (index - 1);
            var to = Math.Min(end, index) - (index - 1);
            if (to <= from) continue;
            var before = points[index - 1];
            var after = points[index];
            var seconds = Seconds(before, after);
            if (seconds is null or <= 0)
            {
                durationComplete = false;
                continue;
            }
            var covered = seconds.Value * (to - from);
            duration += covered;
            if (seconds > MaximumIntervalSeconds) continue;
            AddSensor(heartRate, before.HeartRate, after.HeartRate, from, to, covered);
            AddSensor(power, before.PowerWatts, after.PowerWatts, from, to, covered);
            AddSensor(cadence, before.Cadence, after.Cadence, from, to, covered);
            if (TryDistance(before, after, useGpsDistance, out var meters))
            {
                distance.Add(meters * (to - from), covered);
                usesGpsDistance |= useGpsDistance;
            }
            if (Finite(before.ElevationMeters) && Finite(after.ElevationMeters))
            {
                var change = (after.ElevationMeters!.Value - before.ElevationMeters!.Value) * (to - from);
                gain.Add(Math.Max(change, 0), covered);
                loss.Add(Math.Max(-change, 0), covered);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(resolved, duration > 0 ? duration : null, durationComplete && duration > 0,
            distance.Metric(duration, durationComplete), gain.Metric(duration, durationComplete), loss.Metric(duration, durationComplete),
            heartRate.Metric(duration, durationComplete, average: true), power.Metric(duration, durationComplete, average: true),
            cadence.Metric(duration, durationComplete, average: true),
            distance.Seconds > 0 ? distance.Total / distance.Seconds : null, ambiguous, usesGpsDistance,
            ProjectMap(points, resolved, cancellationToken));
    }

    public ActivityMapProjection ProjectMap(IReadOnlyList<TrackPoint> points, ActivityRange? range = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(points);
        cancellationToken.ThrowIfCancellationRequested();
        if (points.Count == 0) return new([]);
        var resolved = range is null ? null : ResolveRange(points, range);
        var (start, end, _) = resolved is null ? (0d, (double)points.Count - 1, false) : Bounds(resolved);
        var runs = new List<IReadOnlyList<ActivityMapPoint>>();
        List<ActivityMapPoint>? current = null;
        for (var index = Math.Max(1, (int)Math.Floor(start) + 1); index < points.Count && index - 1 < end; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = points[index - 1];
            var after = points[index];
            var seconds = Seconds(before, after);
            // A wholly untimed GPS path remains visible as context; selected ranges need known continuity.
            var untimedContext = range is null && before.Timestamp is null && after.Timestamp is null;
            if (!HasCoordinates(before) || !HasCoordinates(after) ||
                (!untimedContext && (seconds is null or <= 0 or > MaximumIntervalSeconds)))
            {
                current = null;
                continue;
            }
            var from = Math.Max(start, index - 1);
            var to = Math.Min(end, index);
            if (to <= from) continue;
            var first = MapPoint(before, after, index - 1, from - (index - 1));
            var last = MapPoint(before, after, index - 1, to - (index - 1));
            if (current is null || Math.Abs(current[^1].SourcePosition - first.SourcePosition) > 1e-9)
            {
                current = [first];
                runs.Add(current);
            }
            current.Add(last);
        }
        return new(runs,
            resolved is null ? null : BoundaryMapPoint(points, resolved.Start),
            resolved is null ? null : BoundaryMapPoint(points, resolved.End));
    }

    private static ActivityRange ResolveRange(IReadOnlyList<TrackPoint> points, ActivityRange range)
    {
        var error = ActivityRangeBoundary.Validate(points, range);
        if (error is not null) throw new ArgumentException(error, nameof(range));
        return new(ActivityRangeBoundary.Resolve(points, range.Start), ActivityRangeBoundary.Resolve(points, range.End));
    }

    private static (double Start, double End, bool Ambiguous) Bounds(ActivityRange range)
    {
        var startAmbiguous = range.Start.UpperIndex - range.Start.LowerIndex > 1;
        var endAmbiguous = range.End.UpperIndex - range.End.LowerIndex > 1;
        return (startAmbiguous ? range.Start.UpperIndex : range.Start.Position,
            endAmbiguous ? range.End.LowerIndex : range.End.Position, startAmbiguous || endAmbiguous);
    }

    private static void AddSensor(Accumulator accumulator, double? before, double? after, double from, double to, double seconds)
    {
        if (!Finite(before) || !Finite(after)) return;
        var first = before!.Value + (after!.Value - before.Value) * from;
        var last = before.Value + (after.Value - before.Value) * to;
        accumulator.Add((first / 2 + last / 2) * seconds, seconds);
    }

    private static bool TryDistance(TrackPoint before, TrackPoint after, bool useGpsDistance, out double distance)
    {
        distance = 0;
        if (!useGpsDistance)
        {
            if (!Finite(before.DistanceMeters) || !Finite(after.DistanceMeters)) return false;
            distance = after.DistanceMeters!.Value - before.DistanceMeters!.Value;
            return double.IsFinite(distance) && distance >= 0;
        }
        if (!HasCoordinates(before) || !HasCoordinates(after)) return false;
        distance = TrackPathAnalysis.HaversineMeters(before.Latitude!.Value, before.Longitude!.Value, after.Latitude!.Value, after.Longitude!.Value);
        return double.IsFinite(distance) && distance >= 0;
    }

    private static ActivityMapPoint? BoundaryMapPoint(IReadOnlyList<TrackPoint> points, SourceBoundary boundary)
    {
        if (boundary.UpperIndex - boundary.LowerIndex > 1) return null;
        var before = points[boundary.LowerIndex];
        var after = points[boundary.UpperIndex];
        if (!HasCoordinates(before) || !HasCoordinates(after)) return null;
        if (boundary.LowerIndex == boundary.UpperIndex)
            return new(boundary.Position, before.Latitude!.Value, before.Longitude!.Value);
        var seconds = Seconds(before, after);
        return seconds is > 0 and <= MaximumIntervalSeconds ? MapPoint(before, after, boundary.LowerIndex, boundary.Fraction) : null;
    }

    private static ActivityMapPoint MapPoint(TrackPoint before, TrackPoint after, int index, double fraction)
    {
        var longitudeDelta = after.Longitude!.Value - before.Longitude!.Value;
        if (longitudeDelta > 180) longitudeDelta -= 360;
        if (longitudeDelta < -180) longitudeDelta += 360;
        var longitude = before.Longitude.Value + longitudeDelta * fraction;
        if (longitude > 180) longitude -= 360;
        if (longitude < -180) longitude += 360;
        return new(index + fraction,
            before.Latitude!.Value + (after.Latitude!.Value - before.Latitude.Value) * fraction, longitude);
    }

    private static bool Finite(double? value) => value.HasValue && double.IsFinite(value.Value);
    private static bool HasCoordinates(TrackPoint point) => point.Latitude is >= -90 and <= 90 && point.Longitude is >= -180 and <= 180;
    private static double? Seconds(TrackPoint before, TrackPoint after) =>
        before.Timestamp.HasValue && after.Timestamp.HasValue ? (after.Timestamp.Value - before.Timestamp.Value).TotalSeconds : null;

    private sealed class Accumulator
    {
        public double Total { get; private set; }
        public double Seconds { get; private set; }
        public void Add(double total, double seconds)
        {
            if (!double.IsFinite(total) || !double.IsFinite(seconds)) return;
            Total += total;
            Seconds += seconds;
        }
        public RangeMetric Metric(double duration, bool durationComplete, bool average = false) => new(
            Seconds > 0 && double.IsFinite(Total) ? average ? Total / Seconds : Total : null,
            Seconds,
            durationComplete && duration > 0 ? Math.Clamp(Seconds / duration * 100, 0, 100) : null,
            durationComplete && duration > 0 && Math.Abs(Seconds - duration) <= 1e-7);
    }
}
