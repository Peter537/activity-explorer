using System.Data.Common;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Theory]
    [InlineData("Activities")]
    [InlineData("Segments")]
    public async Task Detail_reads_keep_one_stream_revision_while_another_connection_writes(string table)
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Detail snapshot profile");
        var initial = TestSupport.Track(60);
        var activity = await setup.SeedActivityAsync(owner, "Original title", SportKind.Cycling, initial);
        await using (var db = await setup.Factory.CreateDbContextAsync())
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL");
        var storage = CreateStorageServices(setup);
        var segmentId = await new SegmentService(setup.Factory, new WholeStreamSegmentMatcher(), storage.OwnerLock)
            .CreateAsync(new(owner, "Snapshot segment", SportKind.Cycling, initial, 30));

        var pause = new PauseDetailHeader(table);
        var readerFactory = new TestDbFactory(new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(setup.DataDirectory, "test.db")};Default Timeout=5")
            .AddInterceptors(pause).Options);
        var segments = new SegmentService(readerFactory, new WholeStreamSegmentMatcher(), storage.OwnerLock);
        var activities = new ActivityQueryService(readerFactory, new StatisticsService(readerFactory), segments,
            storage.Originals, storage.FileOperations, storage.OwnerLock, NullLogger<ActivityQueryService>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var read = ReadAsync();
        try
        {
            await pause.Entered.Task.WaitAsync(deadline.Token);
            await using var db = await setup.Factory.CreateDbContextAsync(deadline.Token);
            var newState = await db.Activities.Include(x => x.Stream).SingleAsync(x => x.Id == activity, deadline.Token);
            newState.Title = "Updated title";
            var replacement = initial.Select(point => point with { Latitude = 35 }).ToArray();
            newState.Stream!.CompressedPayload = TrackCodec.Encode(replacement);
            await db.SaveChangesAsync(deadline.Token);
            Assert.False(read.IsCompleted);
        }
        finally
        {
            pause.Release.TrySetResult();
            await read;
        }

        async Task ReadAsync()
        {
            if (table == "Activities")
            {
                var detail = await activities.GetAsync(activity, deadline.Token);
                Assert.NotNull(detail);
                Assert.Equal("Original title", detail.Summary.Title);
                Assert.Equal(initial[0].Latitude, detail.Points[0].Latitude);
            }
            else
            {
                var detail = await segments.GetAsync(segmentId, cancellationToken: deadline.Token);
                Assert.NotNull(detail);
                Assert.NotEmpty(detail.SelectedEffortPoints);
                Assert.Equal(initial[0].Latitude, detail.SelectedEffortPoints[0].Latitude);
            }
        }
    }

    private sealed class PauseDetailHeader(string table) : DbCommandInterceptor
    {
        private int _reads;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains($"FROM \"{table}\"", StringComparison.Ordinal) && Interlocked.Increment(ref _reads) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
