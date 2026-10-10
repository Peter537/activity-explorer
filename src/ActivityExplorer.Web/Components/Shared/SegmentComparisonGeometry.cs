using System.Globalization;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

#pragma warning disable CA1716 // Shared is the established component namespace.
namespace ActivityExplorer.Web.Components.Shared;

internal sealed record ComparisonPlotPoint(int SampleIndex, double X, double Y);
internal sealed record ComparisonPlotSeries(IReadOnlyList<string> Lines, IReadOnlyList<ComparisonPlotPoint> IsolatedPoints);

internal static class SegmentComparisonGeometry
{
    public static double? Value(SegmentComparisonSample sample, int metric, bool baseline, SportKind sport)
    {
        var value = baseline ? sample.Baseline : sample.Comparison;
        return metric switch
        {
            0 => sample.DeltaSeconds,
            1 => sport == SportKind.Cycling && value.SpeedMetersPerSecond == 0
                ? 0 : Format.SpeedOrPace(value.SpeedMetersPerSecond, sport),
            2 => value.HeartRate,
            3 => value.PowerWatts,
            _ => null
        };
    }

    // Share retained positions across all plots, including extrema from each effort and sensor.
    public static HashSet<int> RetainedSamples(IReadOnlyList<SegmentComparisonSample> samples, SportKind sport)
    {
        const int maximum = 600;
        if (samples.Count <= maximum) return Enumerable.Range(0, samples.Count).ToHashSet();
        var retained = new HashSet<int> { 0, samples.Count - 1 };
        const int bucketCount = (maximum / 2 - 2) / 16;
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var start = bucket * samples.Count / bucketCount;
            var end = (bucket + 1) * samples.Count / bucketCount;
            retained.Add(start);
            retained.Add(end - 1);
            for (var metric = 0; metric < 4; metric++)
                for (var side = 0; side < (metric == 0 ? 1 : 2); side++)
                {
                    var minimum = double.PositiveInfinity;
                    var maximumValue = double.NegativeInfinity;
                    var minimumIndex = -1;
                    var maximumIndex = -1;
                    for (var index = start; index < end; index++)
                    {
                        if (Value(samples[index], metric, side == 0, sport) is not double value || !double.IsFinite(value)) continue;
                        if (value < minimum) { minimum = value; minimumIndex = index; }
                        if (value > maximumValue) { maximumValue = value; maximumIndex = index; }
                    }
                    if (minimumIndex >= 0) retained.Add(minimumIndex);
                    if (maximumIndex >= 0) retained.Add(maximumIndex);
                }
        }
        // Reserve half the budget so a retained pause endpoint can keep its same-distance partner.
        for (var start = 0; start < samples.Count;)
        {
            var end = start + 1;
            while (end < samples.Count && samples[end].DistanceMeters == samples[start].DistanceMeters) end++;
            if (end - start > 1)
            {
                var present = 0;
                for (var index = start; index < end; index++)
                    if (retained.Contains(index)) present++;
                if (present > 0 && retained.Count + end - start - present <= maximum)
                    for (var index = start; index < end; index++) retained.Add(index);
            }
            start = end;
        }
        return retained;
    }

    public static ComparisonPlotSeries BuildSeries(IReadOnlyList<SegmentComparisonSample> samples,
        IReadOnlySet<int> retained, int metric, bool baseline, SportKind sport, ChartScaleGeometry scale, double distance)
    {
        var lines = new List<string>();
        var isolated = new List<ComparisonPlotPoint>();
        var current = new List<ComparisonPlotPoint>();
        void CompleteRun()
        {
            if (current.Count > 1)
                lines.Add(string.Join(" ", current.Select(point => $"{Invariant(point.X)},{Invariant(point.Y)}")));
            else if (current.Count == 1) isolated.Add(current[0]);
            current.Clear();
        }
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            if (metric == 0 && index > 0 && sample.DistanceMeters == samples[index - 1].DistanceMeters &&
                (!retained.Contains(index - 1) || !retained.Contains(index))) CompleteRun();
            var effort = baseline ? sample.Baseline : sample.Comparison;
            if (metric switch { 1 => effort.SpeedStartsNewRun, 2 => effort.HeartRateStartsNewRun, 3 => effort.PowerStartsNewRun, _ => false })
                CompleteRun();
            if (Value(sample, metric, baseline, sport) is not double value || !double.IsFinite(value))
            {
                // Inspect every source sample so decimation cannot connect across a missing value.
                CompleteRun();
                continue;
            }
            if (!retained.Contains(index)) continue;
            current.Add(new(index, 800 * sample.DistanceMeters / Math.Max(distance, 1e-9), scale.Project(value, 180)));
        }
        CompleteRun();
        return new(lines, isolated);
    }

    public static string Invariant(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
