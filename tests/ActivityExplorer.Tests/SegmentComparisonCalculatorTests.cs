using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Services;

namespace ActivityExplorer.Tests;

public sealed class SegmentComparisonCalculatorTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Different_sampling_rates_align_to_geometry_and_reversing_the_pair_reverses_gain_and_loss()
    {
        var definition = Enumerable.Range(0, 11).Select(index => Point(index * 10, index)).ToArray();
        var baseline = Enumerable.Range(0, 5).Select(index => Point(index * 25, index * 10)).ToArray();
        var comparison = Enumerable.Range(0, 21).Select(index => Point(index * 5,
            index <= 10 ? index * 1.6 : 16 + (index - 10) * 2.8) with
        { DistanceMeters = 900 - index * 10 }).ToArray();
        var first = Align(definition, baseline);
        var second = Align(definition, comparison);
        var samples = SegmentComparisonCalculator.BuildSamples(first, second);
        Assert.True(first.IsAvailable, first.Message);
        Assert.True(second.IsAvailable, second.Message);
        Assert.Equal(0, samples[0].DeltaSeconds, 8);
        Assert.Equal(-4, samples.MinBy(sample => Math.Abs(sample.DistanceMeters - 50))!.DeltaSeconds, 5);
        Assert.Equal(4, samples[^1].DeltaSeconds, 8);
        Assert.Equal(new TrackPathAnalysis(definition).TotalDistanceMeters, samples[^1].DistanceMeters, 6);
        var reversed = SegmentComparisonCalculator.BuildSamples(second, first);
        Assert.Equal(samples.Count, reversed.Count);
        Assert.All(samples.Zip(reversed), pair => Assert.Equal(pair.First.DeltaSeconds, -pair.Second.DeltaSeconds, 8));
    }

    [Fact]
    public void Dwell_keeps_arrival_departure_and_exact_elapsed_endpoints_without_fabricating_sensors()
    {
        var definition = new[] { Point(0, 0), Point(50, 20), Point(100, 40) };
        var baseline = definition;
        var comparison = new[]
        {
            Point(0, 0) with { HeartRate = 100 }, Point(50, 20) with { HeartRate = 120 },
            Point(50, 25) with { HeartRate = null }, Point(50, 30) with { HeartRate = 140 }, Point(100, 50) with { HeartRate = 160 }
        };
        var samples = SegmentComparisonCalculator.BuildSamples(Align(definition, baseline), Align(definition, comparison));
        var dwell = samples.Where(sample => Math.Abs(sample.DistanceMeters - 50) < 0.001).ToArray();
        Assert.Equal(2, dwell.Length);
        Assert.True(dwell[0].IsArrival);
        Assert.False(dwell[1].IsArrival);
        Assert.Equal(0, dwell[0].DeltaSeconds, 6);
        Assert.Equal(10, dwell[1].DeltaSeconds, 6);
        Assert.Equal(120, dwell[0].Comparison.HeartRate);
        Assert.Equal(140, dwell[1].Comparison.HeartRate);
        Assert.True(dwell[1].Comparison.HeartRateStartsNewRun);
        Assert.Equal(0, samples[0].DeltaSeconds, 8);
        Assert.Equal(10, samples[^1].DeltaSeconds, 8);
    }

    [Fact]
    public void Sensor_gaps_remain_missing_at_interpolated_distances_and_zero_is_recorded()
    {
        var definition = new[] { Point(0, 0), Point(50, 10), Point(100, 20) };
        var baseline = new[]
        {
            Point(0, 0) with { HeartRate = 0, PowerWatts = 0 },
            Point(50, 10) with { HeartRate = null, PowerWatts = 100 },
            Point(100, 20) with { HeartRate = 100, PowerWatts = 200 }
        };
        var comparison = Enumerable.Range(0, 5).Select(index => Point(index * 25, index * 5)).ToArray();
        var samples = SegmentComparisonCalculator.BuildSamples(Align(definition, baseline), Align(definition, comparison));
        Assert.Equal(0, samples[0].Baseline.HeartRate);
        Assert.Equal(0, samples[0].Baseline.PowerWatts);
        Assert.All(samples.Where(sample => sample.DistanceMeters > 0.001 && sample.DistanceMeters < 99.999), sample => Assert.Null(sample.Baseline.HeartRate));
        Assert.Equal(50, samples.MinBy(sample => Math.Abs(sample.DistanceMeters - 25))!.Baseline.PowerWatts!.Value, 5);
    }

    [Fact]
    public void Full_source_boundaries_remain_absolute_and_input_points_are_unchanged()
    {
        var full = Enumerable.Range(0, 9).Select(index => Point(index * 20, index * 2) with { DistanceMeters = 1000 + index }).ToArray();
        var definition = full.Skip(2).Take(5).ToArray();
        var snapshot = full.ToArray();
        var result = SegmentComparisonCalculator.Align(definition, full, Effort(full, 2, 6), 30);
        Assert.True(result.IsAvailable, result.Message);
        Assert.Equal(2, result.Points[0].Source.Position);
        Assert.Equal(6, result.Points[^1].Source.Position);
        Assert.Equal(2, result.Map.Start!.SourcePosition);
        Assert.All(result.Points, point => Assert.InRange(point.Source.Position, 2, 6));
        Assert.Equal(snapshot, full);
    }

    [Theory]
    [InlineData("missing-time", SegmentComparisonUnavailableReason.InsufficientTiming)]
    [InlineData("missing-gps", SegmentComparisonUnavailableReason.Discontinuity)]
    [InlineData("reset", SegmentComparisonUnavailableReason.Discontinuity)]
    [InlineData("long-gap", SegmentComparisonUnavailableReason.Discontinuity)]
    [InlineData("duration", SegmentComparisonUnavailableReason.DurationMismatch)]
    public void Unsupported_timing_and_recording_breaks_are_explicit_without_changing_ranked_efforts(string defect, SegmentComparisonUnavailableReason reason)
    {
        var definition = Enumerable.Range(0, 5).Select(index => Point(index * 25, index * 10)).ToArray();
        var source = definition.ToArray();
        var effort = Effort(source);
        if (defect == "missing-time") source[2] = source[2] with { Timestamp = null };
        if (defect == "missing-gps") source[2] = source[2] with { Latitude = null };
        if (defect == "reset") source[2] = source[2] with { Timestamp = source[1].Timestamp };
        if (defect == "long-gap") source[2] = source[2] with { Timestamp = Origin.AddSeconds(45) };
        if (defect == "duration") effort = effort with { ElapsedSeconds = 40.002 };
        var result = SegmentComparisonCalculator.Align(definition, source, effort, 30);
        Assert.False(result.IsAvailable);
        Assert.Equal(reason, result.UnavailableReason);
        Assert.NotEmpty(result.Message!);
        Assert.Empty(result.Points);
        if (defect == "missing-gps") Assert.Equal(2, result.Map.Runs.Count);
    }

    [Fact]
    public void Timestamp_duration_tolerance_is_one_millisecond_and_thirty_second_edges_are_valid()
    {
        var points = new[] { Point(0, 0), Point(50, 30), Point(100, 60) };
        Assert.True(SegmentComparisonCalculator.Align(points, points, Effort(points) with { ElapsedSeconds = 60.0005 }, 30).IsAvailable);
        Assert.Equal(SegmentComparisonUnavailableReason.DurationMismatch,
            SegmentComparisonCalculator.Align(points, points, Effort(points) with { ElapsedSeconds = 60.002 }, 30).UnavailableReason);
    }

    [Fact]
    public void Closed_two_lap_definition_retains_each_traversal_in_order()
    {
        var lap = Enumerable.Range(0, 81).Select(index => new TrackPoint(Origin.AddSeconds(index),
            1 + 0.001 * Math.Sin(2 * Math.PI * index / 80), -30 + 0.001 * Math.Cos(2 * Math.PI * index / 80),
            null, null, null, null, null, null, null)).ToArray();
        var definition = lap.Concat(lap.Skip(1)).Select((point, index) => point with { Timestamp = Origin.AddSeconds(index) }).ToArray();
        var source = definition.Select((point, index) => point with { Timestamp = Origin.AddSeconds(index * 2) }).ToArray();
        var result = Align(definition, source);
        Assert.True(result.IsAvailable, result.Message);
        Assert.Equal(320, result.Points[^1].ElapsedSeconds);
        var turn = result.Points.Single(point => point.Source.Position == 80);
        Assert.Equal(new TrackPathAnalysis(lap).TotalDistanceMeters, turn.DistanceMeters, 5);
        Assert.All(result.Points.Zip(result.Points.Skip(1)), pair => Assert.True(pair.First.DistanceMeters <= pair.Second.DistanceMeters));
    }

    [Fact]
    public void Antimeridian_geometry_and_small_gps_noise_have_finite_monotone_correspondence()
    {
        var definition = Enumerable.Range(0, 9).Select(index => Point(index * 20, index * 2) with
        {
            Latitude = 0,
            Longitude = (179.9995 + index * 0.0002 + 540) % 360 - 180
        }).ToArray();
        var source = definition.Select((point, index) => point with { Latitude = index % 2 == 0 ? 0.000005 : -0.000005 }).ToArray();
        var result = Align(definition, source);
        Assert.True(result.IsAvailable, result.Message);
        Assert.All(result.Points, point => Assert.True(double.IsFinite(point.DistanceMeters)));
        Assert.All(result.Points.Zip(result.Points.Skip(1)), pair => Assert.True(pair.First.DistanceMeters <= pair.Second.DistanceMeters));
    }

    [Fact]
    public void Analysis_budgets_and_cancellation_fail_explicitly()
    {
        var enormous = new[] { Point(0, 0), Point(600_000, 10) };
        Assert.Equal(SegmentComparisonUnavailableReason.AnalysisLimit, Align(enormous, enormous).UnavailableReason);
        var points = new[] { Point(0, 0), Point(100, 10) };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SegmentComparisonCalculator.Align(points, points, Effort(points), 30, cancellation.Token));
    }

    [Fact]
    public void Equally_supported_separate_traversals_are_unavailable_instead_of_tie_broken()
    {
        var definition = Enumerable.Range(0, 7).Select(index => Point(index % 2 * 20, index)).ToArray();
        var source = new[] { Point(0, 0), Point(20, 1), Point(0, 2) };
        var result = Align(definition, source);
        Assert.False(result.IsAvailable);
        Assert.Equal(SegmentComparisonUnavailableReason.AmbiguousAlignment, result.UnavailableReason);
    }

    [Fact]
    public void Densely_overlapping_geometry_cannot_exceed_the_candidate_budget()
    {
        var points = Enumerable.Range(0, 1001).Select(index => Point(index % 2 * 20, index)).ToArray();
        Assert.Equal(SegmentComparisonUnavailableReason.AnalysisLimit, Align(points, points).UnavailableReason);
    }

    private static SegmentEffortAlignment Align(IReadOnlyList<TrackPoint> definition, IReadOnlyList<TrackPoint> source) =>
        SegmentComparisonCalculator.Align(definition, source, Effort(source), 30);

    private static SegmentEffortSummary Effort(IReadOnlyList<TrackPoint> points, int start = 0, int? end = null)
    {
        var finish = end ?? points.Count - 1;
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Test segment",
            (points[finish].Timestamp!.Value - points[start].Timestamp!.Value).TotalSeconds, 1, points[start].Timestamp!.Value, start, finish,
            0, null, null, null, null, null, null, null, null, null, null, null, null, null, null, 100, true);
    }

    private static TrackPoint Point(double meters, double seconds) => new(
        Origin.AddSeconds(seconds), 0, meters / 6_371_000 * 180 / Math.PI, meters, null, null, null, null, null, null);
}
