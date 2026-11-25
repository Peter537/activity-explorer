using System.Collections.Concurrent;
using System.Data.Common;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using ActivityExplorer.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ActivityExplorer.Tests;

public sealed partial class McpWriteTests
{
    [Fact]
    public async Task Library_revision_tracks_committed_cross_profile_writes_and_ignores_passive_scan_updates()
    {
        await using var setup = await Setup.CreateAsync();
        var owner = setup.Fixture.Owner;
        var otherOwner = setup.Fixture.OtherOwner;
        await using var db = await setup.Fixture.Factory.CreateDbContextAsync();
        var folder = new WatchedFolder { OwnerId = otherOwner, Path = setup.Fixture.DirectoryPath };
        db.WatchedFolders.Add(folder);
        await db.SaveChangesAsync();

        async Task<McpChangePreview> Prepare(string name) => await setup.Writes.PrepareAsync(Request("create_tag", owner, new { name }), CancellationToken.None);
        var accepted = await Prepare("Watcher did not change data");
        var initial = await McpLibraryRevision.ReadAsync(db, CancellationToken.None);
        await db.WatchedFolders.Where(value => value.Id == folder.Id).ExecuteUpdateAsync(update =>
            update.SetProperty(value => value.LastScanAtUtc, DateTimeOffset.UtcNow));
        Assert.Equal(initial, await McpLibraryRevision.ReadAsync(db, CancellationToken.None));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE Owners SET DisplayName = 'Rolled back' WHERE Id = {otherOwner}");
            Assert.True(await McpLibraryRevision.ReadAsync(db, CancellationToken.None) > initial);
            await transaction.RollbackAsync();
        }
        Assert.Equal(initial, await McpLibraryRevision.ReadAsync(db, CancellationToken.None));
        await setup.Writes.ApproveAsync(accepted.PlanId, null, CancellationToken.None);
        Assert.Equal("completed", (await WaitAsync(setup.Writes, accepted.PlanId)).Status);

        var rejected = await Prepare("Change and restore still invalidate");
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE Owners SET DisplayName = 'Changed' WHERE Id = {otherOwner}");
            await db.Database.ExecuteSqlAsync($"UPDATE Owners SET DisplayName = 'Other athlete' WHERE Id = {otherOwner}");
            await transaction.CommitAsync();
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Writes.ApproveAsync(rejected.PlanId, null, CancellationToken.None));
        Assert.Contains("changed after preview", error.Message, StringComparison.Ordinal);
        Assert.False(await db.McpOperations.AnyAsync(operation => operation.Id == rejected.PlanId));

        var watchChanged = await Prepare("Changing watch configuration invalidates");
        await db.WatchedFolders.Where(value => value.Id == folder.Id).ExecuteUpdateAsync(update => update.SetProperty(value => value.Enabled, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Writes.ApproveAsync(watchChanged.PlanId, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_queued_receipt_rejects_a_later_library_change_at_execution()
    {
        await using var setup = await Setup.CreateAsync(configure: builder => builder.ConfigureServices(services =>
        {
            var worker = services.Single(item => item.ServiceType == typeof(IHostedService) && item.ImplementationType == typeof(McpOperationWorker));
            services.Remove(worker);
        }));
        var plan = await setup.Writes.PrepareAsync(Request("create_tag", setup.Fixture.Owner, new { name = "Must not apply" }), CancellationToken.None);
        Assert.Equal("queued", (await setup.Writes.ApproveAsync(plan.PlanId, null, CancellationToken.None)).Status);

        await using var db = await setup.Fixture.Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlAsync($"UPDATE Owners SET DisplayName = 'New name' WHERE Id = {setup.Fixture.OtherOwner}");
        await setup.Writes.ExecuteAsync(plan.PlanId, CancellationToken.None);

        var status = await setup.Writes.ApproveAsync(plan.PlanId, null, CancellationToken.None);
        Assert.Equal("failed", status.Status);
        Assert.Contains("changed while this operation was queued", status.Message, StringComparison.Ordinal);
        Assert.False(await db.Tags.AnyAsync(tag => tag.Name == "Must not apply"));
        Assert.Empty(await db.McpEffects.ToArrayAsync());
    }

    [Fact]
    public async Task Small_MCP_change_checks_scalar_revision_in_a_library_with_dense_paths()
    {
        var probe = new RevisionCommandProbe();
        await using var setup = await Setup.CreateAsync(configure: builder => builder.ConfigureServices(services =>
        {
            var repair = services.Single(item => item.ServiceType == typeof(IHostedService) && item.ImplementationType == typeof(StatisticsRepairWorker));
            services.Remove(repair);
            services.RemoveAll<IDbContextFactory<ExplorerDbContext>>();
            services.AddSingleton<IDbContextFactory<ExplorerDbContext>>(provider =>
            {
                var paths = provider.GetRequiredService<AppDataPaths>();
                return new RevisionContextFactory(new DbContextOptionsBuilder<ExplorerDbContext>()
                    .UseSqlite($"Data Source={paths.DatabasePath};Pooling=False").AddInterceptors(probe).Options);
            });
        }));
        await using (var db = await setup.Fixture.Factory.CreateDbContextAsync())
        {
            var dense = GeometryCodec.ToWkb(TestSupport.Track(4096))!;
            const int routes = 64;
            db.Routes.AddRange(Enumerable.Range(0, routes).Select(index => new Route
            {
                OwnerId = setup.Fixture.OtherOwner,
                Name = $"Dense path {index}",
                GeometryWkb = dense,
                Sport = SportKind.Cycling
            }));
            await db.SaveChangesAsync();
            Assert.True(routes * dense.Length >= 4 * 1024 * 1024);
        }

        probe.Reads.Clear();
        var plan = await setup.Writes.PrepareAsync(Request("create_tag", setup.Fixture.Owner, new { name = "Small edit" }), CancellationToken.None);
        await setup.Writes.ApproveAsync(plan.PlanId, null, CancellationToken.None);
        Assert.Equal("completed", (await WaitAsync(setup.Writes, plan.PlanId)).Status);

        var reads = probe.Reads.ToArray();
        Assert.Equal(4, reads.Count(command => command.Contains("FROM \"McpLibraryRevision\"", StringComparison.Ordinal)));
        Assert.DoesNotContain(reads, command => command.Contains("FROM \"Routes\"", StringComparison.Ordinal)
            || command.Contains("GeometryWkb", StringComparison.Ordinal) || command.Contains("CompressedPayload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Detached_preview_does_not_block_writes_or_publish_after_a_library_change()
    {
        var importer = new PausedPreviewImporter();
        var clock = new AdjustableClock();
        await using var setup = await Setup.CreateAsync(clock: clock, configure: builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IActivityImporter>();
            services.AddSingleton<IActivityImporter>(importer);
        }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        var owner = setup.Fixture.Owner;
        await using var upload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(TestSupport.Gpx()));
        var asset = await setup.Writes.ApproveFileAsync(owner, "detached.gpx", upload, 1024 * 1024, token);
        var paths = setup.Services.GetRequiredService<AppDataPaths>();
        var directory = Path.Combine(paths.StagingPath, "mcp-assets", asset.Id.ToString("N"));
        var preparing = setup.Writes.PrepareAsync(Request("import_activities", owner, new { assetId = asset.Id, sourceKind = SourceKind.Gpx }), token);
        try
        {
            await importer.Entered.Task.WaitAsync(token);
            await setup.Services.GetRequiredService<IActivityOrganizationService>().CreateTagAsync(setup.Fixture.OtherOwner, "Writes during extraction", token);
            clock.Advance(TimeSpan.FromHours(1));
            await setup.Writes.CleanupArtifactsAsync(token);
            Assert.True(File.Exists(Path.Combine(directory, asset.Name)));
            Assert.False(preparing.IsCompleted);
        }
        finally { importer.Release.TrySetResult(); }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => preparing);
        Assert.Contains("changed while preparing", error.Message, StringComparison.Ordinal);
        await setup.Writes.CleanupArtifactsAsync(token);
        Assert.False(Directory.Exists(directory));
        await using var db = await setup.Fixture.Factory.CreateDbContextAsync(token);
        Assert.Empty(await db.McpOperations.ToArrayAsync(token));
        Assert.Empty(await db.ImportBatches.ToArrayAsync(token));
    }

    private sealed class PausedPreviewImporter : IActivityImporter
    {
        public string Name => "Paused preview";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanImport(string path) => true;

        public async Task<IReadOnlyList<ImportCandidate>> ReadAsync(string path, SourceKind source, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return [];
        }
    }

    private sealed class RevisionCommandProbe : DbCommandInterceptor
    {
        internal ConcurrentQueue<string> Reads { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)) Reads.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class RevisionContextFactory(DbContextOptions<ExplorerDbContext> options) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);
    }
}
