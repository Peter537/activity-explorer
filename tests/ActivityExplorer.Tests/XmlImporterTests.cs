using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Import;

namespace ActivityExplorer.Tests;

public sealed class XmlImporterTests
{
    private readonly XmlActivityImporter _importer = new();

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
        var path = TestSupport.Write(directory, "bad.gpx", "<gpx><trk>");
        await Assert.ThrowsAnyAsync<Exception>(() => _importer.ReadAsync(path, SourceKind.Gpx));
    }

    [Fact]
    public async Task Dtd_is_prohibited()
    {
        var directory = TestSupport.NewDirectory();
        var path = TestSupport.Write(directory, "unsafe.gpx", "<!DOCTYPE gpx [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><gpx>&xxe;</gpx>");
        await Assert.ThrowsAnyAsync<Exception>(() => _importer.ReadAsync(path, SourceKind.Gpx));
    }
}
