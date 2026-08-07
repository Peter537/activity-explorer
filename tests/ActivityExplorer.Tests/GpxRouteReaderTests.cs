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

        Assert.Equal(2, points.Count);
        Assert.Null(points[0].Timestamp);
        Assert.Equal(20, points[1].ElevationMeters);
    }

    [Fact]
    public async Task Route_reader_prohibits_dtd()
    {
        var directory = TestSupport.NewDirectory();
        var path = TestSupport.Write(directory, "unsafe.gpx",
            "<!DOCTYPE gpx [<!ENTITY xxe SYSTEM 'file:///private'>]><gpx><rte><rtept lat='1' lon='-30'/><rtept lat='2' lon='-29'/></rte></gpx>");

        await Assert.ThrowsAsync<XmlException>(() => new GpxRouteReader().ReadAsync(path));
    }
}
