using ActivityExplorer.Core.Contracts;
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

public sealed partial class StorageLifecycleTests
{
    [Fact]
    public async Task Enqueue_signals_a_batch_committed_when_request_is_cancelled_after_save()
    {
        await using var setup = await StorageSetup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync();
        var source = setup.Stage("source.gpx", TestSupport.Gpx());
        using var cancellation = new CancellationTokenSource();
        var options = new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={setup.Paths.DatabasePath}")
            .AddInterceptors(new CancelAfterSave(cancellation))
            .Options;
        using var ownerLock = new OwnerMutationLock();
        var queue = new ImportQueue(new TestDbFactory(options), setup.Paths, ownerLock);

        var id = await queue.EnqueueAsync(new ImportRequest(ownerId, source, "source.gpx", SourceKind.Gpx), cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var reader = queue.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(id, reader.Current);
        await using var db = await setup.Factory.CreateDbContextAsync();
        Assert.Equal(ImportStatus.Queued, (await db.ImportBatches.SingleAsync(x => x.Id == id)).Status);
        Assert.True(File.Exists(source));
    }

    private sealed class CancelAfterSave(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Failed_copy_removes_partial_target_and_can_be_retried()
    {
        await using var setup = await StorageSetup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync();
        var source = setup.Stage("source.gpx", TestSupport.Gpx());
        var hash = await Fingerprint.Sha256Async(source, CancellationToken.None);
        var target = setup.Originals.GetOriginalTarget(ownerId, hash, ".gpx");
        var coordinator = new FileOperationCoordinator(setup.Factory, setup.Paths, setup.Originals,
            NullLogger<FileOperationCoordinator>.Instance)
        {
            CopyAsync = async (input, output, token) =>
            {
                await output.WriteAsync(new byte[] { 1, 2, 3 }, token);
                throw new IOException("Simulated interrupted copy.");
            }
        };

        await Assert.ThrowsAsync<IOException>(() => coordinator.PrepareCopyAsync(ownerId, null, source, target, hash));
        Assert.False(File.Exists(target));
        var prepared = await setup.FileOperations.PrepareCopyAsync(ownerId, null, source, target, hash);
        Assert.True(File.Exists(target));
        await setup.FileOperations.RollbackAsync(prepared.OperationId);
    }

    [Fact]
    public async Task Cancelled_copy_removes_partial_target()
    {
        await using var setup = await StorageSetup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync();
        var source = setup.Stage("source.gpx", TestSupport.Gpx());
        var hash = await Fingerprint.Sha256Async(source, CancellationToken.None);
        var target = setup.Originals.GetOriginalTarget(ownerId, hash, ".gpx");
        using var cancellation = new CancellationTokenSource();
        var coordinator = new FileOperationCoordinator(setup.Factory, setup.Paths, setup.Originals,
            NullLogger<FileOperationCoordinator>.Instance)
        {
            CopyAsync = async (input, output, token) =>
            {
                await output.WriteAsync(new byte[] { 1, 2, 3 }, token);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.PrepareCopyAsync(ownerId, null, source, target, hash, cancellationToken: cancellation.Token));
        Assert.False(File.Exists(target));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Failed_partial_copy_cleanup_remains_recoverable_until_cleanup_succeeds()
    {
        await using var setup = await StorageSetup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync();
        var source = setup.Stage("source.gpx", TestSupport.Gpx());
        var hash = await Fingerprint.Sha256Async(source, CancellationToken.None);
        var target = setup.Originals.GetOriginalTarget(ownerId, hash, ".gpx");
        var coordinator = new FileOperationCoordinator(setup.Factory, setup.Paths, setup.Originals,
            NullLogger<FileOperationCoordinator>.Instance)
        {
            CopyAsync = async (input, output, token) =>
            {
                await output.WriteAsync(new byte[] { 1, 2, 3 }, token);
                throw new IOException("Simulated interrupted copy.");
            },
            DeleteFile = _ => throw new IOException("Simulated cleanup failure.")
        };

        await Assert.ThrowsAsync<IOException>(() => coordinator.PrepareCopyAsync(ownerId, null, source, target, hash));
        Assert.True(File.Exists(target));
        await coordinator.RecoverAsync();
        await using (var db = await setup.Factory.CreateDbContextAsync())
            Assert.Equal(FileOperationState.Pending, (await db.FileOperations.SingleAsync()).State);

        using var cancellation = new CancellationTokenSource();
        var cancelledRecovery = new FileOperationCoordinator(setup.Factory, setup.Paths, setup.Originals,
            NullLogger<FileOperationCoordinator>.Instance)
        {
            DeleteFile = _ => { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRecovery.RecoverAsync(cancellation.Token));
        await using (var db = await setup.Factory.CreateDbContextAsync())
            Assert.Equal(FileOperationState.Pending, (await db.FileOperations.SingleAsync()).State);

        await setup.FileOperations.RecoverAsync();
        Assert.False(File.Exists(target));
        await using (var db = await setup.Factory.CreateDbContextAsync())
            Assert.Equal(FileOperationState.RolledBack, (await db.FileOperations.SingleAsync()).State);
        var prepared = await setup.FileOperations.PrepareCopyAsync(ownerId, null, source, target, hash);
        await setup.FileOperations.RollbackAsync(prepared.OperationId);
    }

    [Fact]
    public async Task Copy_hash_failure_keeps_preexisting_referenced_target_and_failed_journal()
    {
        await using var setup = await StorageSetup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync();
        var source = setup.Stage("source.gpx", TestSupport.Gpx());
        var hash = await Fingerprint.Sha256Async(source, CancellationToken.None);
        var target = setup.Originals.GetOriginalTarget(ownerId, hash, ".gpx");
        await File.WriteAllTextAsync(target, "existing original");
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var batch = Batch(ownerId, ImportStatus.Completed, string.Empty);
            db.SourceFiles.Add(new SourceFile
            {
                OwnerId = ownerId,
                ImportBatch = batch,
                SourceKind = SourceKind.Gpx,
                OriginalName = "original.gpx",
                StoredPath = setup.Originals.ToStoredPath(target),
                Sha256 = hash,
                Length = 17
            });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            setup.FileOperations.PrepareCopyAsync(ownerId, null, source, target, hash));
        await setup.FileOperations.RecoverAsync();
        Assert.Equal("existing original", await File.ReadAllTextAsync(target));
        await using var verification = await setup.Factory.CreateDbContextAsync();
        Assert.Equal(FileOperationState.Failed, (await verification.FileOperations.SingleAsync()).State);
    }

    [Fact]
    public void Managed_path_guard_rejects_traversal_and_outside_rooted_paths()
    {
        var root = TestSupport.NewDirectory();
        Assert.Throws<InvalidDataException>(() => ManagedPathGuard.ResolveUnder(root, "../outside.fit"));
        Assert.Throws<InvalidDataException>(() => ManagedPathGuard.ResolveUnder(root, Path.GetTempPath()));
    }

    [Fact]
    public async Task Recovery_rolls_back_an_unreferenced_prepared_copy()
    {
        await using var setup = await StorageSetup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync();
        var source = setup.Stage("source.gpx", TestSupport.Gpx());
        var hash = await Fingerprint.Sha256Async(source, CancellationToken.None);
        var target = setup.Originals.GetOriginalTarget(ownerId, hash, ".gpx");

        var prepared = await setup.FileOperations.PrepareCopyAsync(ownerId, null, source, target, hash);
        Assert.True(File.Exists(target));

        await setup.FileOperations.RecoverAsync();

        await using var db = await setup.Factory.CreateDbContextAsync();
        Assert.Equal(FileOperationState.RolledBack,
            (await db.FileOperations.SingleAsync(x => x.Id == prepared.OperationId)).State);
        Assert.False(File.Exists(target));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Recovery_commits_a_referenced_copy_and_deletes_requested_source()
    {
        await using var setup = await StorageSetup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync();
        var source = setup.Stage("source.gpx", TestSupport.Gpx());
        var hash = await Fingerprint.Sha256Async(source, CancellationToken.None);
        var target = setup.Originals.GetOriginalTarget(ownerId, hash, ".gpx");
        var prepared = await setup.FileOperations.PrepareCopyAsync(
            ownerId, null, source, target, hash, deleteSourceOnCommit: true);

        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var batch = new ImportBatch
            {
                OwnerId = ownerId,
                SourceKind = SourceKind.Gpx,
                Status = ImportStatus.Completed,
                DisplayName = "Recovered",
                StagedPath = string.Empty
            };
            db.ImportBatches.Add(batch);
            db.SourceFiles.Add(new SourceFile
            {
                OwnerId = ownerId,
                ImportBatchId = batch.Id,
                SourceKind = SourceKind.Gpx,
                Provider = SourceProvider.Unknown,
                OriginalName = "source.gpx",
                StoredPath = prepared.TargetRelativePath,
                Sha256 = hash,
                Length = new FileInfo(target).Length
            });
            await db.SaveChangesAsync();
        }

        await setup.FileOperations.RecoverAsync();

        await using var verification = await setup.Factory.CreateDbContextAsync();
        Assert.Equal(FileOperationState.Completed,
            (await verification.FileOperations.SingleAsync(x => x.Id == prepared.OperationId)).State);
        Assert.True(File.Exists(target));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task Recovery_restores_referenced_file_quarantine_and_deletes_unreferenced_quarantine()
    {
        await using var setup = await StorageSetup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync();
        var bytes = System.Text.Encoding.UTF8.GetBytes("managed original");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var original = setup.Originals.GetOriginalTarget(ownerId, hash, ".fit");
        await File.WriteAllBytesAsync(original, bytes);
        Guid sourceId;
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var batch = Batch(ownerId, ImportStatus.Completed, string.Empty);
            var source = new SourceFile
            {
                OwnerId = ownerId,
                ImportBatch = batch,
                SourceKind = SourceKind.Fit,
                Provider = SourceProvider.Garmin,
                OriginalName = "source.fit",
                StoredPath = setup.Originals.ToStoredPath(original),
                Sha256 = hash,
                Length = bytes.Length
            };
            db.Add(source);
            await db.SaveChangesAsync();
            sourceId = source.Id;
        }

        var referencedOperation = await setup.FileOperations.QuarantineFileAsync(
            ownerId, sourceId, setup.Originals.ToStoredPath(original));
        Assert.NotNull(referencedOperation);
        Assert.False(File.Exists(original));
        await setup.FileOperations.RecoverAsync();
        Assert.True(File.Exists(original));

        var unreferencedOperation = await setup.FileOperations.QuarantineFileAsync(
            ownerId, sourceId, setup.Originals.ToStoredPath(original));
        Assert.NotNull(unreferencedOperation);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.SourceFiles.Remove(await db.SourceFiles.SingleAsync(x => x.Id == sourceId));
            await db.SaveChangesAsync();
        }
        await setup.FileOperations.RecoverAsync();

        await using var verification = await setup.Factory.CreateDbContextAsync();
        Assert.Equal(FileOperationState.RolledBack,
            (await verification.FileOperations.SingleAsync(x => x.Id == referencedOperation)).State);
        Assert.Equal(FileOperationState.Completed,
            (await verification.FileOperations.SingleAsync(x => x.Id == unreferencedOperation)).State);
        Assert.False(File.Exists(original));
    }

    [Fact]
    public async Task Startup_requeues_existing_staging_and_fails_missing_staging()
    {
        await using var setup = await StorageSetup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync();
        var queuedPath = setup.Stage("queued.gpx", TestSupport.Gpx());
        var interruptedPath = setup.Stage("interrupted.gpx", TestSupport.Gpx());
        Guid queuedId;
        Guid interruptedId;
        Guid missingId;
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var queued = Batch(ownerId, ImportStatus.Queued, setup.Relative(queuedPath));
            var interrupted = Batch(ownerId, ImportStatus.Interrupted, setup.Relative(interruptedPath));
            var missing = Batch(ownerId, ImportStatus.Interrupted, setup.Relative(Path.Combine(setup.Paths.StagingPath, "missing.gpx")));
            db.ImportBatches.AddRange(queued, interrupted, missing);
            await db.SaveChangesAsync();
            queuedId = queued.Id;
            interruptedId = interrupted.Id;
            missingId = missing.Id;
        }

        var queue = new ImportQueue(setup.Factory, setup.Paths, new OwnerMutationLock());
        await queue.RecoverAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var recovered = new HashSet<Guid>();
        await using (var reader = queue.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token))
        {
            while (recovered.Count < 2 && await reader.MoveNextAsync()) recovered.Add(reader.Current);
        }

        Assert.Equal(new HashSet<Guid> { queuedId, interruptedId }, recovered);
        await using var verification = await setup.Factory.CreateDbContextAsync();
        var missingResult = await verification.ImportBatches.SingleAsync(x => x.Id == missingId);
        Assert.Equal(ImportStatus.Failed, missingResult.Status);
        Assert.NotNull(missingResult.CompletedAtUtc);
        Assert.Contains("no longer available", missingResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Staging_recovery_reclaims_owned_orphans_and_preserves_recovery_inputs()
    {
        await using var setup = await StorageSetup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync();
        var root = setup.Paths.StagingPath;
        var abandoned = ManagedStagingDirectory.Create(root);
        TestSupport.Write(Directory.CreateDirectory(Path.Combine(abandoned, "expanded")).FullName, "member.gpx", "partial");
        var completed = ManagedStagingDirectory.Create(root);
        var completedFile = TestSupport.Write(completed, "complete.gpx", "consumed");
        var queued = ManagedStagingDirectory.Create(root);
        var queuedFile = TestSupport.Write(queued, "queued.gpx", "retry");
        var interrupted = ManagedStagingDirectory.Create(root);
        var interruptedFile = TestSupport.Write(interrupted, "interrupted.gpx", "retry");
        var journalSource = ManagedStagingDirectory.Create(root);
        var sourceFile = TestSupport.Write(journalSource, "copy-source.gpx", "protected");
        var journalTarget = ManagedStagingDirectory.Create(root);
        var targetFile = TestSupport.Write(journalTarget, "copy-target.gpx", "protected");
        var unknown = setup.Stage("unknown.gpx", "legacy");

        await using var db = await setup.Factory.CreateDbContextAsync();
        db.ImportBatches.AddRange(
            Batch(ownerId, ImportStatus.Completed, setup.Relative(completedFile)),
            Batch(ownerId, ImportStatus.Queued, setup.Relative(queuedFile)),
            Batch(ownerId, ImportStatus.Interrupted, interruptedFile));
        var journal = new FileOperationJournal
        {
            Kind = FileOperationKind.Copy,
            State = FileOperationState.DatabaseCommitted,
            SourceRelativePath = setup.Relative(sourceFile),
            TargetRelativePath = setup.Relative(targetFile)
        };
        db.FileOperations.Add(journal);
        await db.SaveChangesAsync();

        await StagingRecovery.ReconcileAsync(db, setup.Paths, NullLogger.Instance, CancellationToken.None);
        Assert.False(Directory.Exists(abandoned));
        Assert.False(Directory.Exists(completed));
        foreach (var file in new[] { queuedFile, interruptedFile, sourceFile, targetFile, unknown })
            Assert.True(File.Exists(file), file);

        journal.State = FileOperationState.Completed;
        await db.SaveChangesAsync();
        await StagingRecovery.ReconcileAsync(db, setup.Paths, NullLogger.Instance, CancellationToken.None);
        Assert.False(Directory.Exists(journalSource));
        Assert.False(Directory.Exists(journalTarget));
        Assert.True(File.Exists(unknown));
    }

    [Fact]
    public async Task Staging_recovery_leaves_an_invalid_marker_and_unknown_root_contents_untouched()
    {
        await using var setup = await StorageSetup.CreateAsync();
        var root = setup.Paths.StagingPath;
        var invalidMarker = ManagedStagingDirectory.Create(root);
        var marker = Assert.Single(Directory.GetFiles(invalidMarker));
        File.WriteAllText(marker, Guid.NewGuid().ToString("N"));
        var retained = TestSupport.Write(invalidMarker, "retained.txt", "keep");
        var unmanaged = Directory.CreateDirectory(Path.Combine(root, "third-party")).FullName;
        var nested = ManagedStagingDirectory.Create(unmanaged);

        await using var db = await setup.Factory.CreateDbContextAsync();
        await StagingRecovery.ReconcileAsync(db, setup.Paths, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("keep", File.ReadAllText(retained));
        Assert.True(Directory.Exists(nested));
        Assert.Throws<InvalidDataException>(() => ManagedStagingDirectory.DeleteOwned(root, unmanaged));
        Assert.Throws<InvalidDataException>(() => ManagedStagingDirectory.DeleteOwned(root, nested));
    }

    [Fact]
    public async Task Staging_recovery_retries_when_a_file_cannot_be_deleted_during_the_first_pass()
    {
        TestSupport.RequireWindows();
        await using var setup = await StorageSetup.CreateAsync();
        var staged = ManagedStagingDirectory.Create(setup.Paths.StagingPath);
        var file = TestSupport.Write(staged, "locked.gpx", "retained while in use");
        await using var db = await setup.Factory.CreateDbContextAsync();
        await using (var handle = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await StagingRecovery.ReconcileAsync(db, setup.Paths, NullLogger.Instance, CancellationToken.None);
            Assert.True(File.Exists(file));
        }
        await StagingRecovery.ReconcileAsync(db, setup.Paths, NullLogger.Instance, CancellationToken.None);
        Assert.False(Directory.Exists(staged));
    }

    [Fact]
    public async Task Watched_folder_removes_staging_when_queueing_fails()
    {
        await using var setup = await StorageSetup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync();
        var watchedDirectory = Path.Combine(setup.Root, "watched");
        Directory.CreateDirectory(watchedDirectory);
        var source = TestSupport.Write(watchedDirectory, "activity.gpx", TestSupport.Gpx());
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        var service = new WatchedFolderService(setup.Factory, new ThrowingImportQueue(), setup.Paths);
        await service.AddAsync(ownerId, watchedDirectory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReconcileAsync());

        Assert.Empty(Directory.EnumerateFileSystemEntries(setup.Paths.StagingPath));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Cancellation_marks_import_interrupted_and_retains_staging()
    {
        await using var setup = await StorageSetup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync();
        var source = setup.Stage("cancel.gpx", TestSupport.Gpx());
        var batch = Batch(ownerId, ImportStatus.Queued, setup.Relative(source));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.ImportBatches.Add(batch);
            await db.SaveChangesAsync();
        }

        using var cancellation = new CancellationTokenSource();
        var processor = new ImportProcessor(
            setup.Factory,
            [new CancellingImporter(cancellation)],
            setup.Paths,
            setup.Originals,
            setup.FileOperations,
            new OwnerMutationLock(),
            new StatisticsService(setup.Factory),
            new SegmentService(setup.Factory, new SegmentMatcher(), new OwnerMutationLock()),
            NullLogger<ImportProcessor>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessAsync(batch.Id, cancellation.Token));

        await using var verification = await setup.Factory.CreateDbContextAsync();
        var result = await verification.ImportBatches.SingleAsync(x => x.Id == batch.Id);
        Assert.Equal(ImportStatus.Interrupted, result.Status);
        Assert.Null(result.CompletedAtUtc);
        Assert.True(File.Exists(source));
    }

    private static ImportBatch Batch(Guid ownerId, ImportStatus status, string stagedPath) => new()
    {
        OwnerId = ownerId,
        SourceKind = SourceKind.Gpx,
        Status = status,
        DisplayName = status.ToString(),
        StagedPath = stagedPath
    };

    private sealed class ThrowingImportQueue : IImportQueue
    {
        public ValueTask<Guid> EnqueueAsync(ImportRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Synthetic queue failure.");

        public ValueTask<bool> EnqueueWatchedAsync(Guid ownerId, string stagedPath, string displayName,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Synthetic queue failure.");

        public IAsyncEnumerable<ImportProgress> WatchAsync(
            Guid importId,
            CancellationToken cancellationToken = default) => Empty();

        private static async IAsyncEnumerable<ImportProgress> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CancellingImporter(CancellationTokenSource cancellation) : IActivityImporter
    {
        public string Name => "Cancellation test";
        public bool CanImport(string path) => true;

        public Task<IReadOnlyList<ImportCandidate>> ReadAsync(
            string path,
            SourceKind sourceKind,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ImportCandidate>>([]);
        }
    }

    private sealed class StorageSetup : IAsyncDisposable
    {
        private readonly string? _previousDataRoot;

        private StorageSetup(string root, string? previousDataRoot, AppDataPaths paths, TestDbFactory factory)
        {
            Root = root;
            _previousDataRoot = previousDataRoot;
            Paths = paths;
            Factory = factory;
            Originals = new OriginalStore(paths);
            FileOperations = new FileOperationCoordinator(
                factory, paths, Originals, NullLogger<FileOperationCoordinator>.Instance);
        }

        public string Root { get; }
        public AppDataPaths Paths { get; }
        public TestDbFactory Factory { get; }
        public OriginalStore Originals { get; }
        public FileOperationCoordinator FileOperations { get; }

        public static async Task<StorageSetup> CreateAsync()
        {
            var root = TestSupport.NewDirectory();
            var previous = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_DATA");
            Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", root);
            var paths = new AppDataPaths();
            paths.EnsureCreated();
            var options = new DbContextOptionsBuilder<ExplorerDbContext>()
                .UseSqlite($"Data Source={paths.DatabasePath}")
                .Options;
            var factory = new TestDbFactory(options);
            await using var db = await factory.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            return new StorageSetup(root, previous, paths, factory);
        }

        public async Task<Guid> AddOwnerAsync()
        {
            await using var db = await Factory.CreateDbContextAsync();
            var owner = new OwnerProfile { DisplayName = Guid.NewGuid().ToString("N") };
            db.Owners.Add(owner);
            await db.SaveChangesAsync();
            return owner.Id;
        }

        public string Stage(string fileName, string content)
        {
            var directory = Path.Combine(Paths.StagingPath, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return TestSupport.Write(directory, fileName, content);
        }

        public string Relative(string path) => ManagedPathGuard.RelativeTo(Paths.Root, path);

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", _previousDataRoot);
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestDbFactory(DbContextOptions<ExplorerDbContext> options) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);
        public Task<ExplorerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExplorerDbContext(options));
    }
}
