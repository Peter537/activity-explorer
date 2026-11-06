using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
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

    private sealed class ImportRecoveryFixture : IAsyncDisposable
    {
        private readonly string? _previousDataRoot;
        private readonly AppDataPaths _paths;
        private readonly Guid _owner;
        public DatabaseSetup Setup { get; }
        public OriginalStore Originals { get; }
        public ImportProcessor Processor { get; }

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
            return new ImportProcessor(factory, [new XmlActivityImporter()], _paths, Originals, fileOperations, mutationLock,
                new StatisticsService(factory), new SegmentService(factory, new SegmentMatcher(), mutationLock),
                NullLogger<ImportProcessor>.Instance);
        }

        public async Task<Guid> AddBatchAsync(string name, string contents)
        {
            var directory = Path.Combine(_paths.StagingPath, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = TestSupport.Write(directory, name + ".gpx", contents);
            await using var db = await Setup.Factory.CreateDbContextAsync();
            var batch = new ImportBatch
            {
                OwnerId = _owner,
                SourceKind = SourceKind.Gpx,
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
