using System.Globalization;
using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Core.Models;

public enum ChartAxisKind { ElapsedTime = 1, Distance = 2 }

public sealed record ChartSample(int SourceIndex, double X, double Value, bool StartsNewSegment);

public sealed record ChartSeriesData(
    IReadOnlyList<ChartSample> Samples,
    double? Minimum,
    double? Maximum,
    double? Average,
    double CoveragePercent,
    double AxisMaximum,
    double AxisMinimum = 0);

public sealed record ChartRangeSample(double SourcePosition, double X, double Value, bool StartsNewSegment);

public static class ChartSeriesBuilder
{
    public static ChartSeriesData Build(
        IReadOnlyList<TrackPoint> points,
        Func<TrackPoint, double?> selector,
        ChartAxisKind axis,
        int maximumSamples = 600,
        double gapSeconds = 30)
    {
        if (points.Count == 0) return new ChartSeriesData([], null, null, null, 0, 0);
        var firstTime = points.FirstOrDefault(x => x.Timestamp.HasValue)?.Timestamp;
        var raw = new List<(int Index, double X, double Value)>();
        for (var index = 0; index < points.Count; index++)
        {
            var value = selector(points[index]);
            var x = ActivityRangeProjection.AxisValue(points[index], firstTime, axis);
            if (value.HasValue && x.HasValue && double.IsFinite(value.Value) && double.IsFinite(x.Value))
                raw.Add((index, x.Value, value.Value));
        }

        if (raw.Count == 0)
            return new ChartSeriesData([], null, null, null, 0, AxisMaximum(points, firstTime, axis), AxisMinimum(points, firstTime, axis));

        var selected = Downsample(raw, Math.Max(20, maximumSamples));
        var samples = new List<ChartSample>(selected.Count);
        (int Index, double X, double Value)? previous = null;
        foreach (var item in selected)
        {
            var startsNew = previous is null || HasGap(points, selector, previous.Value.Index, item.Index, gapSeconds, axis);
            samples.Add(new ChartSample(item.Index, item.X, item.Value, startsNew));
            previous = item;
        }

        var values = raw.Select(x => x.Value).ToArray();
        return new ChartSeriesData(
            samples,
            values.Min(),
            values.Max(),
            values.Average(),
            raw.Count * 100d / points.Count,
            Math.Max(AxisMaximum(points, firstTime, axis), raw.Max(x => x.X)),
            AxisMinimum(points, firstTime, axis));
    }

    public static IReadOnlyList<string> ToSvgSegments(
        ChartSeriesData series,
        double width = 800,
        double height = 180,
        double verticalPadding = 14,
        double? plotMinimum = null,
        double? plotMaximum = null)
    {
        if (series.Samples.Count < 2 || !series.Minimum.HasValue || !series.Maximum.HasValue) return [];
        var minimum = plotMinimum ?? series.Minimum.Value;
        var maximum = plotMaximum ?? series.Maximum.Value;
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
        {
            minimum = series.Minimum.Value;
            maximum = series.Maximum.Value;
        }
        var hasValueRange = maximum > minimum;
        var valueScale = hasValueRange ? Math.Max(Math.Abs(minimum), Math.Abs(maximum)) : 1;
        var scaledMinimum = minimum / valueScale;
        var scaledMaximum = maximum / valueScale;
        var scaledSpan = scaledMaximum - scaledMinimum;
        if (hasValueRange && (!double.IsFinite(scaledSpan) || scaledSpan <= 0)) return [];
        var axisSpan = Math.Max(series.AxisMaximum - series.AxisMinimum, 1e-9);
        var plotHeight = height - verticalPadding * 2;
        var result = new List<string>();
        var current = new List<string>();
        foreach (var sample in series.Samples)
        {
            if (sample.StartsNewSegment && current.Count > 1)
            {
                result.Add(string.Join(" ", current));
                current.Clear();
            }
            else if (sample.StartsNewSegment)
            {
                current.Clear();
            }

            var x = width * Math.Clamp((sample.X - series.AxisMinimum) / axisSpan, 0, 1);
            var valueFraction = hasValueRange
                ? (sample.Value / valueScale - scaledMinimum) / scaledSpan
                : 0.5;
            var y = height - verticalPadding - plotHeight * Math.Clamp(valueFraction, 0, 1);
            current.Add(string.Create(CultureInfo.InvariantCulture, $"{x:F1},{y:F1}"));
        }
        if (current.Count > 1) result.Add(string.Join(" ", current));
        return result;
    }

    private static List<(int Index, double X, double Value)> Downsample(
        IReadOnlyList<(int Index, double X, double Value)> samples,
        int maximumSamples)
    {
        if (samples.Count <= maximumSamples) return samples.ToList();
        var bucketCount = Math.Max(1, maximumSamples / 2);
        var result = new List<(int Index, double X, double Value)>(maximumSamples);
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var start = bucket * samples.Count / bucketCount;
            var end = Math.Min(samples.Count, (bucket + 1) * samples.Count / bucketCount);
            if (start >= end) continue;
            var range = samples.Skip(start).Take(end - start).ToArray();
            var minimum = range.MinBy(x => x.Value);
            var maximum = range.MaxBy(x => x.Value);
            result.Add(minimum);
            if (maximum.Index != minimum.Index) result.Add(maximum);
        }
        return result.OrderBy(x => x.Index).Take(maximumSamples).ToList();
    }

    private static bool HasGap(
        IReadOnlyList<TrackPoint> points,
        Func<TrackPoint, double?> selector,
        int previous,
        int current,
        double gapSeconds,
        ChartAxisKind axis)
    {
        for (var index = previous + 1; index <= current; index++)
        {
            var value = selector(points[index]);
            if (!value.HasValue || !double.IsFinite(value.Value)) return true;
            if (axis == ChartAxisKind.Distance &&
                (points[index].DistanceMeters is not double distance || !double.IsFinite(distance))) return true;
            if (!ActivityRangeProjection.CanInterpolate(points[index - 1], points[index], gapSeconds)) return true;
        }
        return false;
    }

    private static double AxisMaximum(IReadOnlyList<TrackPoint> points, DateTimeOffset? firstTime, ChartAxisKind axis)
    {
        var values = points.Select(point => ActivityRangeProjection.AxisValue(point, firstTime, axis)).Where(x => x.HasValue).Select(x => x!.Value);
        return values.DefaultIfEmpty(0).Max();
    }

    private static double AxisMinimum(IReadOnlyList<TrackPoint> points, DateTimeOffset? firstTime, ChartAxisKind axis) =>
        Math.Min(0, points.Select(point => ActivityRangeProjection.AxisValue(point, firstTime, axis))
            .Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty(0).Min());

    public static IReadOnlyList<ChartRangeSample> BuildRangeSamples(
        IReadOnlyList<TrackPoint> points,
        Func<TrackPoint, double?> selector,
        ChartAxisKind axis,
        double gapSeconds = 30)
    {
        var values = ActivityRangeProjection.AxisValues(points, axis);
        var samples = new List<ChartRangeSample>();
        var previousIndex = -2;
        for (var index = 0; index < points.Count; index++)
        {
            var value = selector(points[index]);
            if (values[index] is not double x || value is not double y || !double.IsFinite(y)) continue;
            var startsNew = previousIndex != index - 1 ||
                            !ActivityRangeProjection.CanInterpolate(points[index - 1], points[index], gapSeconds, requireTiming: true);
            samples.Add(new(index, x, y, startsNew));
            previousIndex = index;
        }
        return samples;
    }

    public static IReadOnlyList<ChartRangeSample> ClipRangeSamples(
        IReadOnlyList<ChartRangeSample> samples,
        double startPosition,
        double endPosition)
    {
        if (!double.IsFinite(startPosition) || !double.IsFinite(endPosition) || startPosition > endPosition) return [];
        var result = new List<ChartRangeSample>();
        ChartRangeSample? previous = null;
        foreach (var sample in samples)
        {
            if (previous is not null && !sample.StartsNewSegment && previous.SourcePosition < startPosition && sample.SourcePosition > startPosition)
                result.Add(Interpolate(previous, sample, startPosition, true));
            if (sample.SourcePosition >= startPosition && sample.SourcePosition <= endPosition)
                result.Add(sample with { StartsNewSegment = result.Count == 0 || sample.StartsNewSegment });
            if (previous is not null && !sample.StartsNewSegment && previous.SourcePosition < endPosition && sample.SourcePosition > endPosition && endPosition >= startPosition)
                result.Add(Interpolate(previous, sample, endPosition, result.Count == 0));
            if (sample.SourcePosition > endPosition) break;
            previous = sample;
        }
        return result;
    }

    private static ChartRangeSample Interpolate(ChartRangeSample before, ChartRangeSample after, double position, bool startsNew)
    {
        var fraction = (position - before.SourcePosition) / (after.SourcePosition - before.SourcePosition);
        return new(position, before.X * (1 - fraction) + after.X * fraction,
            before.Value * (1 - fraction) + after.Value * fraction, startsNew);
    }
}
