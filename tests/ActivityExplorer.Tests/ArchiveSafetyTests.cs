using System.IO.Compression;
using System.Text;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Storage;

namespace ActivityExplorer.Tests;

public sealed class ArchiveSafetyTests
{
    [Fact]
    public async Task Archive_imports_nested_activity_file()
    {
        var directory = TestSupport.NewDirectory();
        var archivePath = TestSupport.Zip(directory, "DI_CONNECT/UploadedFiles/run.gpx", TestSupport.Gpx());
        var importer = CreateImporter(Path.Combine(directory, "data"));
        var result = await importer.ReadAsync(archivePath, SourceKind.GarminArchive);
        Assert.Equal(SportKind.Running, Assert.Single(result).Parsed.Sport);
    }

    [Fact]
    public async Task Archive_rejects_path_traversal()
    {
        var directory = TestSupport.NewDirectory();
        var archivePath = TestSupport.Zip(directory, "../escaped.gpx", TestSupport.Gpx());
        var importer = CreateImporter(Path.Combine(directory, "data"));
        await Assert.ThrowsAsync<UnsafeArchiveException>(() => importer.ReadAsync(archivePath, SourceKind.GarminArchive));
        Assert.False(File.Exists(Path.Combine(directory, "escaped.gpx")));
    }

    [Fact]
    public async Task Archive_rejects_symbolic_link_entries()
    {
        var directory = TestSupport.NewDirectory();
        var path = Path.Combine(directory, "symlink.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("link.gpx");
            entry.ExternalAttributes = 0xA000 << 16;
        }
        var importer = CreateImporter(Path.Combine(directory, "data"));
        await Assert.ThrowsAsync<UnsafeArchiveException>(() => importer.ReadAsync(path, SourceKind.GarminArchive));
    }

    [Fact]
    public async Task Gzip_wrapped_gpx_is_supported()
    {
        var directory = TestSupport.NewDirectory();
        var path = Path.Combine(directory, "walk.gpx.gz");
        await using (var output = File.Create(path))
        await using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
        await using (var writer = new StreamWriter(gzip))
            await writer.WriteAsync(TestSupport.Gpx("walking"));
        var importer = CreateImporter(Path.Combine(directory, "data"));
        var result = await importer.ReadAsync(path, SourceKind.GarminArchive);
        Assert.Equal(SportKind.Walking, Assert.Single(result).Parsed.Sport);
    }

    [Fact]
    public async Task Documented_Garmin_layout_ignores_unrelated_wellness_files()
    {
        var directory = TestSupport.NewDirectory();
        var path = Path.Combine(directory, "garmin-export.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "DI_CONNECT/DI_Connect-Fitness-Uploaded-Files/ride.gpx", TestSupport.Gpx("cycling", "Official activity"));
            WriteEntry(archive, "DI_CONNECT/Wellness/wellness.gpx", TestSupport.Gpx("walking", "Not an activity upload"));
        }

        var importer = CreateImporter(Path.Combine(directory, "data"));
        var candidate = Assert.Single(await importer.ReadAsync(path, SourceKind.GarminArchive));
        Assert.Equal(new ActivityExplorer.Core.Models.ImporterDiagnostics(0, 0, null), importer.ConsumeDiagnostics());
        Assert.Equal(SportKind.Cycling, candidate.Parsed.Sport);
        Assert.Equal(SourceProvider.Garmin, candidate.Provider);
        Assert.Equal(AcquisitionMethod.AccountExport, candidate.AcquisitionMethod);
    }

    [Fact]
    public async Task Nested_uploaded_files_archive_keeps_Garmin_layout_context()
    {
        var directory = TestSupport.NewDirectory();
        byte[] nestedBytes;
        using (var memory = new MemoryStream())
        {
            using (var nested = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
                WriteEntry(nested, "123456_ACTIVITY.gpx", TestSupport.Gpx("running", "Nested activity"));
            nestedBytes = memory.ToArray();
        }

        var path = Path.Combine(directory, "garmin-nested-export.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var nested = archive.CreateEntry("DI_CONNECT/DI-Connect-Uploaded-Files/UploadedFiles_0.zip");
            using (var output = nested.Open()) output.Write(nestedBytes);
            WriteEntry(archive, "DI_CONNECT/Wellness/wellness.gpx", TestSupport.Gpx("walking", "Not an activity upload"));
        }

        var candidate = Assert.Single(await CreateImporter(Path.Combine(directory, "data"))
            .ReadAsync(path, SourceKind.GarminArchive));
        Assert.Equal(SportKind.Running, candidate.Parsed.Sport);
    }

    [Fact]
    public async Task Garmin_layout_reports_corrupt_unsupported_and_unrecognized_entries()
    {
        var directory = TestSupport.NewDirectory();
        var path = Path.Combine(directory, "garmin-warnings.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "DI_CONNECT/DI_Connect-Fitness-Uploaded-Files/bad.fit", "not fit");
            WriteEntry(archive, "DI_CONNECT/DI_Connect-Fitness-Uploaded-Files/swim.gpx", TestSupport.Gpx("swimming"));
            WriteEntry(archive, "DI_CONNECT/DI_Connect-Fitness-Uploaded-Files/readme.json", "{}");
        }

        var importer = CreateImporter(Path.Combine(directory, "data"));
        Assert.Empty(await importer.ReadAsync(path, SourceKind.GarminArchive));
        var diagnostics = importer.ConsumeDiagnostics();
        Assert.Equal(2, diagnostics.Unsupported);
        Assert.Equal(1, diagnostics.Warnings);
        Assert.Contains("corrupt", diagnostics.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Strava_bulk_export_enriches_gzip_wrapped_FIT_GPX_and_TCX_files()
    {
        var directory = TestSupport.NewDirectory();
        var fitBytes = await File.ReadAllBytesAsync(TestSupport.CyclingFit(directory, "source.fit"));
        var path = Path.Combine(directory, "strava-export.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "activities.csv", """
                Activity ID,Activity Name,Activity Type,Activity Description,Filename,Activity Gear
                111,"FIT ride, edited",Virtual Ride,FIT description,activities/111.fit.gz,Indoor bike
                222,GPX walk,Walk,GPX description,activities/222.gpx.gz,Walking shoes
                333,TCX ride,Ride,TCX description,activities/333.tcx.gz,Road bike
                """);
            WriteGzipEntry(archive, "activities/111.fit.gz", fitBytes);
            WriteGzipEntry(archive, "activities/222.gpx.gz", Encoding.UTF8.GetBytes(TestSupport.Gpx("walking")));
            WriteGzipEntry(archive, "activities/333.tcx.gz", Encoding.UTF8.GetBytes(TestSupport.Tcx()));
        }

        var importer = CreateImporter(Path.Combine(directory, "data"));
        var result = await importer.ReadAsync(path, SourceKind.StravaArchive);
        var byId = result.ToDictionary(candidate => candidate.ExternalId!);

        Assert.Equal(3, result.Count);
        Assert.Equal("FIT ride, edited", byId["111"].Parsed.Title);
        Assert.Equal("FIT description", byId["111"].Parsed.Description);
        Assert.True(byId["111"].Parsed.IsIndoor);
        Assert.Equal("Indoor bike", byId["111"].Parsed.GearName);
        Assert.Equal("111.fit", byId["111"].OriginalName);
        Assert.Equal("222.gpx", byId["222"].OriginalName);
        Assert.Equal("333.tcx", byId["333"].OriginalName);
        Assert.All(result, candidate =>
        {
            Assert.Equal(SourceProvider.Strava, candidate.Provider);
            Assert.Equal(AcquisitionMethod.AccountExport, candidate.AcquisitionMethod);
        });
        Assert.Equal(new ActivityExplorer.Core.Models.ImporterDiagnostics(0, 0, null), importer.ConsumeDiagnostics());
    }

    [Fact]
    public async Task Strava_bulk_export_detects_wrapped_root_and_ignores_non_activity_files()
    {
        var directory = TestSupport.NewDirectory();
        var path = Path.Combine(directory, "wrapped-strava-export.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "export/activities.csv", """
                Activity ID,Activity Name,Activity Type,Activity Description,Filename,Activity Gear
                444,Recorded walk,Walk,Imported description,activities/444.gpx,Trail shoes
                555,Manual activity,Walk,No recording,,
                """);
            WriteEntry(archive, "export/activities/444.gpx", TestSupport.Gpx("walking", "Original title"));
            WriteEntry(archive, "export/routes/not-an-activity.gpx", TestSupport.Gpx("walking", "Saved route"));
        }

        Assert.True(ImportSourceDetector.IsStravaBulkExport(path));
        var importer = CreateImporter(Path.Combine(directory, "data"));
        var candidate = Assert.Single(await importer.ReadAsync(path, SourceKind.StravaArchive));

        Assert.Equal("444", candidate.ExternalId);
        Assert.Equal("Recorded walk", candidate.Parsed.Title);
        Assert.Equal("Imported description", candidate.Parsed.Description);
        Assert.Equal("Trail shoes", candidate.Parsed.GearName);
        Assert.Equal("444.gpx", candidate.OriginalName);
    }

    [Fact]
    public async Task Strava_basename_fallback_does_not_choose_between_ambiguous_rows()
    {
        var directory = TestSupport.NewDirectory();
        var path = Path.Combine(directory, "ambiguous-strava-export.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "activities.csv", """
                Activity ID,Activity Name,Activity Type,Activity Description,Filename,Activity Gear
                100,First row,Run,,activities/first/repeated.gpx,
                200,Second row,Run,,activities/second/repeated.gpx,
                """);
            WriteEntry(archive, "activities/legacy/repeated.gpx", TestSupport.Gpx("running", "Original title"));
        }

        var candidate = Assert.Single(await CreateImporter(Path.Combine(directory, "data"))
            .ReadAsync(path, SourceKind.StravaArchive));

        Assert.Null(candidate.ExternalId);
        Assert.Equal("Original title", candidate.Parsed.Title);
    }

    [Fact]
    public async Task Strava_bulk_export_warns_for_corrupt_nested_gzip_and_imports_valid_siblings()
    {
        var directory = TestSupport.NewDirectory();
        var path = Path.Combine(directory, "partly-corrupt-strava-export.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "activities.csv", """
                Activity ID,Activity Name,Activity Type,Activity Description,Filename,Activity Gear
                300,Valid walk,Walk,,activities/300.gpx.gz,
                301,Corrupt walk,Walk,,activities/301.gpx.gz,
                """);
            WriteGzipEntry(archive, "activities/300.gpx.gz", Encoding.UTF8.GetBytes(TestSupport.Gpx("walking")));
            WriteBinaryEntry(archive, "activities/301.gpx.gz", Encoding.UTF8.GetBytes("not gzip data"));
        }

        var importer = CreateImporter(Path.Combine(directory, "data"));
        var candidate = Assert.Single(await importer.ReadAsync(path, SourceKind.StravaArchive));
        var diagnostics = importer.ConsumeDiagnostics();

        Assert.Equal("300", candidate.ExternalId);
        Assert.Equal(0, diagnostics.Unsupported);
        Assert.Equal(1, diagnostics.Warnings);
        Assert.Contains("corrupt", diagnostics.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generic_zip_is_not_detected_as_Strava_bulk_export()
    {
        var directory = TestSupport.NewDirectory();
        var path = TestSupport.Zip(directory, "activities/ride.gpx", TestSupport.Gpx());
        Assert.False(ImportSourceDetector.IsStravaBulkExport(path));
    }

    private static void WriteGzipEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name);
        using var output = entry.Open();
        using var gzip = new GZipStream(output, CompressionLevel.SmallestSize);
        gzip.Write(content);
    }

    private static void WriteBinaryEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name);
        using var output = entry.Open();
        output.Write(content);
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static ArchiveActivityImporter CreateImporter(string dataPath)
    {
        Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", dataPath);
        return new ArchiveActivityImporter(new AppDataPaths(), new FitActivityImporter(), new XmlActivityImporter());
    }
}
