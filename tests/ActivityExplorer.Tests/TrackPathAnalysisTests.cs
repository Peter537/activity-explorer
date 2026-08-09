using System.Globalization;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Components.Shared;

namespace ActivityExplorer.Tests;

public sealed class TrackPathAnalysisTests
{
    [Fact]
    public void Cumulative_distance_nearest_point_and_slice_metrics_follow_route_geometry()
    {
        var points = new[]
        {
            Point(0, 10),
            Point(0.001, 20),
            Point(0.004, 5)
        };
        var analysis = new TrackPathAnalysis(points);

        Assert.InRange(analysis.DistanceAt(1), 110, 112);
        Assert.InRange(analysis.TotalDistanceMeters, 444, 446);
        Assert.Equal(1, analysis.FindNearestPointIndex(120));
        Assert.Equal(2, analysis.FindNearestPointIndex(400));

        var forward = analysis.Slice(1, 2);
        Assert.InRange(forward.DistanceMeters, 333, 335);
        Assert.Equal(0, forward.ElevationGainMeters);
        Assert.Equal(15, forward.ElevationLossMeters);
        Assert.Equal(20, forward.StartElevationMeters);
        Assert.Equal(5, forward.EndElevationMeters);
        Assert.True(forward.AverageGradePercent < 0);

        var reverse = analysis.Slice(1, 2, reverseDirection: true);
        Assert.Equal(forward.DistanceMeters, reverse.DistanceMeters);
        Assert.Equal(15, reverse.ElevationGainMeters);
        Assert.Equal(0, reverse.ElevationLossMeters);
        Assert.Equal(5, reverse.StartElevationMeters);
        Assert.Equal(20, reverse.EndElevationMeters);
        Assert.Equal(-forward.AverageGradePercent, reverse.AverageGradePercent);
    }

    [Fact]
    public void Missing_coordinates_retain_distance_and_slice_grade_between_surrounding_positions()
    {
        var points = new[]
        {
            Point(0, 10),
            new TrackPoint(null, null, null, null, 20, null, null, null, null, null),
            Point(0.001, 30)
        };
        var analysis = new TrackPathAnalysis(points);

        Assert.Equal(0, analysis.DistanceAt(1));
        Assert.InRange(analysis.TotalDistanceMeters, 111, 112);
        var metrics = analysis.Slice(0, 2);
        Assert.Equal(analysis.TotalDistanceMeters, metrics.DistanceMeters);
        Assert.Equal(20, metrics.ElevationGainMeters);
        Assert.Equal(0, metrics.ElevationLossMeters);
        Assert.InRange(metrics.AverageGradePercent!.Value, 17.9, 18.0);

        var missingBoundary = analysis.Slice(1, 2);
        Assert.Equal(0, missingBoundary.DistanceMeters);
        Assert.Null(missingBoundary.AverageGradePercent);
    }

    [Fact]
    public void Missing_elevation_is_not_invented_in_the_profile()
    {
        var points = new[]
        {
            Point(0, 10),
            Point(0.001, null),
            Point(0.002, 30),
            Point(0.003, null)
        };
        var analysis = new TrackPathAnalysis(points);

        var metrics = analysis.Slice(0, 3);
        Assert.Equal(2, metrics.ElevationSampleCount);
        Assert.Equal(20, metrics.ElevationGainMeters);
        Assert.Equal(10, metrics.StartElevationMeters);
        Assert.Equal(30, metrics.EndElevationMeters);

        var geometry = SegmentElevationGeometry.Build(points, analysis);
        Assert.False(geometry.HasData);
        Assert.Empty(geometry.AreaPaths);
    }

    [Fact]
    public void Elevation_geometry_preserves_gaps_downsamples_and_uses_invariant_svg_numbers()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("da-DK");
            var points = Enumerable.Range(0, 120)
                .Select(index => Point(index * 0.0001, index is >= 50 and <= 54 ? null : 20 + Math.Sin(index / 8d) * 12))
                .ToArray();
            var analysis = new TrackPathAnalysis(points);

            var geometry = SegmentElevationGeometry.Build(points, analysis, maximumSamples: 24);

            Assert.True(geometry.HasData);
            Assert.Equal(2, geometry.AreaPaths.Count);
            Assert.Equal(2, geometry.LinePaths.Count);
            Assert.All(geometry.LinePaths, path => Assert.Contains('.', path));
            Assert.All(geometry.LinePaths, path => Assert.StartsWith("M ", path, StringComparison.Ordinal));
            Assert.Equal(5, geometry.DistanceTicks.Count);
            Assert.InRange(geometry.LinePaths.Sum(path => path.Count(character => character == 'L')), 2, 35);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Elevation_geometry_enforces_one_global_budget_across_many_short_runs()
    {
        var points = Enumerable.Range(0, 3_000)
            .Select(index => Point(index * 0.00001, index % 3 == 2 ? null : index % 50))
            .ToArray();
        var analysis = new TrackPathAnalysis(points);

        var geometry = SegmentElevationGeometry.Build(points, analysis, maximumSamples: 64);

        Assert.True(geometry.HasData);
        Assert.InRange(geometry.RenderedSampleCount, 2, 64);
        Assert.InRange(geometry.AreaPaths.Count, 1, 32);
        Assert.Equal(geometry.AreaPaths.Count, geometry.LinePaths.Count);
    }

    [Fact]
    public void Empty_path_and_invalid_slice_boundaries_are_handled_explicitly()
    {
        var empty = new TrackPathAnalysis([]);
        Assert.Equal(-1, empty.FindNearestPointIndex(10));
        Assert.Equal(0, empty.TotalDistanceMeters);

        var analysis = new TrackPathAnalysis([Point(0, null), Point(0.001, null)]);
        Assert.Throws<ArgumentOutOfRangeException>(() => analysis.Slice(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => analysis.Slice(1, 0));
        var metrics = analysis.Slice(0, 1);
        Assert.Null(metrics.ElevationGainMeters);
        Assert.Null(metrics.StartElevationMeters);
    }

    private static TrackPoint Point(double longitude, double? elevation) =>
        new(null, 0, longitude, null, elevation, null, null, null, null, null);
}
