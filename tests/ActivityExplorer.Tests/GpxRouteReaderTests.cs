using System.Xml;
using ActivityExplorer.Infrastructure.Import;

namespace ActivityExplorer.Tests;

public sealed class GpxRouteReaderTests
{
    [Fact]
    public async Task Route_points_do_not_require_activity_timestamps()
    {
        var directory = TestSupport.NewDirectory();
        var path = TestSupport.Write(directory, "route.gpx", """
            <?xml version="1.0" encoding="UTF-8"?>
            <gpx version="1.1" creator="tests">
              <rte><name>Fictional route</name>
                <rtept lat="1.0" lon="-30.0"><ele>10</ele></rtept>
                <rtept lat="1.1" lon="-29.9"><ele>20</ele></rtept>
              </rte>
            </gpx>
            """);

        var points = await new GpxRouteReader().ReadAsync(path);
        var segment = await new SegmentPathReader().ReadAsync(path);

        Assert.Equal(2, points.Count);
        Assert.Null(points[0].Timestamp);
        Assert.Equal(20, points[1].ElevationMeters);
        Assert.Equal(points.ToArray(), segment.Points.ToArray());
    }

    [Fact]
    public async Task Both_path_readers_keep_only_track_points_in_the_declared_segment_in_order()
    {
        var path = TestSupport.Write(TestSupport.NewDirectory(), "track.gpx", """
            <gpx xmlns="http://www.topografix.com/GPX/1/1" xmlns:demo="urn:demo">
              <wpt lat="40" lon="5"/>
              <trk><trkseg>
                <trkpt lat="2" lon="-29"><ele>20</ele><time>2026-03-01T01:02:03Z</time></trkpt>
                <trkpt lat="1" lon="-30"><ele/><extensions><demo:ele>99</demo:ele></extensions></trkpt>
                <extensions><demo:trkpt lat="3" lon="-28"/></extensions>
              </trkseg></trk>
            </gpx>
            """);

        var route = await new GpxRouteReader().ReadAsync(path);
        var segment = await new SegmentPathReader().ReadAsync(path);

        Assert.Equal(route.ToArray(), segment.Points.ToArray());
        Assert.Equal(2, route.Count);
        Assert.Equal(2, route[0].Latitude);
        Assert.Equal(20, route[0].ElevationMeters);
        Assert.Null(route[0].Timestamp);
        Assert.Equal(1, route[1].Latitude);
        Assert.Null(route[1].ElevationMeters);
    }

    [Theory]
    [InlineData("<rte>{0}</rte><rte>{0}</rte>")]
    [InlineData("<rte>{0}</rte><trk><trkseg>{1}</trkseg></trk>")]
    [InlineData("<trk><trkseg>{1}</trkseg></trk><rte>{0}</rte>")]
    [InlineData("<trk><trkseg>{1}</trkseg></trk><trk><trkseg>{1}</trkseg></trk>")]
    [InlineData("<trk><trkseg>{1}</trkseg><trkseg>{1}</trkseg></trk>")]
    [InlineData("<trk><trkseg>{1}</trkseg><trkseg/></trk>")]
    [InlineData("<extensions><rte>{0}</rte></extensions>")]
    public async Task Both_path_readers_reject_discontinuous_or_absent_paths(string pathTemplate)
    {
        const string routePoints = "<rtept lat='1' lon='-30'/><rtept lat='2' lon='-29'/>";
        const string trackPoints = "<trkpt lat='1' lon='-30'/><trkpt lat='2' lon='-29'/>";
        var paths = pathTemplate.Replace("{0}", routePoints, StringComparison.Ordinal)
            .Replace("{1}", trackPoints, StringComparison.Ordinal);
        var path = TestSupport.Write(TestSupport.NewDirectory(), "paths.gpx", $"<gpx>{paths}</gpx>");

        var route = await Assert.ThrowsAsync<InvalidDataException>(() => new GpxRouteReader().ReadAsync(path));
        var segment = await Assert.ThrowsAsync<InvalidDataException>(() => new SegmentPathReader().ReadAsync(path));

        Assert.Equal(route.Message, segment.Message);
        Assert.Contains("exactly one directional path", route.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NaN", "-29", "")]
    [InlineData("89", "181", "")]
    [InlineData("2", "-29", "<ele>Infinity</ele>")]
    public async Task Both_path_readers_reject_invalid_coordinates_and_elevation(string latitude, string longitude, string content)
    {
        var path = TestSupport.Write(TestSupport.NewDirectory(), "invalid.gpx",
            $"<gpx><rte><rtept lat='1' lon='-30'/><rtept lat='{latitude}' lon='{longitude}'>{content}</rtept></rte></gpx>");

        var route = await Assert.ThrowsAsync<InvalidDataException>(() => new GpxRouteReader().ReadAsync(path));
        var segment = await Assert.ThrowsAsync<InvalidDataException>(() => new SegmentPathReader().ReadAsync(path));

        Assert.Equal(route.Message, segment.Message);
    }

    [Fact]
    public async Task Route_reader_prohibits_dtd()
    {
        var directory = TestSupport.NewDirectory();
        var path = TestSupport.Write(directory, "unsafe.gpx",
            "<!DOCTYPE gpx [<!ENTITY xxe SYSTEM 'file:///private'>]><gpx><rte><rtept lat='1' lon='-30'/><rtept lat='2' lon='-29'/></rte></gpx>");

        await Assert.ThrowsAsync<XmlException>(() => new GpxRouteReader().ReadAsync(path));
        await Assert.ThrowsAsync<XmlException>(() => new SegmentPathReader().ReadAsync(path));
    }
}
