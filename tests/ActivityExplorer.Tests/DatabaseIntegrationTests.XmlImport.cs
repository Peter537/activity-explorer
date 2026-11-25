using System.IO.Compression;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Tcx_with_the_same_provider_identity_preserves_canonical_fit_movement_and_laps()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Cross-format rider");
        var previousRoot = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_DATA");
        try
        {
            Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", setup.DataDirectory);
            var paths = new AppDataPaths();
            var storage = CreateStorageServices(setup, paths);
            var fit = new FitActivityImporter();
            var archiveImporter = new ArchiveActivityImporter(paths, fit, new XmlActivityImporter());
            var processor = new ImportProcessor(setup.Factory, [archiveImporter], paths, storage.Originals,
                storage.FileOperations, storage.OwnerLock, new StatisticsService(setup.Factory),
                new SegmentService(setup.Factory, new SegmentMatcher(), storage.OwnerLock), NullLogger<ImportProcessor>.Instance);
            var fitPath = TestSupport.CyclingFit(setup.DataDirectory, "cycling.fit", includePower: true);
            var tcxPath = TestSupport.Write(setup.DataDirectory, "cycling.tcx", TestSupport.Tcx());
            var expected = Assert.Single(await fit.ReadAsync(fitPath, SourceKind.Fit)).Parsed;

            await ProcessExportAsync(fitPath);
            await ProcessExportAsync(tcxPath);
            await ProcessExportAsync(tcxPath);

            await using var db = await setup.Factory.CreateDbContextAsync();
            var activity = await db.Activities.SingleAsync();
            var lap = await db.ActivityLaps.SingleAsync();
            Assert.Equal(MovingTimeSource.FitSession, activity.MovingTimeSource);
            Assert.Equal(expected.MovingTimeSeconds, activity.MovingTimeSeconds);
            Assert.Equal(expected.DistanceMeters, activity.DistanceMeters);
            Assert.Equal(expected.AveragePowerWatts, activity.AveragePowerWatts);
            Assert.Equal(Assert.Single(expected.Laps).DistanceMeters, lap.DistanceMeters);
            Assert.Equal(2, await db.SourceFiles.CountAsync());

            async Task ProcessExportAsync(string recordingPath)
            {
                var staging = ManagedStagingDirectory.Create(paths.StagingPath);
                var archivePath = Path.Combine(staging, "strava-export.zip");
                using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
                {
                    var recordedName = "activities/" + Path.GetFileName(recordingPath);
                    archive.CreateEntryFromFile(recordingPath, recordedName);
                    using var csv = new StreamWriter(archive.CreateEntry("activities.csv").Open());
                    csv.WriteLine("Activity ID,Activity Type,Filename");
                    csv.WriteLine($"42,Ride,{recordedName}");
                }

                var batch = new ImportBatch
                {
                    OwnerId = owner,
                    SourceKind = SourceKind.StravaArchive,
                    DisplayName = Path.GetFileName(archivePath),
                    StagedPath = ManagedPathGuard.RelativeTo(paths.Root, archivePath)
                };
                await using (var writeDb = await setup.Factory.CreateDbContextAsync())
                {
                    writeDb.ImportBatches.Add(batch);
                    await writeDb.SaveChangesAsync();
                }
                await processor.ProcessAsync(batch.Id);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", previousRoot);
        }
    }
}
