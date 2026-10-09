using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;

namespace ActivityExplorer.Tests;

public sealed class ActivityRangeTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly ActivityRangeAnalyzer _analyzer = new();

    [Fact]
    public void Fractional_endpoints_integrate_full_source_edges_with_uneven_timing_and_zero_readings()
    {
        TrackPoint[] points =
        [
            Point(0, 0, 10, 0, 100, 20),
            Point(2, 20, 14, 20, 300, 40),
            Point(10, 100, 6, 100, 500, 60)
        ];
        var result = _analyzer.Analyze(points, Range(points, 0.5, 1.5));
        Assert.Equal(5, result.DurationSeconds);
        Assert.True(result.DurationComplete);
        Assert.Equal(50, result.Distance.Value);
        Assert.Equal(2, result.ElevationGain.Value);
        Assert.Equal(4, result.ElevationLoss.Value);
        Assert.Equal(35, result.HeartRate.Value);
        Assert.Equal(330, result.Power.Value);
        Assert.Equal(43, result.Cadence.Value);
        Assert.Equal(10, result.AverageSpeedMetersPerSecond);
        Assert.Equal(100, result.HeartRate.CoveragePercent);
        Assert.True(result.HeartRate.IsComplete);
        Assert.Equal(0.5, result.Map.Start!.SourcePosition);
        Assert.Equal(1.5, result.Map.End!.SourcePosition);
    }

    [Fact]
    public void Long_gaps_count_as_elapsed_but_not_sensor_distance_or_elevation_coverage()
    {
        TrackPoint[] points =
        [
            Point(0, 0, 0, 100), Point(10, 100, 10, 200),
            Point(50, 500, 30, 300), Point(60, 600)
        ];
        var result = _analyzer.Analyze(points, Range(points, 0, 3));
        Assert.Equal(60, result.DurationSeconds);
        Assert.True(result.DurationComplete);
        Assert.Equal(150, result.HeartRate.Value);
        Assert.Equal(10, result.HeartRate.CoveredSeconds);
        Assert.Equal(100d / 6, result.HeartRate.CoveragePercent!.Value, 8);
        Assert.Equal(200, result.Distance.Value);
        Assert.Equal(20, result.Distance.CoveredSeconds);
        Assert.Equal(10, result.AverageSpeedMetersPerSecond);
        Assert.Equal(10, result.ElevationGain.Value);
        Assert.False(result.Distance.IsComplete);
        Assert.Equal(2, result.Map.Runs.Count);
    }

    [Fact]
    public void Reset_and_missing_timestamps_leave_known_duration_partial_and_percentage_unavailable()
    {
        TrackPoint[] points =
        [
            Point(0, 0, heart: 100), Point(10, 100, heart: 100),
            Point(0, 0, heart: 200), Point(10, 100, heart: 200),
            Point(null, 150, heart: 500), Point(20, 200, heart: 500)
        ];
        var result = _analyzer.Analyze(points, Range(points, 0, 5));
        Assert.Equal(20, result.DurationSeconds);
        Assert.False(result.DurationComplete);
        Assert.Equal(150, result.HeartRate.Value);
        Assert.Equal(20, result.HeartRate.CoveredSeconds);
        Assert.Null(result.HeartRate.CoveragePercent);
        Assert.Equal(2, result.Map.Runs.Count);
    }

    [Fact]
    public void Thirty_seconds_is_covered_and_an_isolated_reading_never_becomes_a_weighted_average()
    {
        TrackPoint[] points = [Point(0, 0, heart: 0), Point(30, 30, heart: 60), Point(31, 31, power: 250)];
        var result = _analyzer.Analyze(points, Range(points, 0, 2));
        Assert.Equal(30, result.HeartRate.Value);
        Assert.Equal(30, result.HeartRate.CoveredSeconds);
        Assert.Null(result.Power.Value);
        Assert.Equal(0, result.Power.CoveragePercent);
    }

    [Fact]
    public void Distance_resets_do_not_fall_back_to_gps_but_missing_distance_does()
    {
        TrackPoint[] reset = [Point(0, 100), Point(10, 50), Point(20, 80)];
        var result = _analyzer.Analyze(reset, Range(reset, 0, 2));
        Assert.Equal(30, result.Distance.Value);
        Assert.Equal(50, result.Distance.CoveragePercent);
        Assert.False(result.UsesGpsDistance);
        TrackPoint[] missing = [Point(0, null), Point(10, null) with { Longitude = 12.001 }];
        result = _analyzer.Analyze(missing, Range(missing, 0, 1));
        Assert.InRange(result.Distance.Value!.Value, 63, 65);
        Assert.True(result.UsesGpsDistance);
    }

    [Fact]
    public void Indoor_recorded_distance_needs_no_gps_and_zero_distance_is_available()
    {
        var points = new[] { Point(0, 0), Point(10, 100), Point(20, 100) }
            .Select(point => point with { Latitude = null, Longitude = null }).ToArray();
        var result = _analyzer.Analyze(points, Range(points, 0, 2));
        Assert.Equal(100, result.Distance.Value);
        Assert.Equal(5, result.AverageSpeedMetersPerSecond);
        Assert.Empty(result.Map.Runs);
        Assert.Null(result.Map.Start);
        Assert.Equal(0, _analyzer.Analyze(points, Range(points, 1, 2)).Distance.Value);
    }

    [Fact]
    public void Partly_recorded_distance_is_never_repaired_with_gps_even_when_selected_edges_have_no_counter()
    {
        TrackPoint[] points =
        [
            Point(0, 0), Point(10, 100),
            Point(20, null) with { Longitude = 12.001 }, Point(30, null) with { Longitude = 12.002 }
        ];
        var result = _analyzer.Analyze(points, Range(points, 0, 3));
        Assert.Equal(100, result.Distance.Value);
        Assert.Equal(10, result.Distance.CoveredSeconds);
        Assert.False(result.UsesGpsDistance);
        var missingSection = _analyzer.Analyze(points, Range(points, 2, 3));
        Assert.Null(missingSection.Distance.Value);
        Assert.False(missingSection.UsesGpsDistance);
    }

    [Fact]
    public void Missing_gps_keeps_original_positions_and_does_not_connect_distant_recorded_points()
    {
        TrackPoint[] points =
        [
            Point(0, 0), Point(1, 10), Point(2, 20) with { Latitude = null },
            Point(3, 30), Point(4, 40)
        ];
        var map = _analyzer.ProjectMap(points, Range(points, 0.5, 3.5));
        Assert.Equal(2, map.Runs.Count);
        Assert.Collection(map.Runs[0], point => Assert.Equal(0.5, point.SourcePosition), point => Assert.Equal(1, point.SourcePosition));
        Assert.Collection(map.Runs[1], point => Assert.Equal(3, point.SourcePosition), point => Assert.Equal(3.5, point.SourcePosition));
    }

    [Fact]
    public void Bracketed_power_boundary_resolves_by_time_without_changing_the_benchmark()
    {
        TrackPoint[] points =
        [
            Point(0, 0, power: 100), Point(0.1, 1), Point(4, 40, power: 300),
            Point(8, 80, power: 100), Point(10, 100, power: 100)
        ];
        var window = Assert.Single(BestEffortCalculator.Attempts(points, SportKind.Cycling, RecordKind.PowerCurve, 5, false));
        Assert.Equal(260, window.Value);
        Assert.Equal(Origin.AddSeconds(3), window.StartTime);
        Assert.Equal(1.5, window.StartPosition);
        Assert.Equal(0, window.StartBoundary.LowerIndex);
        Assert.Equal(2, window.StartBoundary.UpperIndex);
        var resolved = ActivityRangeBoundary.Resolve(points, window.StartBoundary);
        Assert.Equal(1, resolved.LowerIndex);
        Assert.Equal(2, resolved.UpperIndex);
        Assert.Equal(1 + 2.9 / 3.9, resolved.Position, 8);
        Assert.Equal(window.StartTime, resolved.Timestamp);
        var result = _analyzer.Analyze(points, new(window.StartBoundary, window.FinishBoundary));
        Assert.Equal(5, result.DurationSeconds!.Value, 8);
        Assert.Equal(4, result.Power.CoveredSeconds);
        Assert.Equal(200, result.Power.Value);
    }

    [Fact]
    public void Ambiguous_skipped_timestamp_bracket_is_preserved_without_inventing_a_boundary()
    {
        TrackPoint[] points = [Point(0, 0), Point(null, 10), Point(4, 40), Point(8, 80)];
        var boundary = new SourceBoundary(0, 2, 0.75, Origin.AddSeconds(3));
        Assert.Same(boundary, ActivityRangeBoundary.Resolve(points, boundary));
        var result = _analyzer.Analyze(points, new(boundary, ActivityRangeBoundary.FromPosition(points, 3)!));
        Assert.True(result.HasAmbiguousBoundaries);
        Assert.False(result.DurationComplete);
        Assert.Equal(4, result.DurationSeconds);
        Assert.Null(result.Map.Start);
        Assert.Equal(2, result.Map.Runs[0][0].SourcePosition);
    }

    [Fact]
    public void Validation_rejects_nonfinite_outside_reversed_and_stale_boundaries_and_calculation_cancels()
    {
        TrackPoint[] points = [Point(0, 0), Point(10, 10)];
        Assert.Null(ActivityRangeBoundary.FromPosition(points, double.NaN));
        Assert.Null(ActivityRangeBoundary.FromPosition(points, -1));
        Assert.Null(ActivityRangeBoundary.FromPosition(points, 2));
        var valid = Range(points, 0, 1);
        Assert.NotNull(ActivityRangeBoundary.Validate(points, new(valid.End, valid.Start)));
        Assert.NotNull(ActivityRangeBoundary.Validate(points, valid with { Start = valid.Start with { Timestamp = Origin.AddSeconds(1) } }));
        Assert.Throws<OperationCanceledException>(() => _analyzer.Analyze(points, valid, new CancellationToken(true)));
    }

    [Fact]
    public void Valid_individual_brackets_cannot_reverse_the_range_when_resolved_on_uneven_timestamps()
    {
        TrackPoint[] points = [Point(0, 0), Point(90, 90), Point(100, 100)];
        var range = new ActivityRange(ActivityRangeBoundary.FromPosition(points, 1)!, new(0, 2, 0.6, Origin.AddSeconds(60)));
        Assert.True(range.Start.Position < range.End.Position);
        Assert.True(ActivityRangeBoundary.Resolve(points, range.Start).Position > ActivityRangeBoundary.Resolve(points, range.End).Position);
        Assert.NotNull(ActivityRangeBoundary.Validate(points, range));
        Assert.Throws<ArgumentException>(() => _analyzer.Analyze(points, range));
    }

    [Fact]
    public void Fingerprint_changes_with_stream_content_or_schema_but_not_owner()
    {
        var stream = new ActivityStream { OwnerId = Guid.NewGuid(), CompressedPayload = TrackCodec.Encode([Point(0, 0), Point(1, 1)]) };
        var first = TrackCodec.Fingerprint(stream);
        stream.OwnerId = Guid.NewGuid();
        Assert.Equal(first, TrackCodec.Fingerprint(stream));
        stream.CompressedPayload = TrackCodec.Encode([Point(0, 0), Point(1, 2)]);
        Assert.NotEqual(first, TrackCodec.Fingerprint(stream));
        var changed = TrackCodec.Fingerprint(stream);
        stream.SchemaVersion++;
        Assert.NotEqual(changed, TrackCodec.Fingerprint(stream));
    }

    private static ActivityRange Range(IReadOnlyList<TrackPoint> points, double start, double end) =>
        new(ActivityRangeBoundary.FromPosition(points, start)!, ActivityRangeBoundary.FromPosition(points, end)!);

    private static TrackPoint Point(double? seconds, double? distance, double? elevation = null, double? heart = null,
        double? power = null, double? cadence = null) =>
        new(seconds.HasValue ? Origin.AddSeconds(seconds.Value) : null, 55, 12, distance, elevation, null, heart, cadence, power, null);
}
