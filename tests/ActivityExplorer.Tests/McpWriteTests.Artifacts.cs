using System.Net;
using System.Text;
using System.Text.Json;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ActivityExplorer.Tests;

public sealed partial class McpWriteTests
{
    [Fact]
    public async Task Running_host_reclaims_expired_assets_and_downloads()
    {
        var clock = new ArtifactClock();
        await using var setup = await Setup.CreateAsync(clock: clock);
        var owner = setup.Fixture.Owner;
        var assetId = await SelectArtifactAsync(setup, owner);
        var exportId = await ExportArtifactAsync(setup, owner);
        using (var response = await setup.Client.GetAsync("/mcp/download/" + exportId))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        var userFolder = Path.Combine(setup.Fixture.DirectoryPath, "user-folder");
        Directory.CreateDirectory(userFolder);
        var folder = await setup.Writes.ApproveFolderAsync(owner, userFolder, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(userFolder, "keep.txt"), "External selection");

        await clock.AdvanceAsync(TimeSpan.FromMinutes(59));
        var freshId = await SelectArtifactAsync(setup, owner);
        await clock.AdvanceAsync(TimeSpan.FromMinutes(1));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (Directory.Exists(ArtifactPath(setup, "mcp-assets", assetId)) ||
               Directory.Exists(ArtifactPath(setup, "mcp-downloads", exportId)))
            await Task.Delay(20, deadline.Token);
        Assert.True(Directory.Exists(ArtifactPath(setup, "mcp-assets", freshId)));
        Assert.True(File.Exists(Path.Combine(userFolder, "keep.txt")));
        var assets = await setup.Writes.ContextAsync(new(owner, "assets"), CancellationToken.None);
        Assert.Equal(1, assets.Total);
        Assert.DoesNotContain(assets.Items, item => item.GetProperty("id").GetGuid() == folder.Id);
        using var expired = await setup.Client.GetAsync("/mcp/download/" + exportId, deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.True(expired.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_profile_deletion_paths_remove_only_their_artifacts_and_preserve_open_readers(bool viaMcp)
    {
        await using var setup = await Setup.CreateAsync();
        var owner = setup.Fixture.Owner;
        var otherOwner = setup.Fixture.OtherOwner;
        var assetId = await SelectArtifactAsync(setup, owner);
        var otherAssetId = await SelectArtifactAsync(setup, otherOwner);
        var exportId = await ExportArtifactAsync(setup, owner);
        var otherExportId = await ExportArtifactAsync(setup, otherOwner);
        var open = await setup.Writes.OpenDownloadAsync(exportId, CancellationToken.None);
        Assert.NotNull(open);
        await using (open.Contents)
        {
            if (viaMcp)
            {
                var preview = await setup.Writes.PrepareAsync(Request("delete_profile", owner, new { }), CancellationToken.None);
                await setup.Writes.ApproveAsync(preview.PlanId, preview.ConfirmationPhrase, CancellationToken.None);
                var deletionStatus = (await WaitAsync(setup.Writes, preview.PlanId)).Status;
                Assert.True(deletionStatus is "completed" or "committed_with_warnings", deletionStatus);
            }
            else
            {
                var profiles = setup.Services.GetRequiredService<IProfileService>();
                await profiles.DeleteAsync(owner, "DELETE MCP athlete");
            }
            Assert.False(Directory.Exists(ArtifactPath(setup, "mcp-assets", assetId)));
            Assert.True(Directory.Exists(ArtifactPath(setup, "mcp-assets", otherAssetId)));
            using var removed = await setup.Client.GetAsync("/mcp/download/" + exportId);
            Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
            using var unaffected = await setup.Client.GetAsync("/mcp/download/" + otherExportId);
            Assert.Equal(HttpStatusCode.OK, unaffected.StatusCode);

            using var bytes = new MemoryStream();
            await open.Contents.CopyToAsync(bytes);
            Assert.Contains("MCP athlete", Encoding.UTF8.GetString(bytes.ToArray()), StringComparison.Ordinal);
        }
        await setup.Writes.CleanupArtifactsAsync(CancellationToken.None);
        Assert.False(Directory.Exists(ArtifactPath(setup, "mcp-downloads", exportId)));
        Assert.True(Directory.Exists(ArtifactPath(setup, "mcp-downloads", otherExportId)));
    }

    [Fact]
    public async Task Failed_output_removes_its_owned_directory()
    {
        await using var setup = await Setup.CreateAsync();
        var owner = setup.Fixture.Owner;
        var batch = new ImportBatch { OwnerId = owner, DisplayName = "Synthetic", SourceKind = SourceKind.Gpx, Status = ImportStatus.Completed };
        var original = new SourceFile
        {
            OwnerId = owner,
            ImportBatchId = batch.Id,
            RouteId = setup.Fixture.Route,
            SourceKind = SourceKind.Gpx,
            OriginalName = "missing.gpx",
            StoredPath = $"originals/{owner:N}/missing.gpx"
        };
        await using (var db = await setup.Fixture.Factory.CreateDbContextAsync())
        {
            db.ImportBatches.Add(batch);
            db.SourceFiles.Add(original);
            await db.SaveChangesAsync();
        }

        var preview = await setup.Writes.PrepareAsync(Request("download_original", owner, new { sourceFileId = original.Id }), CancellationToken.None);
        await setup.Writes.ApproveAsync(preview.PlanId, null, CancellationToken.None);
        Assert.Equal("failed", (await WaitAsync(setup.Writes, preview.PlanId)).Status);
        await setup.Services.GetRequiredService<IOwnerMutationLock>().RunAsync(() => Task.FromResult(true));
        Assert.Null(await setup.Writes.OpenDownloadAsync(preview.PlanId, CancellationToken.None));
        Assert.False(Directory.Exists(ArtifactPath(setup, "mcp-downloads", preview.PlanId)));
    }

    [Fact]
    public async Task Legacy_cleanup_uses_the_receipt_and_exact_payload_path()
    {
        await using var setup = await Setup.CreateAsync();
        var owner = setup.Fixture.Owner;
        var receipt = new McpOperation
        {
            Id = Guid.NewGuid(),
            Action = "export_profile",
            ProfileIdsJson = JsonSerializer.Serialize(new[] { owner }),
            RequestJson = "{}",
            RevisionToken = "legacy"
        };
        receipt.StartExecution();
        receipt.Finish(McpExecutionState.Failed, DateTimeOffset.UtcNow);
        await using (var db = await setup.Fixture.Factory.CreateDbContextAsync())
        {
            db.McpOperations.Add(receipt);
            await db.SaveChangesAsync();
        }

        var legacy = ArtifactPath(setup, "mcp-downloads", receipt.Id);
        var unrelated = ArtifactPath(setup, "mcp-downloads", Guid.NewGuid());
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(unrelated);
        await File.WriteAllTextAsync(Path.Combine(legacy, "content"), "Legacy app output");
        await File.WriteAllTextAsync(Path.Combine(legacy, "keep.txt"), "Unrelated sibling");
        await File.WriteAllTextAsync(Path.Combine(unrelated, "content"), "Unowned data");

        await setup.Writes.CleanupArtifactsAsync(CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(legacy, "content")));
        Assert.True(File.Exists(Path.Combine(legacy, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(unrelated, "content")));
    }

    [Fact]
    public async Task Failed_file_selection_reclaims_its_owned_temporary_input()
    {
        await using var setup = await Setup.CreateAsync();
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(TestSupport.Gpx()));
        await Assert.ThrowsAsync<InvalidDataException>(() => setup.Writes.ApproveFileAsync(
            setup.Fixture.Owner, "too-large.gpx", source, maximumBytes: 16, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(setup.Fixture.DirectoryPath, "staging", "mcp-assets")));
    }

    [Fact]
    public async Task Approval_cannot_publish_an_upload_that_overlaps_its_profile_deletion()
    {
        await using var setup = await Setup.CreateAsync();
        using var source = new GatedUpload(Encoding.UTF8.GetBytes(TestSupport.Gpx()));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var selection = setup.Writes.ApproveFileAsync(setup.Fixture.Owner, "copying.gpx", source, 1024 * 1024, deadline.Token);
        try
        {
            await source.CopyStarted.WaitAsync(deadline.Token);
            await setup.Services.GetRequiredService<IProfileService>().DeleteAsync(setup.Fixture.Owner, "DELETE MCP athlete", deadline.Token);
        }
        finally { source.Release(); }

        var error = await Record.ExceptionAsync(() => selection);
        Assert.True(error is IOException or ArgumentException, error?.ToString());
        await setup.Writes.CleanupArtifactsAsync(deadline.Token);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(setup.Fixture.DirectoryPath, "staging", "mcp-assets")));
    }

    private static async Task<Guid> SelectArtifactAsync(Setup setup, Guid owner)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(TestSupport.Gpx()));
        return (await setup.Writes.ApproveFileAsync(owner, "export-test.gpx", source, 1024 * 1024, CancellationToken.None)).Id;
    }

    private static async Task<Guid> ExportArtifactAsync(Setup setup, Guid owner)
    {
        var preview = await setup.Writes.PrepareAsync(Request("export_profile", owner, new { }), CancellationToken.None);
        await setup.Writes.ApproveAsync(preview.PlanId, null, CancellationToken.None);
        Assert.Equal("completed", (await WaitAsync(setup.Writes, preview.PlanId)).Status);
        return preview.PlanId;
    }

    private static string ArtifactPath(Setup setup, string area, Guid id) =>
        Path.Combine(setup.Fixture.DirectoryPath, "staging", area, id.ToString("N"));

    private sealed class GatedUpload(byte[] contents) : MemoryStream(contents)
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CopyStarted => _started.Task;
        public void Release() => _release.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class ArtifactClock : TimeProvider
    {
        private long _utcTicks = DateTimeOffset.UtcNow.UtcTicks;
        private readonly TaskCompletionSource<ManualTimer> _cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (period != TimeSpan.FromMinutes(1)) return base.CreateTimer(callback, state, dueTime, period);
            var timer = new ManualTimer(callback, state);
            _cleanup.SetResult(timer);
            return timer;
        }

        public async Task AdvanceAsync(TimeSpan duration)
        {
            Interlocked.Add(ref _utcTicks, duration.Ticks);
            (await _cleanup.Task.WaitAsync(TimeSpan.FromSeconds(20))).Tick();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public void Tick() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
