using ActivityExplorer.Infrastructure.Import;
using Dynastream.Fit;

namespace ActivityExplorer.Tests;

public sealed class SegmentPathReaderTests
{
    [Fact]
    public async Task Reads_single_paths_from_supported_text_formats()
    {
        var directory = TestSupport.NewDirectory();
        var inputs = new Dictionary<string, string>
        {
            ["path.gpx"] = """
                <gpx><rte><rtept lat="1" lon="-30"><ele>10</ele></rtept><rtept lat="1.1" lon="-29.9"><ele>20</ele></rtept></rte></gpx>
                """,
            ["path.tcx"] = """
                <TrainingCenterDatabase><Courses><Course><Track>
                  <Trackpoint><Position><LatitudeDegrees>1</LatitudeDegrees><LongitudeDegrees>-30</LongitudeDegrees></Position></Trackpoint>
                  <Trackpoint><Position><LatitudeDegrees>1.1</LatitudeDegrees><LongitudeDegrees>-29.9</LongitudeDegrees></Position></Trackpoint>
                </Track></Course></Courses></TrainingCenterDatabase>
                """,
            ["path.kml"] = """
                <kml><Document><Placemark><LineString><coordinates>-30,1,10 -29.9,1.1,20</coordinates></LineString></Placemark></Document></kml>
                """,
            ["path.geojson"] = """
                {"type":"Feature","properties":{"provider_id":"discard-me"},"geometry":{"type":"LineString","coordinates":[[-30,1,10],[-29.9,1.1,20]]}}
                """
        };
        var reader = new SegmentPathReader();

        foreach (var input in inputs)
        {
            var result = await reader.ReadAsync(TestSupport.Write(directory, input.Key, input.Value));
            Assert.Equal(2, result.Points.Count);
            Assert.Equal(1, result.Points[0].Latitude);
            Assert.Equal(-29.9, result.Points[1].Longitude!.Value, 6);
            Assert.Null(result.Points[0].Timestamp);
            Assert.Null(result.Points[0].HeartRate);
        }
    }

    [Fact]
    public async Task Rejects_multiple_or_unsafe_xml_paths()
    {
        var directory = TestSupport.NewDirectory();
        var multiple = TestSupport.Write(directory, "multiple.gpx", """
            <gpx><trk><trkseg><trkpt lat="1" lon="-30"/><trkpt lat="1.1" lon="-29.9"/></trkseg>
            <trkseg><trkpt lat="2" lon="-20"/><trkpt lat="2.1" lon="-19.9"/></trkseg></trk></gpx>
            """);
        var unsafeXml = TestSupport.Write(directory, "unsafe.kml",
            "<!DOCTYPE kml [<!ENTITY xxe SYSTEM 'file:///private'>]><kml><LineString><coordinates>&xxe;</coordinates></LineString></kml>");
        var reader = new SegmentPathReader();

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(multiple));
        await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadAsync(unsafeXml));
    }

    [Fact]
    public async Task Reads_synthetic_fit_segment_geometry_without_exposing_segment_metadata()
    {
        var directory = TestSupport.NewDirectory();
        var path = WriteFitSegment(directory);

        var result = await new SegmentPathReader().ReadAsync(path);

        Assert.Equal("FIT SEGMENT", result.Format);
        Assert.Equal(3, result.Points.Count);
        Assert.Equal(1, result.Points[0].Latitude!.Value, 5);
        Assert.Equal(-29.998, result.Points[2].Longitude!.Value, 5);
        Assert.Equal(["Points", "Format"], typeof(SegmentPathData).GetProperties().Select(property => property.Name).ToArray());
    }

    [Fact]
    public async Task Reads_fit_course_but_rejects_fit_activity()
    {
        var directory = TestSupport.NewDirectory();
        var course = WriteFitCourse(directory);
        var activity = TestSupport.CyclingFit(directory);
        var reader = new SegmentPathReader();

        var result = await reader.ReadAsync(course);

        Assert.Equal("FIT COURSE", result.Format);
        Assert.Equal(3, result.Points.Count);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(activity));
    }

    private static string WriteFitSegment(string directory)
    {
        var path = Path.Combine(directory, "synthetic-segment.fit");
        using var stream = System.IO.File.Create(path);
        var encoder = new Encode(stream, ProtocolVersion.V20);
        var fileId = new FileIdMesg();
        fileId.SetType(Dynastream.Fit.File.Segment);
        fileId.SetManufacturer(1);
        encoder.Write(fileId);

        var identity = new SegmentIdMesg();
        identity.SetName("Synthetic metadata that must not be retained");
        identity.SetUuid("00000000-0000-0000-0000-000000000001");
        identity.SetSport(Sport.Cycling);
        encoder.Write(identity);

        for (ushort index = 0; index < 3; index++)
        {
            var point = new SegmentPointMesg();
            point.SetMessageIndex(index);
            point.SetPositionLat(ToSemicircles(1 + index * 0.001));
            point.SetPositionLong(ToSemicircles(-30 + index * 0.001));
            point.SetDistance(index * 150);
            point.SetEnhancedAltitude(10 + index);
            point.SetLeaderTime(0, 999 + index);
            encoder.Write(point);
        }
        encoder.Close();
        return path;
    }

    private static string WriteFitCourse(string directory)
    {
        var path = Path.Combine(directory, "synthetic-course.fit");
        using var stream = System.IO.File.Create(path);
        var encoder = new Encode(stream, ProtocolVersion.V20);
        var fileId = new FileIdMesg();
        fileId.SetType(Dynastream.Fit.File.Course);
        fileId.SetManufacturer(1);
        encoder.Write(fileId);
        for (var index = 0; index < 3; index++)
        {
            var point = new RecordMesg();
            point.SetPositionLat(ToSemicircles(1 + index * 0.001));
            point.SetPositionLong(ToSemicircles(-30 + index * 0.001));
            point.SetDistance(index * 150);
            encoder.Write(point);
        }
        encoder.Close();
        return path;
    }

    private static int ToSemicircles(double degrees) =>
        (int)Math.Round(degrees * 2_147_483_648d / 180d);
}
