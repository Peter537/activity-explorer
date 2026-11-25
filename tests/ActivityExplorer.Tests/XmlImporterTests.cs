using System.Xml;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Import;

namespace ActivityExplorer.Tests;

public sealed class XmlImporterTests
{
    private readonly XmlActivityImporter _importer = new();

    [Fact]
    public async Task Tcx_ignores_course_points_outside_the_recorded_activity()
    {
        var recorded = TestSupport.Tcx();
        var mixed = recorded.Replace("</TrainingCenterDatabase>", """
            <Courses><Course><Name>Unrelated course</Name><Track><Trackpoint>
            <Time>2026-03-01T08:00:00Z</Time><DistanceMeters>999999</DistanceMeters>
            <Position><LatitudeDegrees>25</LatitudeDegrees><LongitudeDegrees>65</LongitudeDegrees></Position>
            </Trackpoint></Track></Course></Courses></TrainingCenterDatabase>
            """, StringComparison.Ordinal);
        Assert.NotEqual(recorded, mixed);
        var directory = TestSupport.NewDirectory();
        var baseline = Assert.Single(await _importer.ReadAsync(TestSupport.Write(directory, "recorded.tcx", recorded), SourceKind.Tcx)).Parsed;
        var result = Assert.Single(await _importer.ReadAsync(TestSupport.Write(directory, "mixed.tcx", mixed), SourceKind.Tcx)).Parsed;
        Assert.Equal(baseline.Points, result.Points);
        Assert.Equal(baseline.DistanceMeters, result.DistanceMeters);
        Assert.Equal(baseline.ElapsedTimeSeconds, result.ElapsedTimeSeconds);
    }

    [Theory]
    [InlineData("running", SportKind.Running)]
    [InlineData("cycling", SportKind.Cycling)]
    [InlineData("walking", SportKind.Walking)]
    [InlineData("hiking", SportKind.Walking)]
    public async Task Gpx_maps_supported_sports(string sourceSport, SportKind expected)
    {
        var directory = TestSupport.NewDirectory();
        var path = TestSupport.Write(directory, "activity.gpx", TestSupport.Gpx(sourceSport));
        var result = await _importer.ReadAsync(path, SourceKind.Gpx);
        Assert.Equal(expected, Assert.Single(result).Parsed.Sport);
    }

    [Theory]
    [InlineData("indoor cycling", true)]
    [InlineData("virtual cycling", true)]
    [InlineData("treadmill running", true)]
    [InlineData("outdoor cycling", false)]
    public async Task Xml_activity_labels_provide_indoor_classification(string sourceSport, bool expected)
    {
        var directory = TestSupport.NewDirectory();
        var path = TestSupport.Write(directory, "classified.gpx", TestSupport.Gpx(sourceSport));
        var activity = Assert.Single(await _importer.ReadAsync(path, SourceKind.Gpx)).Parsed;
        Assert.Equal(expected, activity.IsIndoor);
    }

    [Fact]
    public async Task Generic_xml_activity_label_defers_to_the_gps_fallback()
    {
        var directory = TestSupport.NewDirectory();
        var path = TestSupport.Write(directory, "generic.gpx", TestSupport.Gpx("cycling"));
        Assert.Null(Assert.Single(await _importer.ReadAsync(path, SourceKind.Gpx)).Parsed.IsIndoor);
    }
    [Fact]
    public async Task Gpx_preserves_source_offset_and_utc_time()
    {
        var directory = TestSupport.NewDirectory();
        var path = TestSupport.Write(directory, "activity.gpx", TestSupport.Gpx());
        var activity = Assert.Single(await _importer.ReadAsync(path, SourceKind.Gpx)).Parsed;
        Assert.Equal(TimeSpan.FromMinutes(150), activity.OriginalUtcOffset);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 7, 30, 0, TimeSpan.Zero), activity.StartTimeUtc);
        Assert.Equal(120, activity.ElapsedTimeSeconds);
        Assert.True(activity.DistanceMeters > 200);
    }

    [Fact]
    public async Task Tcx_reads_lap_and_sensors()
    {
        var directory = TestSupport.NewDirectory();
        var path = TestSupport.Write(directory, "activity.tcx", TestSupport.Tcx());
        var activity = Assert.Single(await _importer.ReadAsync(path, SourceKind.Tcx)).Parsed;
        Assert.Equal(SportKind.Cycling, activity.Sport);
        Assert.Single(activity.Laps);
        Assert.Equal(130, activity.AverageHeartRate);
        Assert.Equal(85, activity.AverageCadence);
        Assert.Equal(150, activity.DistanceMeters);
    }

    [Fact]
    public async Task Gpx_movement_estimate_uses_motion_but_omits_stops_track_breaks_and_sparse_intervals()
    {
        var path = TestSupport.Write(TestSupport.NewDirectory(), "paused.gpx", """
            <gpx><trk><type>Running</type><trkseg>
              <trkpt lat="1" lon="-30"><time>2026-01-01T00:00:00Z</time></trkpt>
              <trkpt lat="1" lon="-30"><time>2026-01-01T00:00:08Z</time></trkpt>
              <trkpt lat="1.0001" lon="-30"><time>2026-01-01T00:00:18Z</time></trkpt>
              <trkpt lat="1.0002" lon="-30"><time>2026-01-01T00:01:08Z</time></trkpt>
            </trkseg><trkseg>
              <trkpt lat="2" lon="-30"><time>2026-01-01T00:01:13Z</time></trkpt>
              <trkpt lat="2.0001" lon="-30"><time>2026-01-01T00:01:23Z</time></trkpt>
            </trkseg></trk></gpx>
            """);

        var activity = Assert.Single(await _importer.ReadAsync(path, SourceKind.Gpx)).Parsed;

        Assert.Equal(83, activity.ElapsedTimeSeconds);
        Assert.Equal(20, activity.MovingTimeSeconds);
        Assert.Equal(MovingTimeSource.EstimatedFromRecords, activity.MovingTimeSource);
        Assert.InRange(activity.DistanceMeters, 33, 34);
        Assert.Equal(0, activity.Points[1].SpeedMetersPerSecond);
        Assert.Null(activity.Points[3].SpeedMetersPerSecond);
        Assert.Null(activity.Points[4].SpeedMetersPerSecond);
    }

    [Theory]
    [InlineData("", MovingTimeSource.Unavailable)]
    [InlineData("<Extensions><TPX><Speed>0</Speed></TPX></Extensions>", MovingTimeSource.EstimatedFromRecords)]
    public async Task Tcx_distinguishes_unavailable_and_observed_stationary_time(string sensor, MovingTimeSource expected)
    {
        var path = TestSupport.Write(TestSupport.NewDirectory(), "indoor.tcx", $"""
            <TrainingCenterDatabase><Activities><Activity Sport="Treadmill Running"><Lap>
              <TotalTimeSeconds>60</TotalTimeSeconds><DistanceMeters>50</DistanceMeters><Track>
                <Trackpoint><Time>2026-01-01T00:00:00Z</Time><Cadence>80</Cadence></Trackpoint>
                <Trackpoint><Time>2026-01-01T00:00:10Z</Time><Cadence>80</Cadence>{sensor}</Trackpoint>
              </Track>
            </Lap></Activity></Activities></TrainingCenterDatabase>
            """);

        var activity = Assert.Single(await _importer.ReadAsync(path, SourceKind.Tcx)).Parsed;

        Assert.Equal(expected, activity.MovingTimeSource);
        Assert.Equal(0, activity.MovingTimeSeconds);
        Assert.Equal(10, activity.ElapsedTimeSeconds);
        Assert.Equal(50, activity.DistanceMeters);
        Assert.Null(activity.TimerTimeSeconds);
        Assert.Equal(0, Assert.Single(activity.Laps).MovingSeconds);
    }

    [Theory]
    [InlineData(120, 145, 270)]
    [InlineData(0, 25, 150)]
    public async Task Tcx_uses_declared_lap_distance_then_record_deltas_for_both_counter_layouts(
        int first, int middle, int last)
    {
        var path = TestSupport.Write(TestSupport.NewDirectory(), "laps.tcx", $"""
            <TrainingCenterDatabase><Activities><Activity Sport="Biking">
              <Lap><DistanceMeters>150</DistanceMeters><Track>
                <Trackpoint><Time>2026-01-01T00:00:00Z</Time><DistanceMeters>20</DistanceMeters></Trackpoint>
                <Trackpoint><Time>2026-01-01T00:00:10Z</Time><DistanceMeters>120</DistanceMeters></Trackpoint>
              </Track></Lap>
              <Lap><Track>
                <Trackpoint><Time>2026-01-01T00:01:00Z</Time><DistanceMeters>{first}</DistanceMeters></Trackpoint>
                <Trackpoint><Time>2026-01-01T00:01:10Z</Time><DistanceMeters>{middle}</DistanceMeters></Trackpoint>
                <Trackpoint><Time>2026-01-01T00:01:20Z</Time><DistanceMeters>{last}</DistanceMeters></Trackpoint>
              </Track></Lap>
            </Activity></Activities></TrainingCenterDatabase>
            """);

        var activity = Assert.Single(await _importer.ReadAsync(path, SourceKind.Tcx)).Parsed;

        Assert.Equal(300, activity.DistanceMeters);
        Assert.Equal(80, activity.ElapsedTimeSeconds);
        Assert.Equal(30, activity.MovingTimeSeconds);
        Assert.Equal(MovingTimeSource.EstimatedFromRecords, activity.MovingTimeSource);
        Assert.Equal(new double?[] { 0, 100, 100, 125, 250 }, activity.Points.Select(point => point.DistanceMeters));
        Assert.Null(activity.Points[2].SpeedMetersPerSecond);
        Assert.Collection(activity.Laps,
            lap => Assert.Equal(new LapCandidate(1, 150, 10, 10, null, null), lap),
            lap => Assert.Equal(new LapCandidate(2, 150, 20, 20, null, null), lap));
    }

    [Fact]
    public async Task Unsupported_sport_is_reported()
    {
        var directory = TestSupport.NewDirectory();
        var path = TestSupport.Write(directory, "swim.gpx", TestSupport.Gpx("swimming"));
        await Assert.ThrowsAsync<UnsupportedActivityException>(() => _importer.ReadAsync(path, SourceKind.Gpx));
    }

    [Fact]
    public async Task Corrupt_xml_is_rejected()
    {
        var directory = TestSupport.NewDirectory();
        var path = TestSupport.Write(directory, "bad.gpx", TestSupport.Gpx() + "</extra>");
        await Assert.ThrowsAsync<XmlException>(() => _importer.ReadAsync(path, SourceKind.Gpx));
    }

    [Fact]
    public async Task Dtd_is_prohibited()
    {
        var directory = TestSupport.NewDirectory();
        var gpx = TestSupport.Gpx().Replace("<gpx ", "<!DOCTYPE gpx>\n<gpx ");
        var path = TestSupport.Write(directory, "doctype.gpx", gpx);
        await Assert.ThrowsAsync<XmlException>(() => _importer.ReadAsync(path, SourceKind.Gpx));
    }
}
