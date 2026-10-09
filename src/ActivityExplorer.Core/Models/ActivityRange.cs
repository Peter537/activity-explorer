using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Core.Models;

public sealed record SourceBoundary(int LowerIndex, int UpperIndex, double Fraction, DateTimeOffset? Timestamp)
{
    public double Position => LowerIndex + (UpperIndex - LowerIndex) * Fraction;
}

public sealed record ActivityRange(SourceBoundary Start, SourceBoundary End);

public sealed record RangeMetric(double? Value, double CoveredSeconds, double? CoveragePercent, bool IsComplete);

public sealed record ActivityMapPoint(double SourcePosition, double Latitude, double Longitude);

public sealed record ActivityMapProjection(
    IReadOnlyList<IReadOnlyList<ActivityMapPoint>> Runs,
    ActivityMapPoint? Start = null, ActivityMapPoint? End = null);

public sealed record ActivityRangeAnalysis(
    ActivityRange Range, double? DurationSeconds, bool DurationComplete,
    RangeMetric Distance, RangeMetric ElevationGain, RangeMetric ElevationLoss,
    RangeMetric HeartRate, RangeMetric Power, RangeMetric Cadence,
    double? AverageSpeedMetersPerSecond, bool HasAmbiguousBoundaries, bool UsesGpsDistance,
    ActivityMapProjection Map);

public static class ActivityRangeBoundary
{
    public static SourceBoundary? FromPosition(IReadOnlyList<TrackPoint> points, double position)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (!double.IsFinite(position) || position < 0 || position > points.Count - 1) return null;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        var fraction = position - lower;
        return new(lower, upper, fraction, InterpolateTime(points[lower].Timestamp, points[upper].Timestamp, fraction));
    }

    public static SourceBoundary Resolve(IReadOnlyList<TrackPoint> points, SourceBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(boundary);
        if (BoundaryError(points, boundary) is not null) return boundary;
        if (boundary.Fraction == 0) return FromPosition(points, boundary.LowerIndex)!;
        if (boundary.Fraction == 1) return FromPosition(points, boundary.UpperIndex)!;
        if (boundary.UpperIndex - boundary.LowerIndex <= 1 || boundary.Timestamp is null) return boundary;
        for (var index = boundary.LowerIndex + 1; index <= boundary.UpperIndex; index++)
            if (points[index - 1].Timestamp is not { } before || points[index].Timestamp is not { } after || after <= before)
                return boundary;
        for (var index = boundary.LowerIndex + 1; index <= boundary.UpperIndex; index++)
        {
            var before = points[index - 1].Timestamp!.Value;
            var after = points[index].Timestamp!.Value;
            if (boundary.Timestamp.Value > after) continue;
            if (boundary.Timestamp.Value == before) return FromPosition(points, index - 1)!;
            if (boundary.Timestamp.Value == after) return FromPosition(points, index)!;
            var fraction = (boundary.Timestamp.Value - before).TotalSeconds / (after - before).TotalSeconds;
            return new(index - 1, index, fraction, boundary.Timestamp);
        }
        return boundary;
    }

    public static string? Validate(IReadOnlyList<TrackPoint> points, ActivityRange range)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(range);
        var error = BoundaryError(points, range.Start) ?? BoundaryError(points, range.End);
        if (error is not null) return error;
        if (range.Start.Position >= range.End.Position)
            return "The range start must precede its end.";
        var start = Resolve(points, range.Start);
        var end = Resolve(points, range.End);
        return start.Position < end.Position ? null : "The resolved range start must precede its end.";
    }

    private static string? BoundaryError(IReadOnlyList<TrackPoint> points, SourceBoundary boundary)
    {
        if (boundary.LowerIndex < 0 || boundary.UpperIndex < boundary.LowerIndex || boundary.UpperIndex >= points.Count ||
            !double.IsFinite(boundary.Fraction) || boundary.Fraction is < 0 or > 1 ||
            (boundary.LowerIndex == boundary.UpperIndex && boundary.Fraction != 0))
            return "The selected range is outside the recorded stream.";
        var expected = InterpolateTime(points[boundary.LowerIndex].Timestamp, points[boundary.UpperIndex].Timestamp, boundary.Fraction);
        if (expected.HasValue != boundary.Timestamp.HasValue ||
            (expected.HasValue && Math.Abs((expected.Value - boundary.Timestamp!.Value).Ticks) > 1))
            return "The selected range no longer matches the recorded stream.";
        return null;
    }

    private static DateTimeOffset? InterpolateTime(DateTimeOffset? before, DateTimeOffset? after, double fraction)
    {
        if (fraction == 0) return before;
        if (fraction == 1) return after;
        return before.HasValue && after.HasValue
            ? before.Value.AddTicks((long)Math.Round((after.Value - before.Value).Ticks * fraction))
            : null;
    }
}
