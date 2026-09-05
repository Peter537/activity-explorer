using System.IO.Compression;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Dynastream.Fit;

namespace ActivityExplorer.Tests;

public sealed class RowingImporterTests
{
    [Theory]
    [InlineData(Sport.FitnessEquipment, SubSport.IndoorRowing, true)]
    [InlineData(Sport.Rowing, SubSport.IndoorRowing, true)]
    [InlineData(Sport.Rowing, SubSport.VirtualActivity, true)]
    [InlineData(Sport.Rowing, SubSport.Generic, null)]
    public async Task Fit_rowing_classification_preserves_streams_and_strokes(Sport sport, SubSport subSport, bool? indoor)
    {
        var path = TestSupport.RowingFit(TestSupport.NewDirectory(), sport, subSport, withGps: indoor is null);
        var candidate = Assert.Single(await new FitActivityImporter().ReadAsync(path, SourceKind.Fit));
        var parsed = candidate.Parsed;
        Assert.Equal(SportKind.Rowing, parsed.Sport);
        Assert.Equal(indoor, parsed.IsIndoor);
        Assert.Equal(2, candidate.ParserVersion);
        Assert.Equal(1500, parsed.DistanceMeters);
        Assert.Equal(600, parsed.TimerTimeSeconds);
        Assert.Equal(600, parsed.MovingTimeSeconds);
        Assert.Equal(2, parsed.Laps.Count);
        Assert.Equal(301, parsed.Points.Count);
        Assert.Equal(24, parsed.AverageCadence);
        Assert.Equal(28, parsed.MaxCadence);
        Assert.Equal(100, parsed.AveragePowerWatts);
        Assert.Null(parsed.PedalRevolutions);
        var strokes = Assert.Single(parsed.Metrics, metric => metric.Key == "fit.total_strokes");
        Assert.Equal(240, strokes.NumericValue);
        Assert.Equal("strokes", strokes.Unit);
        Assert.All(parsed.Points, point => Assert.Equal(indoor is null, point.Latitude.HasValue));
    }

    [Fact]
    public async Task Missing_rowing_sensors_remain_missing()
    {
        var path = TestSupport.RowingFit(TestSupport.NewDirectory(), withSensors: false);
        var parsed = Assert.Single(await new FitActivityImporter().ReadAsync(path, SourceKind.Fit)).Parsed;
        Assert.Null(parsed.AveragePowerWatts);
        Assert.Null(parsed.AverageCadence);
        Assert.DoesNotContain(parsed.Metrics, metric => metric.Key == "fit.total_strokes");
    }

    [Theory]
    [InlineData(SubSport.Generic)]
    [InlineData(SubSport.Elliptical)]
    public async Task Unrelated_fitness_equipment_is_rejected(SubSport subSport)
    {
        var path = TestSupport.RowingFit(TestSupport.NewDirectory(), subSport: subSport);
        await Assert.ThrowsAsync<UnsupportedActivityException>(() => new FitActivityImporter().ReadAsync(path, SourceKind.Fit));
    }

    [Theory]
    [InlineData("Rowing", null)]
    [InlineData("indoor_rowing", true)]
    [InlineData("VirtualRow", true)]
    [InlineData("Outdoor Rowing", false)]
    public async Task Xml_rowing_labels_are_supported(string label, bool? indoor)
    {
        var path = TestSupport.Write(TestSupport.NewDirectory(), "row.tcx", TestSupport.Tcx(label));
        var parsed = Assert.Single(await new XmlActivityImporter().ReadAsync(path, SourceKind.Tcx)).Parsed;
        Assert.Equal(SportKind.Rowing, parsed.Sport);
        Assert.Equal(indoor, parsed.IsIndoor);
    }

    [Fact]
    public async Task Gps_free_xml_retains_missing_distance_boundaries()
    {
        var path = TestSupport.Write(TestSupport.NewDirectory(), "row.tcx", """
            <TrainingCenterDatabase><Activities><Activity Sport="IndoorRowing"><Lap><Track>
            <Trackpoint><Time>2026-01-01T00:00:00Z</Time><DistanceMeters>0</DistanceMeters></Trackpoint>
            <Trackpoint><Time>2026-01-01T00:00:20Z</Time></Trackpoint>
            <Trackpoint><Time>2026-01-01T00:01:00Z</Time><DistanceMeters>150</DistanceMeters></Trackpoint>
            </Track></Lap></Activity></Activities></TrainingCenterDatabase>
            """);
        var parsed = Assert.Single(await new XmlActivityImporter().ReadAsync(path, SourceKind.Tcx)).Parsed;
        Assert.Null(parsed.Points[1].DistanceMeters);
        Assert.Null(parsed.Points[2].SpeedMetersPerSecond);
        Assert.Null(BestEffortCalculator.BestDistance(parsed.Points, 100, SportKind.Rowing));
        Assert.Null(BestEffortCalculator.BestTimedDistance(parsed.Points, 60, SportKind.Rowing));
    }

    [Theory]
    [InlineData("Other", "Indoor Rowing", 1)]
    [InlineData("", "VirtualRow", 1)]
    [InlineData("Swimming", "Rowing", 0)]
    [InlineData("Other", "Unknown", 0)]
    public async Task Strava_rowing_metadata_fills_only_missing_or_generic_xml_sport(string sport, string label, int expected)
    {
        var directory = TestSupport.NewDirectory();
        var path = Path.Combine(directory, "export.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("activities.csv").Open()))
                writer.Write($"Activity ID,Activity Type,Filename\n123,{label},activities/123.tcx");
            using var xml = new StreamWriter(archive.CreateEntry("activities/123.tcx").Open());
            xml.Write(TestSupport.Tcx(sport));
        }
        var previous = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_DATA");
        try
        {
            Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", Path.Combine(directory, "data"));
            var importer = new ArchiveActivityImporter(new AppDataPaths(), new FitActivityImporter(), new XmlActivityImporter());
            var result = await importer.ReadAsync(path, SourceKind.StravaArchive);
            Assert.Equal(expected, result.Count);
            Assert.All(result, candidate =>
            {
                Assert.Equal(SportKind.Rowing, candidate.Parsed.Sport);
                Assert.True(candidate.Parsed.IsIndoor);
            });
        }
        finally { Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", previous); }
        var standalone = TestSupport.Write(directory, "standalone.tcx", TestSupport.Tcx(sport));
        await Assert.ThrowsAsync<UnsupportedActivityException>(() => new XmlActivityImporter().ReadAsync(standalone, SourceKind.Tcx));
    }
}
