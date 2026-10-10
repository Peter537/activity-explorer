using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Components.Shared;

namespace ActivityExplorer.Tests;

public sealed class SegmentComparisonGeometryTests
{
    [Fact]
    public void Shared_downsampling_is_bounded_and_retains_sensor_and_time_gap_extrema()
    {
        var samples = Enumerable.Range(0, 10_000).Select(index => Sample(index, 100, 150, index)).ToArray();
        samples[423] = samples[423] with { Comparison = samples[423].Comparison with { ElapsedSeconds = 9000 } };
        samples[887] = samples[887] with { Baseline = samples[887].Baseline with { HeartRate = 220 } };
        samples[999] = samples[999] with { Comparison = samples[999].Comparison with { PowerWatts = 2000 } };

        var retained = SegmentComparisonGeometry.RetainedSamples(samples, SportKind.Cycling);

        Assert.InRange(retained.Count, 4, 600);
        Assert.Contains(0, retained);
        Assert.Contains(9999, retained);
        Assert.Contains(423, retained);
        Assert.Contains(887, retained);
        Assert.Contains(999, retained);
    }

    [Fact]
    public void Decimation_cannot_bridge_a_missing_sensor_or_a_hidden_dwell_gap()
    {
        var samples = Enumerable.Range(0, 6).Select(index => Sample(index, 100, 150, index)).ToArray();
        var retained = new HashSet<int> { 0, 1, 4, 5 };
        var scale = ChartScaleBuilder.Build(0, 200);
        samples[2] = samples[2] with { Baseline = samples[2].Baseline with { HeartRate = null } };
        var missing = SegmentComparisonGeometry.BuildSeries(samples, retained, 2, true, SportKind.Cycling, scale, 5);
        Assert.Equal(2, missing.Lines.Count);
        Assert.Empty(missing.IsolatedPoints);

        samples[2] = samples[2] with { Baseline = samples[2].Baseline with { HeartRate = 120, HeartRateStartsNewRun = true } };
        var hidden = SegmentComparisonGeometry.BuildSeries(samples, retained, 2, true, SportKind.Cycling, scale, 5);
        Assert.Equal(missing.Lines, hidden.Lines);

        var continuous = SegmentComparisonGeometry.BuildSeries(samples, retained, 2, false, SportKind.Cycling, scale, 5);
        Assert.Single(continuous.Lines);
    }

    [Fact]
    public void Dense_pauses_remain_vertical_or_a_gap_and_never_become_a_diagonal_shortcut()
    {
        var samples = Enumerable.Range(0, 2000).SelectMany(index =>
        {
            var arrival = Sample(index, 100, 150, index * 2) with { IsArrival = true };
            return new[] { arrival, arrival with { IsArrival = false, Comparison = arrival.Comparison with { ElapsedSeconds = index * 2 + 1 } } };
        }).ToArray();
        var retained = SegmentComparisonGeometry.RetainedSamples(samples, SportKind.Cycling);
        Assert.InRange(retained.Count, 2, 600);
        foreach (var index in retained) Assert.Contains(index % 2 == 0 ? index + 1 : index - 1, retained);

        var plot = SegmentComparisonGeometry.BuildSeries(samples, retained, 0, true, SportKind.Cycling,
            new ChartScaleGeometry(0, 2, []), 2000);
        foreach (var line in plot.Lines)
        {
            var vertices = line.Split(' ');
            for (var index = 1; index < vertices.Length; index++)
            {
                var previous = vertices[index - 1].Split(',');
                var current = vertices[index].Split(',');
                if (previous[0] == current[0]) continue;
                var gap = double.Parse(current[0], System.Globalization.CultureInfo.InvariantCulture) - double.Parse(previous[0], System.Globalization.CultureInfo.InvariantCulture);
                Assert.InRange(gap, 0, 0.40001);
            }
        }
    }

    [Fact]
    public void Stationary_time_is_vertical_and_missing_values_differ_from_zero()
    {
        var arrival = Sample(10, 0, null, 10) with { IsArrival = true };
        var departure = arrival with { IsArrival = false, Comparison = arrival.Comparison with { ElapsedSeconds = 15 } };
        var samples = new[] { Sample(0, 100, 150, 0), arrival, departure, Sample(20, 100, 150, 20) };
        var plot = SegmentComparisonGeometry.BuildSeries(samples, new HashSet<int> { 0, 1, 2, 3 }, 0, true,
            SportKind.Cycling, new ChartScaleGeometry(0, 10, []), 20);

        Assert.Equal("0,180 400,180 400,90 800,180", Assert.Single(plot.Lines));
        Assert.Equal(0, SegmentComparisonGeometry.Value(arrival, 2, true, SportKind.Cycling));
        Assert.Null(SegmentComparisonGeometry.Value(arrival, 2, false, SportKind.Cycling));
        var stopped = arrival with { Baseline = arrival.Baseline with { SpeedMetersPerSecond = 0 } };
        Assert.Equal(0, SegmentComparisonGeometry.Value(stopped, 1, true, SportKind.Cycling));
        Assert.Null(SegmentComparisonGeometry.Value(stopped, 1, true, SportKind.Running));
        Assert.Equal(1d / 3, SegmentComparisonGeometry.Value(arrival, 1, true, SportKind.Rowing)!.Value, 10);
    }

    private static SegmentComparisonSample Sample(double distance, double? baselineHeart, double? comparisonHeart, double elapsed)
    {
        var source = new SourceBoundary(0, 0, 0, null);
        return new(distance, false,
            new(source, elapsed, 25, baselineHeart, 100, null),
            new(source, elapsed, 25, comparisonHeart, 100, null));
    }
}
