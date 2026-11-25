using System.IO.Compression;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Storage;
using Dynastream.Fit;
using FitDateTime = Dynastream.Fit.DateTime;
using FitFile = Dynastream.Fit.File;

namespace ActivityExplorer.Tests;

public sealed class ActivityImportSessionTests
{
    [Fact]
    public async Task Fit_with_multiple_sessions_is_unsupported()
    {
        var path = WriteFitWithTwoSessions(TestSupport.NewDirectory());
        var error = await Assert.ThrowsAsync<UnsupportedActivityException>(() => new FitActivityImporter().ReadAsync(path, SourceKind.Fit));
        Assert.Contains("multiple sessions", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tcx_with_multiple_activities_is_unsupported_even_when_the_first_sport_is_missing()
    {
        var path = TestSupport.Write(TestSupport.NewDirectory(), "two-activities.tcx", MultipleTcxActivities());
        var error = await Assert.ThrowsAsync<UnsupportedActivityException>(() => new XmlActivityImporter().ReadAsync(path, SourceKind.Tcx));
        Assert.Contains("multiple activities", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archive_skips_multisession_files_and_preserves_valid_sibling()
    {
        var directory = TestSupport.NewDirectory();
        var path = Path.Combine(directory, "mixed.zip");
        var fitPath = WriteFitWithTwoSessions(directory);
        var tcxPath = TestSupport.Write(directory, "two-activities.tcx", MultipleTcxActivities());
        var gpxPath = TestSupport.Write(directory, "valid.gpx", TestSupport.Gpx());
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var fixture in new[] { fitPath, tcxPath, gpxPath })
                archive.CreateEntryFromFile(fixture, Path.GetFileName(fixture));
        }

        var previous = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_DATA");
        try
        {
            Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", Path.Combine(directory, "data"));
            var importer = new ArchiveActivityImporter(new AppDataPaths(), new FitActivityImporter(), new XmlActivityImporter());
            var (candidates, diagnostics) = await importer.ReadWithDiagnosticsAsync(path, SourceKind.GarminArchive);

            Assert.Equal("valid.gpx", Assert.Single(candidates).OriginalName);
            Assert.Equal(2, diagnostics.Unsupported);
            Assert.Equal(0, diagnostics.Warnings);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", previous);
        }
    }

    private static string MultipleTcxActivities()
    {
        var tcx = TestSupport.Tcx();
        var start = tcx.IndexOf("<Activity ", StringComparison.Ordinal);
        var end = tcx.IndexOf("</Activities>", StringComparison.Ordinal);
        var first = tcx[start..end].Replace(" Sport=\"Biking\"", "", StringComparison.Ordinal);
        return tcx[..start] + first + tcx[start..];
    }

    private static string WriteFitWithTwoSessions(string directory)
    {
        var path = Path.Combine(directory, "two-sessions.fit");
        using var stream = System.IO.File.Create(path);
        var encoder = new Encode(stream, ProtocolVersion.V20);
        var start = new System.DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var file = new FileIdMesg();
        file.SetType(FitFile.Activity);
        file.SetTimeCreated(new FitDateTime(start));
        encoder.Write(file);

        foreach (var (offset, sport) in new[] { (0, Sport.Cycling), (60, Sport.Running) })
        {
            var session = new SessionMesg();
            session.SetStartTime(new FitDateTime(start.AddSeconds(offset)));
            session.SetTimestamp(new FitDateTime(start.AddSeconds(offset + 30)));
            session.SetSport(sport);
            session.SetTotalElapsedTime(30);
            session.SetTotalMovingTime(20);
            encoder.Write(session);
        }
        encoder.Close();
        return path;
    }
}
