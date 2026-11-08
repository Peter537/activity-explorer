using System.Data.Common;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ActivityExplorer.Tests;

public sealed class DatabaseReadConcurrencyTests
{
    [Fact]
    public async Task Import_history_readers_share_a_snapshot_without_reserving_the_writer()
    {
        var directory = TestSupport.NewDirectory();
        var path = Path.Combine(directory, "concurrent-import-history.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 10
        }.ToString();
        var owner = new OwnerProfile { DisplayName = "Concurrent import reader", TimeZoneId = "UTC" };
        var batch = new ImportBatch { OwnerId = owner.Id, DisplayName = "Concurrent import.fit", Status = ImportStatus.Completed };
        var activity = new ActivityExplorer.Core.Domain.Activity { OwnerId = owner.Id, Title = "Retained activity", NaturalFingerprint = "concurrent-import" };
        var route = new Route { OwnerId = owner.Id, Name = "Retained route" };
        var source = new SourceFile
        {
            OwnerId = owner.Id,
            ImportBatchId = batch.Id,
            ActivityId = activity.Id,
            RouteId = route.Id,
            OriginalName = "concurrent-import.fit",
            Sha256 = "concurrent-import-source"
        };
        var options = new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite(connectionString).Options;
        await using (var db = new ExplorerDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL");
            db.AddRange(owner, batch, activity, route, source);
            await db.SaveChangesAsync();
        }
        var unchangedDatabase = await File.ReadAllBytesAsync(path);
        var pause = new PauseFirstImportCount();
        var factory = new Factory(new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite(connectionString).AddInterceptors(pause).Options);
        var service = new ImportHistoryService(factory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ImportHistoryPage>? first = null;
        Task<ImportHistoryPage>? second = null;
        try
        {
            first = service.ListAsync(owner.Id, cancellationToken: cancellation.Token);
            await pause.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = Task.Run(() => service.ListAsync(owner.Id, cancellationToken: cancellation.Token));
            // Readiness proves overlap while the first transaction is held, rather than comparing query timings.
            await pause.SecondRead.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted);
            pause.Release.TrySetResult();
            var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, firstResult.Total);
            Assert.Equal(firstResult.Items, secondResult.Items);
            var retained = Assert.Single(firstResult.Items);
            Assert.Equal(batch.Id, retained.Id);
            Assert.Equal(1, retained.LinkedActivityCount);
            Assert.Equal(1, retained.LinkedRouteCount);
            Assert.Equal(0, pause.Writes);
            Assert.Equal(unchangedDatabase, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            pause.Release.TrySetResult();
            try
            {
                var pending = new[] { first, second }.Where(task => task is not null).Cast<Task>();
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                await cancellation.CancelAsync();
                foreach (var file in new[] { path, path + "-wal", path + "-shm" }) File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }

    private sealed class PauseFirstImportCount : DbCommandInterceptor
    {
        private int reads;
        private int writes;
        public int Writes => Volatile.Read(ref writes);
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT COUNT(*)", StringComparison.Ordinal) &&
                command.CommandText.Contains("ImportBatches", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    FirstRead.TrySetResult();
                    await Release.Task.WaitAsync(cancellationToken);
                }
                else SecondRead.TrySetResult();
            }
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref writes);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Factory(DbContextOptions<ExplorerDbContext> options) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);
        public Task<ExplorerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
