using ActivityExplorer.Core.Domain;
using ActivityExplorer.Web.Components.Shared;

namespace ActivityExplorer.Tests;

public sealed class SegmentCreatorPathTests
{
    [Fact]
    public void Activity_projection_keeps_only_positioned_samples_and_maps_to_source_indices()
    {
        var points = new[]
        {
            Point(55.0, 12.0, 10),
            Point(null, null, 20),
            Point(55.1, 12.1, null),
            Point(55.2, null, 40),
            Point(55.3, 12.3, 50)
        };

        var result = SegmentCreatorPath.FromActivity(points);

        Assert.Equal(3, result.Count);
        Assert.Equal([0, 2, 4], result.SourcePointIndices);
        Assert.Same(points[0], result.Points[0]);
        Assert.Same(points[2], result.Points[1]);
        Assert.Same(points[4], result.Points[2]);
        Assert.Equal(2, result.SourceIndexAt(1));
    }

    [Fact]
    public void Activity_projection_handles_empty_and_incomplete_tracks()
    {
        Assert.Empty(SegmentCreatorPath.FromActivity([]).Points);
        Assert.Empty(SegmentCreatorPath.FromActivity([Point(55, null, null)]).Points);
    }

    [Fact]
    public void Source_index_lookup_rejects_visual_indices_outside_the_projection()
    {
        var result = SegmentCreatorPath.FromActivity([Point(55, 12, 10), Point(56, 13, 20)]);

        Assert.Throws<ArgumentOutOfRangeException>(() => result.SourceIndexAt(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.SourceIndexAt(2));
    }

    private static TrackPoint Point(double? latitude, double? longitude, double? elevation) =>
        new(null, latitude, longitude, null, elevation, null, null, null, null, null);
}
