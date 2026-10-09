using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Core.Models;

public sealed record ActivityRangeSection(
    int Id,
    double StartPosition,
    double EndPosition,
    double AxisMinimum,
    double AxisMaximum);

public static class ActivityRangeProjection
{
    public static IReadOnlyList<ActivityRangeSection> BuildSections(
        IReadOnlyList<TrackPoint> points,
        ChartAxisKind axis)
    {
        var values = AxisValues(points, axis);
        var sections = new List<ActivityRangeSection>();
        var start = -1;
        for (var index = 0; index <= values.Count; index++)
        {
            var available = index < values.Count && values[index].HasValue;
            var reset = available && start >= 0 && (axis == ChartAxisKind.ElapsedTime
                ? values[index] <= values[index - 1]
                : values[index] < values[index - 1]);
            if (start >= 0 && (!available || reset))
            {
                sections.Add(new(sections.Count, start, index - 1, values[start]!.Value, values[index - 1]!.Value));
                start = -1;
            }
            if (available && start < 0) start = index;
        }
        return sections;
    }

    public static bool HasOverlappingSections(IReadOnlyList<ActivityRangeSection> sections)
    {
        var ordered = sections.OrderBy(section => section.AxisMinimum).ToArray();
        var maximum = double.NegativeInfinity;
        foreach (var section in ordered)
        {
            if (section.AxisMinimum <= maximum) return true;
            maximum = Math.Max(maximum, section.AxisMaximum);
        }
        return false;
    }

    public static IReadOnlyList<double?> AxisValues(IReadOnlyList<TrackPoint> points, ChartAxisKind axis)
    {
        var firstTime = points.FirstOrDefault(point => point.Timestamp.HasValue)?.Timestamp;
        return points.Select(point => AxisValue(point, firstTime, axis)).ToArray();
    }

    public static double? AxisValue(TrackPoint point, DateTimeOffset? firstTime, ChartAxisKind axis)
    {
        var value = axis == ChartAxisKind.Distance
            ? point.DistanceMeters
            : point.Timestamp.HasValue && firstTime.HasValue
                ? (point.Timestamp.Value - firstTime.Value).TotalSeconds
                : (double?)null;
        return value.HasValue && double.IsFinite(value.Value) ? value : null;
    }

    public static bool CanInterpolate(TrackPoint before, TrackPoint after, double gapSeconds = 30, bool requireTiming = false)
    {
        if (requireTiming && (!before.Timestamp.HasValue || !after.Timestamp.HasValue)) return false;
        if (before.Timestamp.HasValue != after.Timestamp.HasValue) return false;
        if (before.Timestamp.HasValue && after.Timestamp.HasValue)
        {
            var seconds = (after.Timestamp.Value - before.Timestamp.Value).TotalSeconds;
            if (seconds <= 0 || seconds > gapSeconds) return false;
        }
        return !(before.DistanceMeters is double from && after.DistanceMeters is double to &&
                 double.IsFinite(from) && double.IsFinite(to) && to < from);
    }
}
