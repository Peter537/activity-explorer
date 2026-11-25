using System.Globalization;
using System.IO.Compression;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_archive_resumes_only_a_proven_prefix_and_preserves_an_ambiguous_archive(bool ambiguous)
    {
        await using var fixture = await ImportRecoveryFixture.CreateAsync();
        var first = TestSupport.Gpx(name: "Previously committed");
        var second = TestSupport.Gpx(name: "Waiting to import").Replace("2026-01-01", "2026-01-02", StringComparison.Ordinal);
        var id = await fixture.AddArchiveAsync("legacy", [("first.gpx", first), ("second.gpx", second)]);
        using var interrupted = new CancellationTokenSource();
        var processor = fixture.CreateProcessor(fixture.WithInterceptor(new InterruptCheckpoint(2, interrupted)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessAsync(id, interrupted.Token));

        string staged;
        await using (var db = await fixture.Setup.Factory.CreateDbContextAsync())
        {
            await db.Set<ImportBatchCheckpoint>().Where(row => row.BatchId == id).ExecuteDeleteAsync();
            var old = await db.ImportBatches.SingleAsync(batch => batch.Id == id);
            Assert.Equal(1, old.ActivitiesCreated);
            if (ambiguous) old.DuplicatesSkipped = 1;
            staged = old.StagedPath;
            await db.SaveChangesAsync();
        }

        await fixture.Processor.ProcessAsync(id);

        await using var verify = await fixture.Setup.Factory.CreateDbContextAsync();
        var result = await verify.ImportBatches.SingleAsync(batch => batch.Id == id);
        Assert.Equal(ambiguous ? ImportStatus.ReviewRequired : ImportStatus.Completed, result.Status);
        Assert.Equal(ambiguous ? 1 : 2, result.ActivitiesCreated);
        Assert.Equal(0, result.ActivitiesUpdated);
        Assert.Equal(ambiguous ? 1 : 0, result.DuplicatesSkipped);
        if (ambiguous)
        {
            Assert.Contains("staged file were kept", result.ErrorMessage, StringComparison.Ordinal);
            await StagingRecovery.ReconcileAsync(verify, new AppDataPaths(), NullLogger.Instance, CancellationToken.None);
        }
        Assert.Equal(ambiguous, File.Exists(staged));
    }
    [Fact]
    public async Task Distinct_gpx_sources_with_same_activity_import_and_replay_successfully()
    {
        await using var fixture = await ImportRecoveryFixture.CreateAsync();
        var first = await fixture.AddBatchAsync("first", TestSupport.Gpx(name: "First"));
        var second = await fixture.AddBatchAsync("second", TestSupport.Gpx(name: "Second"));
        var duplicate = await fixture.AddBatchAsync("duplicate", TestSupport.Gpx(name: "Second"));

        await fixture.Processor.ProcessAsync(first);
        await fixture.Processor.ProcessAsync(second);
        await fixture.Processor.ProcessAsync(duplicate);

        await using var db = await fixture.Setup.Factory.CreateDbContextAsync();
        var activity = await db.Activities.Include(x => x.SourceFiles).SingleAsync();
        Assert.Equal(2, activity.SourceFiles.Count);
        Assert.Equal(2, activity.SourceFiles.Select(x => x.Sha256).Distinct().Count());
        var batches = await db.ImportBatches.OrderBy(x => x.DisplayName).ToArrayAsync();
        Assert.All(batches, batch => Assert.Equal(ImportStatus.Completed, batch.Status));
        Assert.Equal(1, batches.Single(x => x.Id == first).ActivitiesCreated);
        Assert.Equal(1, batches.Single(x => x.Id == second).ActivitiesUpdated);
        Assert.Equal(1, batches.Single(x => x.Id == duplicate).DuplicatesSkipped);
        Assert.All(activity.SourceFiles, source => Assert.True(File.Exists(fixture.Originals.ResolveStoredPath(source.StoredPath))));
    }

    [Fact]
    public async Task Candidate_save_failure_records_failed_batch_without_resaving_failed_graph()
    {
        await using var fixture = await ImportRecoveryFixture.CreateAsync();
        var first = await fixture.AddBatchAsync("first", TestSupport.Gpx(name: "First"));
        await fixture.Processor.ProcessAsync(first);
        var second = await fixture.AddBatchAsync("second", TestSupport.Gpx(name: "Second"));
        var failingFactory = new TestDbFactory(new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(fixture.Setup.DataDirectory, "test.db")}")
            .AddInterceptors(new FailCandidateSave())
            .Options);
        var failingProcessor = fixture.CreateProcessor(failingFactory);

        await failingProcessor.ProcessAsync(second);

        await using var db = await fixture.Setup.Factory.CreateDbContextAsync();
        var batch = await db.ImportBatches.SingleAsync(x => x.Id == second);
        Assert.Equal(ImportStatus.Failed, batch.Status);
        Assert.NotNull(batch.CompletedAtUtc);
        Assert.Contains("Synthetic candidate save failure", batch.ErrorMessage);
        Assert.Equal(0, batch.ActivitiesUpdated);
        Assert.Single(await db.Activities.ToListAsync());
        Assert.Single(await db.SourceFiles.ToListAsync());
    }

    [Fact]
    public async Task Restarted_archive_keeps_the_outcomes_of_an_uninterrupted_import()
    {
        await using var fixture = await ImportRecoveryFixture.CreateAsync();
        await using (var schema = await fixture.Setup.Factory.CreateDbContextAsync())
            await ProjectionVersions.EnsureAsync(schema, CancellationToken.None);
        var first = TestSupport.Gpx(name: "First");
        (string Name, string Content)[] entries =
        [
            ("activities.csv", "Activity Name,Activity Name,Filename\nUnused,Unused,activities/first.gpx"),
            ("activities/first.gpx", first),
            ("activities/corrupt.gpx", "<gpx>"),
            ("activities/swim.gpx", TestSupport.Gpx("swimming")),
            ("activities/exact.gpx", first),
            ("activities/second.gpx", TestSupport.Gpx(name: "Second").Replace("2026-01-01", "2026-01-02")),
            ("activities/readme.json", "{}")
        ];
        var restarted = await fixture.AddArchiveAsync("restarted", entries);
        for (var index = 2; index <= 5; index += 3)
        {
            using var cancellation = new CancellationTokenSource();
            var interruption = fixture.CreateProcessor(fixture.WithInterceptor(new InterruptCheckpoint(index, cancellation)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interruption.ProcessAsync(restarted, cancellation.Token));
            await using var interrupted = await fixture.Setup.Factory.CreateDbContextAsync();
            Assert.Equal(ImportStatus.Interrupted, (await interrupted.ImportBatches.FindAsync(restarted))!.Status);
            Assert.Equal(index - 1, (await interrupted.Set<ImportBatchCheckpoint>().FindAsync(restarted))!.NextEntry);
            if (index == 5)
                Assert.True(await ProjectionVersions.PendingRecords(interrupted).AnyAsync(state => state.OwnerId == fixture.Owner));
        }
        await fixture.Processor.ProcessAsync(restarted);

        var controlOwner = await fixture.Setup.SeedOwnerAsync("Control");
        var uninterrupted = await fixture.AddArchiveAsync("uninterrupted", entries, controlOwner);
        await fixture.Processor.ProcessAsync(uninterrupted);

        await using var db = await fixture.Setup.Factory.CreateDbContextAsync();
        var resumed = (await db.ImportBatches.FindAsync(restarted))!;
        var control = (await db.ImportBatches.FindAsync(uninterrupted))!;
        Assert.Equal(ImportStatus.CompletedWithWarnings, resumed.Status);
        Assert.Equal((6, 2, 1, 1, 2, 2), Counters(resumed));
        Assert.Equal(Counters(control), Counters(resumed));
        Assert.Equal(control.Summary, resumed.Summary);
        Assert.Null(resumed.ErrorMessage);
        var resumedActivities = await db.Activities.Where(x => x.OwnerId == resumed.OwnerId).OrderBy(x => x.StartTimeUtc)
            .Select(x => new { x.Title, x.NaturalFingerprint, x.MutationVersion }).ToListAsync();
        var controlActivities = await db.Activities.Where(x => x.OwnerId == controlOwner).OrderBy(x => x.StartTimeUtc)
            .Select(x => new { x.Title, x.NaturalFingerprint, x.MutationVersion }).ToListAsync();
        Assert.Equal(controlActivities, resumedActivities);
        var resumedSources = await db.SourceFiles.Where(x => x.ImportBatchId == restarted).OrderBy(x => x.OriginalName)
            .Select(x => new { x.OriginalName, x.Sha256, x.Provider }).ToListAsync();
        var controlSources = await db.SourceFiles.Where(x => x.ImportBatchId == uninterrupted).OrderBy(x => x.OriginalName)
            .Select(x => new { x.OriginalName, x.Sha256, x.Provider }).ToListAsync();
        Assert.Equal(controlSources, resumedSources);

        static (int, int, int, int, int, int) Counters(ImportBatch batch) =>
            (batch.FilesDiscovered, batch.ActivitiesCreated, batch.ActivitiesUpdated, batch.DuplicatesSkipped,
                batch.UnsupportedSkipped, batch.Warnings);
    }

    [Fact]
    public async Task An_import_keeps_committed_results_when_record_publication_needs_repair()
    {
        await using var fixture = await ImportRecoveryFixture.CreateAsync();
        await using (var schema = await fixture.Setup.Factory.CreateDbContextAsync())
            await ProjectionVersions.EnsureAsync(schema, CancellationToken.None);
        var batchId = await fixture.AddBatchAsync("repair-needed", TestSupport.Gpx());
        var processor = fixture.CreateProcessor(fixture.WithInterceptor(new FailRecordPublication()));

        await processor.ProcessAsync(batchId);

        await using (var db = await fixture.Setup.Factory.CreateDbContextAsync())
        {
            var batch = await db.ImportBatches.SingleAsync(value => value.Id == batchId);
            Assert.Equal(ImportStatus.CompletedWithWarnings, batch.Status);
            Assert.Equal(1, batch.ActivitiesCreated);
            Assert.Null(batch.ErrorMessage);
            Assert.Contains("pending repair", batch.Summary);
            Assert.True(await ProjectionVersions.PendingRecords(db).AnyAsync(state => state.OwnerId == fixture.Owner));
        }
        var statistics = new StatisticsService(fixture.Setup.Factory);
        using var worker = new StatisticsRepairWorker(fixture.Setup.Factory, statistics, NullLogger<StatisticsRepairWorker>.Instance);
        await worker.RunOnceAsync();
        Assert.NotEmpty(await statistics.GetRecordsAsync(fixture.Owner));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(40)]
    public async Task Archive_persistence_tracks_one_recording_at_a_time(int count)
    {
        await using var fixture = await ImportRecoveryFixture.CreateAsync();
        var entries = Enumerable.Range(0, count).Select(index => ($"activity-{index}.gpx",
            TestSupport.Gpx(name: $"Recording {index}").Replace("2026-01-01",
                new DateOnly(2026, 1, 1).AddDays(index).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
        var batch = await fixture.AddArchiveAsync("many-recordings", entries);
        var observer = new TrackCandidateGraph();
        await fixture.CreateProcessor(fixture.WithInterceptor(observer)).ProcessAsync(batch);

        await using var db = await fixture.Setup.Factory.CreateDbContextAsync();
        Assert.Equal(count, (await db.ImportBatches.FindAsync(batch))!.ActivitiesCreated);
        Assert.Equal(1, observer.PeakActivities);
    }

    [Fact]
    public async Task Concurrent_archive_visits_report_independent_diagnostics_and_clean_their_scratch()
    {
        await using var fixture = await ImportRecoveryFixture.CreateAsync();
        var directory = ManagedStagingDirectory.Create(fixture.Paths.StagingPath);
        var regular = Path.Combine(directory, "regular.zip");
        var corrupt = Path.Combine(directory, "corrupt.zip");
        WriteArchive(regular, [("good.gpx", TestSupport.Gpx())]);
        WriteArchive(corrupt, [("bad.gpx", "<gpx>")]);
        var importer = new ArchiveActivityImporter(fixture.Paths, new FitActivityImporter(), new XmlActivityImporter());
        var visited = 0;
        var outcomes = await Task.WhenAll(
            importer.VisitAsync(regular, SourceKind.StravaArchive, _ => visited++),
            importer.VisitAsync(corrupt, SourceKind.StravaArchive, _ => throw new InvalidOperationException("Corrupt recording was accepted.")));

        Assert.Equal(1, visited);
        Assert.Equal(new ImporterDiagnostics(0, 0, null), outcomes[0]);
        Assert.Equal(1, outcomes[1].Warnings);
        Assert.Empty(Directory.EnumerateDirectories(directory));
    }

    [Fact]
    public async Task Watched_archives_keep_durable_outer_identity_and_isolate_unreadable_files()
    {
        await using var fixture = await ImportRecoveryFixture.CreateAsync();
        var folder = Path.Combine(fixture.Setup.DataDirectory, "watched");
        Directory.CreateDirectory(folder);
        var regular = Path.Combine(folder, "regular.zip");
        var noRecordings = Path.Combine(folder, "no-recordings.zip");
        var compressed = Path.Combine(folder, "compressed.gpx.gz");
        var unreadable = Path.Combine(folder, "unreadable.gpx");
        var rejected = Path.Combine(folder, "rejected.zip");
        WriteArchive(regular, [("first.gpx", TestSupport.Gpx())]);
        WriteArchive(noRecordings, [("note.txt", "No recordings")]);
        await using (var output = File.Create(compressed))
        await using (var gzip = new GZipStream(output, CompressionMode.Compress))
        await using (var writer = new StreamWriter(gzip))
            await writer.WriteAsync(TestSupport.Gpx(name: "Compressed"));
        await File.WriteAllTextAsync(unreadable, TestSupport.Gpx("walking"));
        await File.WriteAllTextAsync(rejected, "not a zip");
        foreach (var path in Directory.EnumerateFiles(folder)) MakeOldEnough(path);

        using var mutations = new OwnerMutationLock();
        var queue = new ImportQueue(fixture.Setup.Factory, fixture.Paths, mutations);
        var service = new WatchedFolderService(fixture.Setup.Factory, queue, fixture.Paths, mutations);
        await service.AddAsync(fixture.Owner, folder);
        await using (File.Open(unreadable, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Task.WhenAll(service.ReconcileAsync(), service.ReconcileAsync());

        await using (var db = await fixture.Setup.Factory.CreateDbContextAsync())
        {
            var initial = await db.Set<WatchedImportAdmission>().AsNoTracking().Include(x => x.Batch).ToListAsync();
            Assert.Equal(4, initial.Count);
            Assert.DoesNotContain(initial, x => x.Batch.DisplayName == Path.GetFileName(unreadable));
            foreach (var admission in initial)
            {
                Assert.Equal(admission.Sha256, await Fingerprint.Sha256Async(queue.ResolveStagedPath(admission.Batch.StagedPath), default));
                await fixture.Processor.ProcessAsync(admission.BatchId);
            }
        }

        var copy = Path.Combine(folder, "copied.zip");
        File.Copy(regular, copy);
        MakeOldEnough(copy);
        var restartedQueue = new ImportQueue(fixture.Setup.Factory, fixture.Paths, mutations);
        var restarted = new WatchedFolderService(fixture.Setup.Factory, restartedQueue, fixture.Paths, mutations);
        await restarted.ReconcileAsync();
        await restarted.ReconcileAsync();
        await using (var db = await fixture.Setup.Factory.CreateDbContextAsync())
        {
            Assert.Equal(5, await db.ImportBatches.CountAsync());
            Assert.True(await db.Set<WatchedImportAdmission>().AnyAsync(x => x.Rejected && x.Batch.DisplayName == "rejected.zip"));
            Assert.Equal(ImportStatus.CompletedWithWarnings, (await db.ImportBatches.SingleAsync(x => x.DisplayName == "no-recordings.zip")).Status);
        }

        File.Delete(regular);
        WriteArchive(regular, [("first.gpx", TestSupport.Gpx(name: "Modified content"))]);
        MakeOldEnough(regular);
        await restarted.ReconcileAsync();
        var manualDirectory = ManagedStagingDirectory.Create(fixture.Paths.StagingPath);
        var manualSource = Path.Combine(manualDirectory, "regular.zip");
        File.Copy(regular, manualSource);
        await restartedQueue.EnqueueAsync(new ImportRequest(fixture.Owner, manualSource, "manual.zip", SourceKind.StravaArchive));
        await using var verification = await fixture.Setup.Factory.CreateDbContextAsync();
        Assert.Equal(7, await verification.ImportBatches.CountAsync());
        Assert.Equal(2, await verification.ImportBatches.CountAsync(x => x.DisplayName == "regular.zip"));
        Assert.Equal(6, await verification.Set<WatchedImportAdmission>().CountAsync());
    }

    [Fact]
    public async Task Correcting_the_gzip_inner_suffix_allows_the_same_bytes_to_be_imported()
    {
        await using var fixture = await ImportRecoveryFixture.CreateAsync();
        var folder = Path.Combine(fixture.Setup.DataDirectory, "watched");
        Directory.CreateDirectory(folder);
        var original = Path.Combine(folder, "recording.gz");
        await using (var output = File.Create(original))
        await using (var gzip = new GZipStream(output, CompressionMode.Compress))
        await using (var writer = new StreamWriter(gzip))
            await writer.WriteAsync(TestSupport.Gpx());
        MakeOldEnough(original);
        using var mutations = new OwnerMutationLock();
        var queue = new ImportQueue(fixture.Setup.Factory, fixture.Paths, mutations);
        var service = new WatchedFolderService(fixture.Setup.Factory, queue, fixture.Paths, mutations);
        await service.AddAsync(fixture.Owner, folder);
        await service.ReconcileAsync();
        await using var db = await fixture.Setup.Factory.CreateDbContextAsync();
        var first = await db.ImportBatches.AsNoTracking().SingleAsync();
        await fixture.Processor.ProcessAsync(first.Id);
        Assert.Empty(await db.Activities.ToListAsync());

        File.Move(original, Path.Combine(folder, "recording.gpx.gz"));
        await service.ReconcileAsync();
        var corrected = await db.ImportBatches.AsNoTracking().SingleAsync(x => x.Id != first.Id);
        await fixture.Processor.ProcessAsync(corrected.Id);
        Assert.Single(await db.Activities.ToListAsync());
        var inputs = await db.Set<WatchedImportAdmission>().AsNoTracking().ToListAsync();
        Assert.Single(inputs.Select(x => x.Sha256).Distinct());
        string[] expectedFormats = [".gpx.gz", ".gz"];
        Assert.Equal(expectedFormats, inputs.Select(x => x.Format).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Lost_watched_staging_can_be_admitted_again_without_losing_batch_history()
    {
        await using var fixture = await ImportRecoveryFixture.CreateAsync();
        var folder = Path.Combine(fixture.Setup.DataDirectory, "watched");
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "retry.zip");
        WriteArchive(source, [("first.gpx", TestSupport.Gpx())]);
        MakeOldEnough(source);
        using var mutations = new OwnerMutationLock();
        var queue = new ImportQueue(fixture.Setup.Factory, fixture.Paths, mutations);
        var service = new WatchedFolderService(fixture.Setup.Factory, queue, fixture.Paths, mutations);
        await service.AddAsync(fixture.Owner, folder);
        await service.ReconcileAsync();
        await using var db = await fixture.Setup.Factory.CreateDbContextAsync();
        var admitted = await db.ImportBatches.AsNoTracking().SingleAsync();
        File.Delete(queue.ResolveStagedPath(admitted.StagedPath));

        var recoveredQueue = new ImportQueue(fixture.Setup.Factory, fixture.Paths, mutations);
        await recoveredQueue.RecoverAsync(default);
        await new WatchedFolderService(fixture.Setup.Factory, recoveredQueue, fixture.Paths, mutations).ReconcileAsync();
        var batches = await db.ImportBatches.AsNoTracking().ToListAsync();
        Assert.Equal(2, batches.Count);
        Assert.Equal(ImportStatus.Failed, batches.Single(x => x.Id == admitted.Id).Status);
        var retry = Assert.Single(batches, x => x.Status == ImportStatus.Queued);
        Assert.Equal(retry.Id, (await db.Set<WatchedImportAdmission>().AsNoTracking().SingleAsync()).BatchId);
    }

    private static void MakeOldEnough(string path) => File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));

    private static void WriteArchive(string path, IEnumerable<(string Name, string Content)> entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }
    }

    private sealed class InterruptCheckpoint(int nextEntry, CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ImportBatchCheckpoint>()
                .Any(x => x.State == EntityState.Modified && x.Entity.NextEntry == nextEntry))
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TrackCandidateGraph : SaveChangesInterceptor
    {
        public int PeakActivities { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            PeakActivities = Math.Max(PeakActivities, eventData.Context!.ChangeTracker.Entries<Activity>().Count());
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailCandidateSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<SourceFile>()
                .Any(entry => entry.State is EntityState.Added or EntityState.Modified))
                throw new DbUpdateConcurrencyException("Synthetic candidate save failure");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailRecordPublication : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<StatisticSnapshot>().Any(entry => entry.State == EntityState.Added))
                throw new DbUpdateException("Synthetic record publication failure");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ImportRecoveryFixture : IAsyncDisposable
    {
        private readonly string? _previousDataRoot;
        private readonly AppDataPaths _paths;
        private readonly Guid _owner;
        public DatabaseSetup Setup { get; }
        public OriginalStore Originals { get; }
        public ImportProcessor Processor { get; }
        public AppDataPaths Paths => _paths;
        public Guid Owner => _owner;

        private ImportRecoveryFixture(DatabaseSetup setup, string? previousDataRoot, AppDataPaths paths, Guid owner)
        {
            Setup = setup;
            _previousDataRoot = previousDataRoot;
            _paths = paths;
            _owner = owner;
            Originals = new OriginalStore(paths);
            Processor = CreateProcessor(setup.Factory);
        }

        public static async Task<ImportRecoveryFixture> CreateAsync()
        {
            var setup = await DatabaseSetup.CreateAsync();
            var previousDataRoot = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_DATA");
            Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", setup.DataDirectory);
            var paths = new AppDataPaths();
            paths.EnsureCreated();
            return new ImportRecoveryFixture(setup, previousDataRoot, paths, await setup.SeedOwnerAsync("Importer"));
        }

        public ImportProcessor CreateProcessor(IDbContextFactory<ExplorerDbContext> factory)
        {
            var mutationLock = new OwnerMutationLock();
            var fileOperations = new FileOperationCoordinator(factory, _paths, Originals, NullLogger<FileOperationCoordinator>.Instance);
            var xml = new XmlActivityImporter();
            return new ImportProcessor(factory, [new ArchiveActivityImporter(_paths, new FitActivityImporter(), xml), xml], _paths, Originals, fileOperations, mutationLock,
                new StatisticsService(factory), new SegmentService(factory, new SegmentMatcher(), mutationLock),
                NullLogger<ImportProcessor>.Instance);
        }

        public TestDbFactory WithInterceptor(SaveChangesInterceptor interceptor) => new(new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Setup.DataDirectory, "test.db")}")
            .AddInterceptors(interceptor)
            .Options);

        public Task<Guid> AddArchiveAsync(string name, IEnumerable<(string Name, string Content)> entries, Guid? ownerId = null)
        {
            var directory = ManagedStagingDirectory.Create(_paths.StagingPath);
            var path = Path.Combine(directory, name + ".zip");
            WriteArchive(path, entries);
            return AddBatchAsync(name, path, SourceKind.StravaArchive, ownerId ?? _owner);
        }

        public async Task<Guid> AddBatchAsync(string name, string contents)
        {
            var directory = Path.Combine(_paths.StagingPath, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = TestSupport.Write(directory, name + ".gpx", contents);
            return await AddBatchAsync(name, path, SourceKind.Gpx, _owner);
        }

        private async Task<Guid> AddBatchAsync(string name, string path, SourceKind source, Guid ownerId)
        {
            await using var db = await Setup.Factory.CreateDbContextAsync();
            var batch = new ImportBatch
            {
                OwnerId = ownerId,
                SourceKind = source,
                DisplayName = name,
                StagedPath = path,
                Status = ImportStatus.Queued
            };
            db.ImportBatches.Add(batch);
            await db.SaveChangesAsync();
            return batch.Id;
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", _previousDataRoot);
            if (Directory.Exists(Setup.DataDirectory)) Directory.Delete(Setup.DataDirectory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
