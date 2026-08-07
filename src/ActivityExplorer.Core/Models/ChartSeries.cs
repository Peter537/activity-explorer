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
    double AxisMaximum);

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
            var x = AxisValue(points[index], firstTime, axis);
            if (value.HasValue && x.HasValue && double.IsFinite(value.Value) && double.IsFinite(x.Value))
                raw.Add((index, x.Value, value.Value));
        }

        if (raw.Count == 0)
            return new ChartSeriesData([], null, null, null, 0, AxisMaximum(points, firstTime, axis));

        var selected = Downsample(raw, Math.Max(20, maximumSamples));
        var samples = new List<ChartSample>(selected.Count);
        (int Index, double X, double Value)? previous = null;
        foreach (var item in selected)
        {
            var startsNew = previous is null || HasGap(points, selector, previous.Value.Index, item.Index, gapSeconds);
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
            Math.Max(AxisMaximum(points, firstTime, axis), raw.Max(x => x.X)));
    }

    public static IReadOnlyList<string> ToSvgSegments(
        ChartSeriesData series,
        double width = 800,
        double height = 180,
        double verticalPadding = 14)
    {
        if (series.Samples.Count < 2 || !series.Minimum.HasValue || !series.Maximum.HasValue) return [];
        var valueSpan = Math.Max(series.Maximum.Value - series.Minimum.Value, 1e-9);
        var axisSpan = Math.Max(series.AxisMaximum, 1e-9);
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

            var x = width * sample.X / axisSpan;
            var y = height - verticalPadding - (height - verticalPadding * 2) * (sample.Value - series.Minimum.Value) / valueSpan;
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
        double gapSeconds)
    {
        for (var index = previous + 1; index <= current; index++)
        {
            var value = selector(points[index]);
            if (!value.HasValue || !double.IsFinite(value.Value)) return true;
            var before = points[index - 1].Timestamp;
            var after = points[index].Timestamp;
            if (before.HasValue && after.HasValue && (after.Value - before.Value).TotalSeconds > gapSeconds)
                return true;
        }
        return false;
    }

    private static double? AxisValue(TrackPoint point, DateTimeOffset? firstTime, ChartAxisKind axis) =>
        axis == ChartAxisKind.Distance
            ? point.DistanceMeters
            : point.Timestamp.HasValue && firstTime.HasValue
                ? Math.Max(0, (point.Timestamp.Value - firstTime.Value).TotalSeconds)
                : null;

    private static double AxisMaximum(IReadOnlyList<TrackPoint> points, DateTimeOffset? firstTime, ChartAxisKind axis)
    {
        var values = points.Select(point => AxisValue(point, firstTime, axis)).Where(x => x.HasValue).Select(x => x!.Value);
        return values.DefaultIfEmpty(0).Max();
    }
}
