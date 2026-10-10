using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Infrastructure.Services;

internal static class SegmentComparisonCalculator
{
    internal const int MaximumGeometryNodes = 50_001;
    internal const int MaximumCandidateCells = 1_000_000;
    internal const int MaximumDistanceChecks = 5_000_000;
    internal const int MaximumSourcePoints = 250_000;
    internal const double DurationToleranceSeconds = 0.001;
    private const double SampleSpacingMeters = 10;
    private const double DistanceEpsilon = 1e-7;
    private const double EarthRadius = 6_371_000;

    public static SegmentEffortAlignment Align(
        IReadOnlyList<TrackPoint> definition,
        IReadOnlyList<TrackPoint> source,
        SegmentEffortSummary effort,
        double toleranceMeters,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var emptyMap = new ActivityMapProjection([]);
        SegmentEffortAlignment Unavailable(SegmentComparisonUnavailableReason reason, string message, ActivityMapProjection? map = null) =>
            new(effort.Id, [], map ?? emptyMap, reason, message);

        if (effort.StartPointIndex < 0 || effort.EndPointIndex >= source.Count || effort.EndPointIndex <= effort.StartPointIndex)
            return Unavailable(SegmentComparisonUnavailableReason.InvalidInterval, "The effort no longer identifies a valid recorded interval.");
        var count = effort.EndPointIndex - effort.StartPointIndex + 1;
        if (count > MaximumSourcePoints || definition.Count > MaximumSourcePoints)
            return Unavailable(SegmentComparisonUnavailableReason.AnalysisLimit, "This effort exceeds the comparison analysis limit.");
        var range = new ActivityRange(ActivityRangeBoundary.FromPosition(source, effort.StartPointIndex)!,
            ActivityRangeBoundary.FromPosition(source, effort.EndPointIndex)!);
        var map = new ActivityRangeAnalyzer().ProjectMap(source, range, cancellationToken);
        if (definition.Count < 2 || !double.IsFinite(toleranceMeters) || toleranceMeters <= 0 || definition.Any(point => !HasCoordinates(point)))
            return Unavailable(SegmentComparisonUnavailableReason.InsufficientAlignment, "The saved segment has insufficient usable geometry.", map);

        var slice = new TrackPoint[count];
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var point = source[effort.StartPointIndex + index];
            slice[index] = point;
            if (point.Timestamp is null)
                return Unavailable(SegmentComparisonUnavailableReason.InsufficientTiming, "Every point in an effort needs recorded timing for comparison.", map);
            if (!HasCoordinates(point))
                return Unavailable(SegmentComparisonUnavailableReason.Discontinuity, "The effort contains a gap in its recorded GPS path.", map);
            if (index == 0) continue;
            var seconds = (point.Timestamp.Value - slice[index - 1].Timestamp!.Value).TotalSeconds;
            if (seconds <= 0 || seconds > ActivityRangeAnalyzer.MaximumIntervalSeconds)
                return Unavailable(SegmentComparisonUnavailableReason.Discontinuity, "The effort contains missing, reset, or interrupted timing.", map);
        }
        var duration = (slice[^1].Timestamp!.Value - slice[0].Timestamp!.Value).TotalSeconds;
        if (!double.IsFinite(effort.ElapsedSeconds) || effort.ElapsedSeconds <= 0 || Math.Abs(duration - effort.ElapsedSeconds) > DurationToleranceSeconds)
            return Unavailable(SegmentComparisonUnavailableReason.DurationMismatch, "Recorded timing differs from this saved effort. Recompute the segment efforts.", map);

        var definitionDistances = Measure(definition, cancellationToken);
        var sourceDistances = Measure(slice, cancellationToken);
        if (definitionDistances[^1] <= DistanceEpsilon || sourceDistances[^1] <= DistanceEpsilon)
            return Unavailable(SegmentComparisonUnavailableReason.InsufficientAlignment, "The effort needs a usable start-to-finish GPS path.", map);
        var canonical = Resample(definition, definitionDistances, cancellationToken);
        var recorded = Resample(slice, sourceDistances, cancellationToken);
        if (canonical is null || recorded is null)
            return Unavailable(SegmentComparisonUnavailableReason.AnalysisLimit, "This path exceeds the comparison geometry limit.", map);
        var result = AlignGeometry(canonical, recorded, toleranceMeters, cancellationToken);
        if (result.Reason != SegmentComparisonUnavailableReason.None)
            return Unavailable(result.Reason, result.Reason switch
            {
                SegmentComparisonUnavailableReason.AnalysisLimit => "This path exceeds the comparison alignment limit.",
                SegmentComparisonUnavailableReason.AmbiguousAlignment => "The recorded path has more than one equally supported traversal alignment.",
                _ => "The recorded path cannot be aligned continuously to the saved segment within its tolerance."
            }, map);

        var positions = Enumerable.Range(0, count).Select(index => (double)index)
            .Concat(recorded.Select(point => point.SourcePosition)).Order().Distinct().ToArray();
        var aligned = new List<SegmentAlignedPoint>(positions.Length);
        var cursor = 1;
        var definitionCursor = 1;
        foreach (var position in positions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lower = (int)Math.Floor(position);
            var upper = (int)Math.Ceiling(position);
            var fraction = position - lower;
            var geometricDistance = Lerp(sourceDistances[lower], sourceDistances[upper], fraction);
            while (cursor < recorded.Count - 1 && recorded[cursor].Distance < geometricDistance) cursor++;
            var interval = recorded[cursor].Distance - recorded[cursor - 1].Distance;
            var ratio = interval > 0 ? Math.Clamp((geometricDistance - recorded[cursor - 1].Distance) / interval, 0, 1) : 0;
            var canonicalDistance = Lerp(result.Distances![cursor - 1], result.Distances[cursor], ratio);
            if (position == 0) canonicalDistance = 0;
            if (position == count - 1) canonicalDistance = definitionDistances[^1];
            var point = Interpolate(slice[lower], slice[upper], fraction);
            while (definitionCursor < definition.Count - 1 && definitionDistances[definitionCursor] < canonicalDistance) definitionCursor++;
            var canonicalPoint = AtDistance(definition, definitionDistances, canonicalDistance, definitionCursor);
            if (Distance(point, canonicalPoint) > toleranceMeters + DistanceEpsilon)
                return Unavailable(SegmentComparisonUnavailableReason.InsufficientAlignment, "The full recorded path does not support the sampled alignment within the segment tolerance.", map);
            var boundary = ActivityRangeBoundary.FromPosition(source, effort.StartPointIndex + position)!;
            aligned.Add(new(canonicalDistance, boundary,
                (boundary.Timestamp!.Value - slice[0].Timestamp!.Value).TotalSeconds, point));
        }
        return new(effort.Id, aligned, map);
    }

    public static IReadOnlyList<SegmentComparisonSample> BuildSamples(
        SegmentEffortAlignment baseline,
        SegmentEffortAlignment comparison,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!baseline.IsAvailable || !comparison.IsAvailable) return [];
        if (Math.Abs(baseline.Points[^1].DistanceMeters - comparison.Points[^1].DistanceMeters) > DistanceEpsilon) return [];
        var distances = baseline.Points.Select(point => point.DistanceMeters)
            .Concat(comparison.Points.Select(point => point.DistanceMeters)).Order().ToArray();
        var samples = new List<SegmentComparisonSample>();
        var baselineCursor = 0;
        var comparisonCursor = 0;
        double? previousDistance = null;
        foreach (var distance in distances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (previousDistance.HasValue && distance - previousDistance.Value <= DistanceEpsilon) continue;
            previousDistance = distance;
            var before = ValuesAt(baseline.Points, distance, ref baselineCursor);
            var after = ValuesAt(comparison.Points, distance, ref comparisonCursor);
            var dwell = before.Departure.ElapsedSeconds > before.Arrival.ElapsedSeconds || after.Departure.ElapsedSeconds > after.Arrival.ElapsedSeconds;
            if (dwell) samples.Add(new(distance, true, before.Arrival, after.Arrival));
            samples.Add(new(distance, false, before.Departure, after.Departure));
        }
        var baselineSensorCursor = 0;
        var comparisonSensorCursor = 0;
        SegmentComparisonValue? previousBaseline = null;
        SegmentComparisonValue? previousComparison = null;
        for (var index = 0; index < samples.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = samples[index];
            previousBaseline = MarkIncomingGaps(sample.Baseline, previousBaseline, baseline.Points, ref baselineSensorCursor);
            previousComparison = MarkIncomingGaps(sample.Comparison, previousComparison, comparison.Points, ref comparisonSensorCursor);
            samples[index] = sample with { Baseline = previousBaseline, Comparison = previousComparison };
        }
        return samples;
    }

    private static SegmentComparisonValue MarkIncomingGaps(SegmentComparisonValue value, SegmentComparisonValue? previous,
        IReadOnlyList<SegmentAlignedPoint> points, ref int cursor)
    {
        var speedGap = previous?.SpeedMetersPerSecond is null || value.SpeedMetersPerSecond is null;
        var heartGap = previous?.HeartRate is null || value.HeartRate is null;
        var powerGap = previous?.PowerWatts is null || value.PowerWatts is null;
        while (cursor < points.Count && points[cursor].Source.Position <= value.Source.Position)
        {
            var point = points[cursor++].Point;
            speedGap |= Finite(point.SpeedMetersPerSecond) is null;
            heartGap |= Finite(point.HeartRate) is null;
            powerGap |= Finite(point.PowerWatts) is null;
        }
        return value with { SpeedStartsNewRun = speedGap, HeartRateStartsNewRun = heartGap, PowerStartsNewRun = powerGap };
    }

    private static (SegmentComparisonValue Arrival, SegmentComparisonValue Departure) ValuesAt(
        IReadOnlyList<SegmentAlignedPoint> points, double distance, ref int cursor)
    {
        while (cursor < points.Count - 1 && points[cursor].DistanceMeters < distance - DistanceEpsilon) cursor++;
        if (Math.Abs(points[cursor].DistanceMeters - distance) <= DistanceEpsilon)
        {
            var arrival = points[cursor];
            while (cursor < points.Count - 1 && Math.Abs(points[cursor + 1].DistanceMeters - distance) <= DistanceEpsilon) cursor++;
            return (Value(arrival), Value(points[cursor]));
        }
        var after = points[cursor];
        var before = points[Math.Max(0, cursor - 1)];
        var fraction = (distance - before.DistanceMeters) / (after.DistanceMeters - before.DistanceMeters);
        var point = Interpolate(before.Point, after.Point, fraction);
        var position = Lerp(before.Source.Position, after.Source.Position, fraction);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        var boundary = new SourceBoundary(lower, upper, position - lower, point.Timestamp);
        var value = Value(new(distance, boundary, Lerp(before.ElapsedSeconds, after.ElapsedSeconds, fraction), point));
        return (value, value);
    }

    private static SegmentComparisonValue Value(SegmentAlignedPoint point) => new(
        point.Source, point.ElapsedSeconds, Finite(point.Point.SpeedMetersPerSecond), Finite(point.Point.HeartRate),
        Finite(point.Point.PowerWatts), HasCoordinates(point.Point)
            ? new(point.Source.Position, point.Point.Latitude!.Value, point.Point.Longitude!.Value) : null);

    private static AlignmentResult AlignGeometry(IReadOnlyList<GeometryPoint> canonical, IReadOnlyList<GeometryPoint> recorded,
        double tolerance, CancellationToken cancellationToken)
    {
        var buckets = new Dictionary<(long X, long Y, long Z), List<int>>();
        for (var index = 0; index < canonical.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Bucket(canonical[index].Point, tolerance);
            if (!buckets.TryGetValue(key, out var members)) buckets.Add(key, members = []);
            members.Add(index);
        }
        var cells = new List<Cell>();
        var lookup = new Dictionary<long, int>();
        var checks = 0;
        for (var sourceIndex = 0; sourceIndex < recorded.Count; sourceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bucket = Bucket(recorded[sourceIndex].Point, tolerance);
            var candidates = new List<(int Index, double Residual)>();
            for (var x = -1; x <= 1; x++)
                for (var y = -1; y <= 1; y++)
                    for (var z = -1; z <= 1; z++)
                    {
                        if (!buckets.TryGetValue((bucket.X + x, bucket.Y + y, bucket.Z + z), out var members)) continue;
                        foreach (var index in members)
                        {
                            if (++checks > MaximumDistanceChecks) return new(null, SegmentComparisonUnavailableReason.AnalysisLimit);
                            var residual = Distance(canonical[index].Point, recorded[sourceIndex].Point);
                            if (residual <= tolerance) candidates.Add((index, residual * residual));
                        }
                    }
            candidates.Sort((left, right) => left.Index.CompareTo(right.Index));
            foreach (var candidate in candidates)
            {
                if (cells.Count >= MaximumCandidateCells) return new(null, SegmentComparisonUnavailableReason.AnalysisLimit);
                var cell = new Cell(candidate.Index, sourceIndex, candidate.Residual);
                if (candidate.Index == 0 && sourceIndex == 0) cell.Forward = 0;
                Consider(candidate.Index - 1, sourceIndex - 1);
                Consider(candidate.Index - 1, sourceIndex);
                Consider(candidate.Index, sourceIndex - 1);
                lookup.Add(Key(candidate.Index, sourceIndex), cells.Count);
                cells.Add(cell);

                void Consider(int previousDefinition, int previousSource)
                {
                    if (!lookup.TryGetValue(Key(previousDefinition, previousSource), out var previousIndex)) return;
                    var previous = cells[previousIndex];
                    var cost = previous.Forward + EdgeCost(previous, cell, canonical, recorded);
                    if (cost >= cell.Forward) return;
                    cell.Forward = cost;
                    cell.Previous = previousIndex;
                }
            }
        }
        if (!lookup.TryGetValue(Key(canonical.Count - 1, recorded.Count - 1), out var finishIndex) || !double.IsFinite(cells[finishIndex].Forward))
            return new(null, SegmentComparisonUnavailableReason.InsufficientAlignment);
        cells[finishIndex].Reverse = 0;
        for (var index = cells.Count - 1; index >= 0; index--)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var cell = cells[index];
            Consider(cell.Definition + 1, cell.Source + 1);
            Consider(cell.Definition + 1, cell.Source);
            Consider(cell.Definition, cell.Source + 1);
            void Consider(int nextDefinition, int nextSource)
            {
                if (!lookup.TryGetValue(Key(nextDefinition, nextSource), out var nextIndex)) return;
                var next = cells[nextIndex];
                cell.Reverse = Math.Min(cell.Reverse, next.Reverse + EdgeCost(cell, next, canonical, recorded));
            }
        }
        var optimum = cells[finishIndex].Forward;
        var equalityTolerance = 1e-9 * Math.Max(1, optimum);
        var minimum = Enumerable.Repeat(double.PositiveInfinity, recorded.Count).ToArray();
        var maximum = Enumerable.Repeat(double.NegativeInfinity, recorded.Count).ToArray();
        foreach (var cell in cells)
        {
            if (!double.IsFinite(cell.Forward + cell.Reverse) || Math.Abs(cell.Forward + cell.Reverse - optimum) > equalityTolerance) continue;
            var distance = canonical[cell.Definition].Distance;
            minimum[cell.Source] = Math.Min(minimum[cell.Source], distance);
            maximum[cell.Source] = Math.Max(maximum[cell.Source], distance);
        }
        for (var index = 0; index < recorded.Count; index++)
            if (maximum[index] - minimum[index] > 2 * SampleSpacingMeters + DistanceEpsilon)
                return new(null, SegmentComparisonUnavailableReason.AmbiguousAlignment);

        Array.Fill(minimum, double.PositiveInfinity);
        Array.Fill(maximum, double.NegativeInfinity);
        for (var index = finishIndex; index >= 0; index = cells[index].Previous)
        {
            var cell = cells[index];
            minimum[cell.Source] = Math.Min(minimum[cell.Source], canonical[cell.Definition].Distance);
            maximum[cell.Source] = Math.Max(maximum[cell.Source], canonical[cell.Definition].Distance);
        }
        var distances = minimum.Zip(maximum, (first, last) => (first + last) / 2).ToArray();
        distances[0] = 0;
        distances[^1] = canonical[^1].Distance;
        return new(distances, SegmentComparisonUnavailableReason.None);
    }

    private static double EdgeCost(Cell before, Cell after, IReadOnlyList<GeometryPoint> canonical, IReadOnlyList<GeometryPoint> recorded) =>
        (before.ResidualSquared / 2 + after.ResidualSquared / 2) *
        (canonical[after.Definition].Distance - canonical[before.Definition].Distance + recorded[after.Source].Distance - recorded[before.Source].Distance);

    private static long Key(int definition, int source) => ((long)source << 32) | (uint)definition;

    private static (long X, long Y, long Z) Bucket(TrackPoint point, double size)
    {
        var latitude = point.Latitude!.Value * Math.PI / 180;
        var longitude = point.Longitude!.Value * Math.PI / 180;
        var radius = EarthRadius / size;
        return ((long)Math.Floor(radius * Math.Cos(latitude) * Math.Cos(longitude)),
            (long)Math.Floor(radius * Math.Cos(latitude) * Math.Sin(longitude)), (long)Math.Floor(radius * Math.Sin(latitude)));
    }

    private static double[] Measure(IReadOnlyList<TrackPoint> points, CancellationToken cancellationToken)
    {
        var distances = new double[points.Count];
        for (var index = 1; index < points.Count; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            distances[index] = distances[index - 1] + Distance(points[index - 1], points[index]);
        }
        return distances;
    }

    private static IReadOnlyList<GeometryPoint>? Resample(IReadOnlyList<TrackPoint> points, IReadOnlyList<double> distances,
        CancellationToken cancellationToken)
    {
        var requested = Math.Ceiling(distances[^1] / SampleSpacingMeters) + 1;
        if (!double.IsFinite(requested) || requested > MaximumGeometryNodes) return null;
        var count = Math.Max(2, (int)requested);
        var result = new GeometryPoint[count];
        var cursor = 1;
        for (var index = 0; index < count; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var target = distances[^1] * index / (count - 1d);
            while (cursor < points.Count - 1 && distances[cursor] < target) cursor++;
            var span = distances[cursor] - distances[cursor - 1];
            var fraction = span > 0 ? Math.Clamp((target - distances[cursor - 1]) / span, 0, 1) : 0;
            var position = cursor - 1 + fraction;
            if (index == 0) position = 0;
            if (index == count - 1) position = points.Count - 1;
            result[index] = new(target, position, Interpolate(points[(int)Math.Floor(position)], points[(int)Math.Ceiling(position)], position - Math.Floor(position)));
        }
        return result;
    }

    private static TrackPoint AtDistance(IReadOnlyList<TrackPoint> points, IReadOnlyList<double> distances, double distance, int cursor)
    {
        var span = distances[cursor] - distances[cursor - 1];
        return Interpolate(points[cursor - 1], points[cursor], span > 0 ? Math.Clamp((distance - distances[cursor - 1]) / span, 0, 1) : 0);
    }

    private static TrackPoint Interpolate(TrackPoint before, TrackPoint after, double fraction)
    {
        if (fraction <= 0) return before;
        if (fraction >= 1) return after;
        DateTimeOffset? timestamp = before.Timestamp.HasValue && after.Timestamp.HasValue
            ? before.Timestamp.Value.AddTicks((long)Math.Round((after.Timestamp.Value - before.Timestamp.Value).Ticks * fraction)) : null;
        var longitude = InterpolateLongitude(before.Longitude, after.Longitude, fraction);
        return new(timestamp, InterpolateValue(before.Latitude, after.Latitude, fraction), longitude, null,
            InterpolateValue(before.ElevationMeters, after.ElevationMeters, fraction),
            InterpolateValue(before.SpeedMetersPerSecond, after.SpeedMetersPerSecond, fraction),
            InterpolateValue(before.HeartRate, after.HeartRate, fraction), InterpolateValue(before.Cadence, after.Cadence, fraction),
            InterpolateValue(before.PowerWatts, after.PowerWatts, fraction), InterpolateValue(before.TemperatureCelsius, after.TemperatureCelsius, fraction),
            InterpolateValue(before.RespirationRate, after.RespirationRate, fraction));
    }

    private static double? InterpolateLongitude(double? before, double? after, double fraction)
    {
        if (Finite(before) is not double from || Finite(after) is not double to) return null;
        var delta = (to - from + 540) % 360 - 180;
        return (from + delta * fraction + 540) % 360 - 180;
    }

    private static double? InterpolateValue(double? before, double? after, double fraction) =>
        Finite(before) is double from && Finite(after) is double to ? Lerp(from, to, fraction) : null;
    private static double? Finite(double? value) => value.HasValue && double.IsFinite(value.Value) ? value : null;
    private static bool HasCoordinates(TrackPoint point) => point.Latitude is >= -90 and <= 90 && point.Longitude is >= -180 and <= 180;
    private static double Lerp(double before, double after, double fraction) => before * (1 - fraction) + after * fraction;
    private static double Distance(TrackPoint before, TrackPoint after)
    {
        var latitude = (after.Latitude!.Value - before.Latitude!.Value) * Math.PI / 180;
        var longitude = (after.Longitude!.Value - before.Longitude!.Value) * Math.PI / 180;
        var value = Math.Sin(latitude / 2) * Math.Sin(latitude / 2) + Math.Cos(before.Latitude.Value * Math.PI / 180) *
            Math.Cos(after.Latitude.Value * Math.PI / 180) * Math.Sin(longitude / 2) * Math.Sin(longitude / 2);
        return EarthRadius * 2 * Math.Asin(Math.Sqrt(Math.Clamp(value, 0, 1)));
    }

    private sealed record GeometryPoint(double Distance, double SourcePosition, TrackPoint Point);
    private sealed record AlignmentResult(double[]? Distances, SegmentComparisonUnavailableReason Reason);
    private sealed class Cell(int definition, int source, double residualSquared)
    {
        public int Definition { get; } = definition;
        public int Source { get; } = source;
        public double ResidualSquared { get; } = residualSquared;
        public double Forward { get; set; } = double.PositiveInfinity;
        public double Reverse { get; set; } = double.PositiveInfinity;
        public int Previous { get; set; } = -1;
    }
}
