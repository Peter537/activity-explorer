using System.Globalization;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Components.Shared;

namespace ActivityExplorer.Tests;

public sealed class SegmentGradeProfileGeometryTests
{
    [Fact]
    public void Fifty_metre_window_shifts_at_boundaries_and_follows_uneven_distance()
    {
        var coordinates = new[] { 0d, 0.00009, 0.00027, 0.00054, 0.0009 };
        var geometryOnly = coordinates.Select(longitude => Point(longitude, null)).ToArray();
        var analysis = new TrackPathAnalysis(geometryOnly);
        var points = geometryOnly.Select((point, index) =>
            point with { ElevationMeters = 100 + analysis.DistanceAt(index) * 0.1 }).ToArray();

        var geometry = SegmentGradeProfileGeometry.Build(points);

        Assert.True(geometry.HasData);
        Assert.Equal(points.Length, geometry.Samples.Count);
        Assert.All(geometry.Samples, sample => Assert.InRange(sample.GradePercent!.Value, 9.999, 10.001));
        Assert.InRange(geometry.MinimumGradePercent!.Value, 9.999, 10.001);
        Assert.InRange(geometry.MaximumGradePercent!.Value, 9.999, 10.001);
        Assert.All(geometry.GradeSpans, span => Assert.Equal(SegmentGradeBucket.Steep, span.Bucket));
    }

    [Fact]
    public void Short_duplicate_and_reversed_paths_remain_finite_and_directional()
    {
        var shortPath = new[]
        {
            Point(0, 10),
            Point(0, 10),
            Point(0.0002, 13)
        };
        var shortGeometry = SegmentGradeProfileGeometry.Build(shortPath);
        Assert.True(shortGeometry.HasData);
        Assert.All(shortGeometry.Samples, sample =>
        {
            Assert.True(sample.GradePercent.HasValue);
            Assert.True(double.IsFinite(sample.GradePercent.Value));
        });

        var reversed = shortPath.Reverse().ToArray();
        var reversedGeometry = SegmentGradeProfileGeometry.Build(reversed);
        Assert.All(reversedGeometry.Samples, sample => Assert.True(sample.GradePercent < 0));
        Assert.All(reversedGeometry.GradeSpans, span => Assert.Equal(SegmentGradeBucket.Downhill, span.Bucket));

        var extreme = SegmentGradeProfileGeometry.Build([Point(0, 0), Point(0.0001, 20)]);
        Assert.True(extreme.MaximumGradePercent > 100);
        Assert.All(extreme.GradeSpans, span => Assert.Equal(SegmentGradeBucket.VerySteep, span.Bucket));
    }

    [Fact]
    public void Elevation_gaps_are_not_bridged_or_invented()
    {
        var points = new[]
        {
            Point(0, 10),
            Point(0.0001, 11),
            Point(0.0002, null),
            Point(0.0003, 30),
            Point(0.0004, 31)
        };

        var geometry = SegmentGradeProfileGeometry.Build(points);

        Assert.True(geometry.HasData);
        Assert.Equal(2, geometry.AreaPaths.Count);
        Assert.Equal(80, geometry.ElevationCoveragePercent);
        Assert.DoesNotContain(geometry.GradeSpans, span => span.StartSourceIndex == 1 && span.EndSourceIndex == 3);
        Assert.Contains(geometry.GradeSpans, span => span.StartSourceIndex == 0 && span.EndSourceIndex == 1);
        Assert.Contains(geometry.GradeSpans, span => span.StartSourceIndex == 3 && span.EndSourceIndex == 4);

        var isolated = SegmentGradeProfileGeometry.Build(
            [Point(0, 10), Point(0.0001, null), Point(0.0002, 20), Point(0.0003, null)]);
        Assert.False(isolated.HasData);
        Assert.Empty(isolated.GradeSpans);
        Assert.Equal(50, isolated.ElevationCoveragePercent);
    }

    [Theory]
    [InlineData(-1.0001, "Downhill")]
    [InlineData(-1, "Flat")]
    [InlineData(1.9999, "Flat")]
    [InlineData(2, "Gentle")]
    [InlineData(4.9999, "Gentle")]
    [InlineData(5, "Moderate")]
    [InlineData(7.9999, "Moderate")]
    [InlineData(8, "Steep")]
    [InlineData(11.9999, "Steep")]
    [InlineData(12, "VerySteep")]
    public void Grade_buckets_have_deterministic_boundaries(double grade, string expected) =>
        Assert.Equal(expected, SegmentGradeProfileGeometry.BucketFor(grade).ToString());

    [Fact]
    public void Alternating_grade_transitions_cannot_exceed_the_global_render_budget()
    {
        var points = Enumerable.Range(0, 10_000)
            .Select(index => Point(index * 0.0005, index % 2 == 0 ? 0 : 20))
            .ToArray();

        var geometry = SegmentGradeProfileGeometry.Build(points, maximumSamples: 64);

        Assert.True(geometry.HasData);
        Assert.InRange(geometry.Samples.Count, 2, 64);
        Assert.True(geometry.GradeSpans.Count <= geometry.Samples.Count - geometry.AreaPaths.Count);
        Assert.Contains(geometry.Samples, sample => sample.SourceIndex == 0);
        Assert.Contains(geometry.Samples, sample => sample.SourceIndex == points.Length - 1);
    }

    [Fact]
    public void Many_elevation_runs_remain_disconnected_within_the_global_render_budget()
    {
        var points = Enumerable.Range(0, 3_000)
            .Select(index => Point(index * 0.00002, index % 3 == 2 ? null : index % 100))
            .ToArray();

        var geometry = SegmentGradeProfileGeometry.Build(points, maximumSamples: 64);

        Assert.True(geometry.HasData);
        Assert.InRange(geometry.Samples.Count, 2, 64);
        Assert.InRange(geometry.AreaPaths.Count, 1, 32);
        Assert.All(geometry.GradeSpans, span => Assert.Equal(1, span.EndSourceIndex - span.StartSourceIndex));
    }

    [Fact]
    public void Downsampling_preserves_extrema_transitions_and_invariant_svg()
    {
        var points = Enumerable.Range(0, 1_000)
            .Select(index => Point(index * 0.00001, 50 + Math.Sin(index / 85d) * 18))
            .ToArray();
        var minimumIndex = Array.IndexOf(points, points.MinBy(point => point.ElevationMeters));
        var maximumIndex = Array.IndexOf(points, points.MaxBy(point => point.ElevationMeters));
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("da-DK");
            var geometry = SegmentGradeProfileGeometry.Build(points, maximumSamples: 48);

            Assert.True(geometry.HasData);
            Assert.InRange(geometry.Samples.Count, 20, 48);
            Assert.Contains(geometry.Samples, sample => sample.SourceIndex == 0);
            Assert.Contains(geometry.Samples, sample => sample.SourceIndex == points.Length - 1);
            Assert.Contains(geometry.Samples, sample => sample.SourceIndex == minimumIndex);
            Assert.Contains(geometry.Samples, sample => sample.SourceIndex == maximumIndex);
            Assert.All(geometry.AreaPaths, path => Assert.Contains('.', path));
            Assert.All(geometry.GradeSpans, span =>
            {
                Assert.Contains('.', span.AreaPath);
                Assert.Contains('.', span.LinePath);
            });
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    private static TrackPoint Point(double longitude, double? elevation) =>
        new(null, 0, longitude, null, elevation, null, null, null, null, null);
}
