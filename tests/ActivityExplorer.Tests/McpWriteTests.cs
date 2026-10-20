using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using ActivityExplorer.Mcp;
using ActivityExplorer.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ActivityExplorer.Tests;

public sealed class McpWriteTests
{
    [Fact]
    public async Task Preview_is_read_only_and_changed_data_or_wrong_phrase_cannot_be_approved()
    {
        await using var setup = await Setup.CreateAsync();
        await using var db = await setup.Fixture.Factory.CreateDbContextAsync();
        var before = await db.Segments.CountAsync();
        var preview = await setup.Writes.PrepareAsync(Request("create_segment_from_activity", setup.Fixture.Owner,
            new { activityId = setup.Fixture.Activity, name = "Reviewed climb", startPointIndex = 3, endPointIndex = 25, toleranceMeters = 30d, reverseDirection = false }), CancellationToken.None);
        Assert.Equal(before, await db.Segments.CountAsync());
        Assert.Empty(await db.McpOperations.ToArrayAsync());
        Assert.Empty(await db.McpEffects.ToArrayAsync());
        Assert.True(preview.Details.GetProperty("dryRun").GetBoolean());
        Assert.True(preview.Details.GetProperty("effects").GetProperty("distanceMeters").GetDouble() > 0);
        await setup.Services.GetRequiredService<IActivityOrganizationService>().CreateTagAsync(setup.Fixture.Owner, "Changed after preview");
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Writes.ApproveAsync(preview.PlanId, null, CancellationToken.None));
        var delete = await setup.Writes.PrepareAsync(Request("delete_activities", setup.Fixture.Owner, new { activityIds = new[] { setup.Fixture.Activity } }), CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Writes.ApproveAsync(delete.PlanId, "yes", CancellationToken.None));
        setup.Writes.Decline(delete.PlanId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => setup.Writes.ApproveAsync(delete.PlanId, delete.ConfirmationPhrase, CancellationToken.None));
        Assert.Empty(await db.McpOperations.ToArrayAsync());
        Assert.Equal(before, await db.Segments.CountAsync());
    }

    [Fact]
    public async Task Execution_is_idempotent_and_receipt_is_atomic_with_segment_creation()
    {
        await using var setup = await Setup.CreateAsync();
        var preview = await setup.Writes.PrepareAsync(Request("create_segment_from_activity", setup.Fixture.Owner,
            new { activityId = setup.Fixture.Activity, name = "One climb", startPointIndex = 3, endPointIndex = 25, toleranceMeters = 30d, reverseDirection = false }), CancellationToken.None);
        var approvals = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => setup.Writes.ApproveAsync(preview.PlanId, null, CancellationToken.None)));
        Assert.All(approvals, result => Assert.Equal(preview.PlanId, result.OperationId));
        var status = await WaitAsync(setup.Writes, preview.PlanId);
        Assert.Equal("completed", status.Status);
        await using var db = await setup.Fixture.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.Segments.CountAsync(item => item.Name == "One climb"));
        var effect = await db.McpEffects.SingleAsync(item => item.Id == preview.PlanId);
        Assert.Equal(status.EntityId, effect.EntityId);
        Assert.Equal("", (await db.McpOperations.SingleAsync()).RequestJson);
        Assert.Equal(status.EntityId, (await setup.Writes.ApproveAsync(preview.PlanId, null, CancellationToken.None)).EntityId);
    }

    [Fact]
    public async Task Permissions_ownership_expiry_and_browser_origins_are_enforced()
    {
        await using var setup = await Setup.CreateAsync(allowExtra: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => setup.Writes.PrepareAsync(Request("delete_profile", setup.Fixture.Owner, new { }), CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => setup.Writes.PrepareAsync(Request("set_map_privacy", null, new { mode = MapPrivacyMode.Blank }), CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => setup.Writes.PrepareAsync(Request("create_tag", setup.Fixture.OtherOwner, new { name = "Hidden" }), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Writes.PrepareAsync(Request("update_activity", setup.Fixture.Owner,
            new { activityId = setup.Fixture.OtherActivity, request = new UpdateActivityRequest("Wrong owner", null, null, setup.Fixture.Owner) }), CancellationToken.None));
        using var unauthenticated = await setup.Client.PostAsJsonAsync("/internal/mcp/prepare", Request("create_tag", setup.Fixture.Owner, new { name = "Denied" }), McpChangeJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/internal/mcp/prepare")
        { Content = JsonContent.Create(Request("create_tag", setup.Fixture.Owner, new { name = "Denied" }), options: McpChangeJson.Options) };
        crossOrigin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setup.Writes.Token);
        crossOrigin.Headers.Add("Origin", "https://example.invalid");
        using var denied = await setup.Client.SendAsync(crossOrigin);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var getApproval = await setup.Client.GetAsync("/internal/mcp/plans/" + Guid.NewGuid() + "/approve");
        Assert.NotEqual(HttpStatusCode.OK, getApproval.StatusCode);
        await using var db = await setup.Fixture.Factory.CreateDbContextAsync();
        Assert.Empty(await db.McpOperations.ToArrayAsync());
    }

    [Fact]
    public async Task Every_existing_action_family_can_be_previewed_and_executed()
    {
        await using var setup = await Setup.CreateAsync();
        var owner = setup.Fixture.Owner;
        var org = setup.Services.GetRequiredService<IActivityOrganizationService>();
        var criteria = new SavedSearchCriteria(2, null, null, null, null, [], "start-desc", new(ReportingPreset.AllTime));
        var saved = await org.SaveSavedSearchAsync(owner, "Saved selection", criteria);
        var metric = await setup.Services.GetRequiredService<IActivityQueryService>().AddMetricAsync(setup.Fixture.Activity, new("custom", "Custom", 1, null, "count"));
        var child = await setup.Services.GetRequiredService<ISegmentService>().CreateFromActivityAsync(new(owner, setup.Fixture.Activity, "Independent child", 10, 25));
        var start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7);
        var goalId = await setup.Services.GetRequiredService<IGoalService>().CreateAsync(new(owner, new(GoalMetric.Distance, GoalRecurrence.Once, start, start.AddDays(6)), new("Goal", null, 1000)));
        var goal = (await setup.Services.GetRequiredService<IGoalService>().GetDetailAsync(goalId))!;
        await using var upload = new MemoryStream(Encoding.UTF8.GetBytes(TestSupport.Gpx()));
        var asset = await setup.Writes.ApproveFileAsync(owner, "selected.gpx", upload, 1024 * 1024, CancellationToken.None);
        var folderPath = Path.Combine(setup.Fixture.DirectoryPath, "approved-source");
        Directory.CreateDirectory(folderPath);
        var folderAsset = setup.Writes.ApproveFolder(owner, folderPath);
        var folderId = await setup.Services.GetRequiredService<IWatchedFolderService>().AddAsync(owner, folderPath);
        var points = TestSupport.Track(30);
        var operations = new List<McpChangeRequest>
        {
            Request("create_segment_from_activity", owner, new { activityId = setup.Fixture.Activity, name = "Activity section", startPointIndex = 3, endPointIndex = 25, toleranceMeters = 30d, reverseDirection = true }),
            Request("create_segment_from_route", owner, new { routeId = setup.Fixture.Route, name = "Route section", startPointIndex = 3, endPointIndex = 25, toleranceMeters = 30d, reverseDirection = false }),
            Request("create_segment_path", owner, new { name = "Drawn segment", sport = SportKind.Cycling, points, toleranceMeters = 30d }),
            Request("create_subsegment", owner, new { segmentId = setup.Fixture.Segment, name = "Child section", startPointIndex = 3, endPointIndex = 25, toleranceMeters = 30d }),
            Request("attach_segment", owner, new { parentSegmentId = setup.Fixture.Segment, childSegmentId = child }),
            Request("detach_segment", owner, new { parentSegmentId = setup.Fixture.Segment, childSegmentId = child }),
            Request("recompute_segment", owner, new { segmentId = setup.Fixture.Segment }),
            Request("update_activity", owner, new { activityId = setup.Fixture.Activity, request = new UpdateActivityRequest("Reviewed title", "Description", "Bike", owner) }),
            Request("add_activity_metric", owner, new { activityId = setup.Fixture.Activity, request = new ActivityMetricRequest("another", "Another", 2, null, "count") }),
            Request("update_activity_metric", owner, new { activityId = setup.Fixture.Activity, metricId = metric, request = new ActivityMetricRequest("custom", "Changed", 3, null, "count") }),
            Request("delete_activity_metric", owner, new { activityId = setup.Fixture.Activity, metricId = metric }),
            Request("create_tag", owner, new { name = "New tag" }),
            Request("rename_tag", owner, new { tagId = setup.Fixture.TagId, name = "Renamed training" }),
            Request("set_activity_tags", owner, new { activityId = setup.Fixture.Activity, tagIds = new[] { setup.Fixture.TagId } }),
            Request("apply_activity_batch", owner, new { activityIds = new[] { setup.Fixture.Activity }, changes = new BatchChangeRequest([], [], GearEditMode.Set, "Reviewed gear") }),
            Request("save_saved_search", owner, new { name = "New search", criteria }),
            Request("rename_saved_search", owner, new { savedSearchId = saved, name = "Renamed selection" }),
            Request("delete_saved_search", owner, new { savedSearchId = saved }),
            Request("create_route_from_activity", owner, new { activityId = setup.Fixture.Activity, name = "Activity route", description = "Saved" }),
            Request("create_route_path", owner, new { name = "Drawn route", sport = SportKind.Cycling, points, description = "Drawn" }),
            Request("import_segment", owner, new { assetId = asset.Id, name = "Imported segment", sport = SportKind.Cycling, toleranceMeters = 30d }),
            Request("create_goal", owner, new { request = new CreateGoalRequest(owner, new(GoalMetric.ActivityCount, GoalRecurrence.Weekly, start, null), new("Weekly goal", null, 3)) }),
            Request("edit_goal", owner, new { goalId, request = new EditGoalRequest(owner, goalId, goal.Goal.MutationVersion, goal.Edition!.Key, GoalEditScope.ThisEdition, new("Edited goal", null, 2000), goal.Edition.State) }),
            Request("create_profile", null, new { name = "New profile without implicit access" }),
            Request("update_profile_timezone", owner, new { timeZoneId = "UTC" }),
            Request("add_watched_folder", owner, new { assetId = folderAsset.Id }),
            Request("remove_watched_folder", owner, new { folderId }),
            Request("recompute_statistics", owner, new { }),
            Request("rebuild_exploration", owner, new { recheckLimited = false }),
            Request("set_map_privacy", null, new { mode = MapPrivacyMode.Blank }),
            Request("export_profile", owner, new { }),
            Request("export_route", owner, new { routeId = setup.Fixture.Route }),
            Request("delete_tag", owner, new { tagId = setup.Fixture.TagId })
        };
        foreach (var request in operations)
        {
            var preview = await setup.Writes.PrepareAsync(request, CancellationToken.None);
            var accepted = await setup.Writes.ApproveAsync(preview.PlanId, preview.ConfirmationPhrase, CancellationToken.None);
            var completed = await WaitAsync(setup.Writes, accepted.OperationId);
            Assert.True(completed.Status == "completed", $"{request.Action}: {completed.Status}: {completed.Message}");
            if (request.Action == "create_profile")
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => setup.Writes.ContextAsync(new(completed.EntityId!.Value, "tags"), CancellationToken.None));
            if (request.Action.StartsWith("export_", StringComparison.Ordinal))
            {
                using var download = await setup.Client.GetAsync("/mcp/download/" + completed.OperationId);
                Assert.Equal(HttpStatusCode.OK, download.StatusCode);
                Assert.DoesNotContain("latitude", completed.Result!.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);
                if (request.Action == "export_route")
                {
                    await using var gpx = await download.Content.ReadAsStreamAsync();
                    var document = await System.Xml.Linq.XDocument.LoadAsync(gpx, System.Xml.Linq.LoadOptions.None, CancellationToken.None);
                    Assert.Equal("gpx", document.Root!.Name.LocalName);
                }
                if (request.Action == "export_profile")
                {
                    var snapshot = await download.Content.ReadAsStringAsync();
                    await setup.Services.GetRequiredService<IActivityQueryService>().UpdateAsync(setup.Fixture.Activity, new("Edited after export", null, null, owner));
                    using var repeatedDownload = await setup.Client.GetAsync("/mcp/download/" + completed.OperationId);
                    Assert.Equal(snapshot, await repeatedDownload.Content.ReadAsStringAsync());
                }
            }
        }
        await using var routeUpload = new MemoryStream(Encoding.UTF8.GetBytes(TestSupport.Gpx()));
        var routeAsset = await setup.Writes.ApproveFileAsync(owner, "route.gpx", routeUpload, 1024 * 1024, CancellationToken.None);
        var routePreview = await setup.Writes.PrepareAsync(Request("import_route", owner, new { assetId = routeAsset.Id, name = "Retained GPX route", sport = SportKind.Cycling }), CancellationToken.None);
        await setup.Writes.ApproveAsync(routePreview.PlanId, null, CancellationToken.None);
        var importedRoute = await WaitAsync(setup.Writes, routePreview.PlanId);
        Assert.Equal("completed", importedRoute.Status);
        await using (var db = await setup.Fixture.Factory.CreateDbContextAsync())
        {
            var source = await db.SourceFiles.SingleAsync(item => item.RouteId == importedRoute.EntityId);
            var original = await setup.Writes.PrepareAsync(Request("download_original", owner, new { sourceFileId = source.Id }), CancellationToken.None);
            await setup.Writes.ApproveAsync(original.PlanId, null, CancellationToken.None);
            Assert.Equal("completed", (await WaitAsync(setup.Writes, original.PlanId)).Status);
            using var download = await setup.Client.GetAsync("/mcp/download/" + original.PlanId);
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Contains("gpx", await download.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        var importPreview = await setup.Writes.PrepareAsync(Request("import_activities", owner, new { assetId = asset.Id, sourceKind = SourceKind.Gpx }), CancellationToken.None);
        await setup.Writes.ApproveAsync(importPreview.PlanId, null, CancellationToken.None);
        Assert.Equal("Completed", (await WaitAsync(setup.Writes, importPreview.PlanId)).Status);
        var latestGoal = (await setup.Services.GetRequiredService<IGoalService>().GetDetailAsync(goalId))!;
        var archive = await setup.Writes.PrepareAsync(Request("archive_goal", owner, new
        {
            goalId,
            request = new ArchiveGoalRequest(owner, goalId, latestGoal.Goal.MutationVersion, latestGoal.Edition!.Key, latestGoal.Edition.State)
        }), CancellationToken.None);
        await setup.Writes.ApproveAsync(archive.PlanId, null, CancellationToken.None);
        Assert.Equal("completed", (await WaitAsync(setup.Writes, archive.PlanId)).Status);
        var transfer = await setup.Writes.PrepareAsync(Request("transfer_activity", owner, new { activityId = setup.Fixture.Activity, destinationProfileId = setup.Fixture.OtherOwner }), CancellationToken.None);
        await setup.Writes.ApproveAsync(transfer.PlanId, null, CancellationToken.None);
        Assert.Equal("completed", (await WaitAsync(setup.Writes, transfer.PlanId)).Status);
        var deleteActivities = await setup.Writes.PrepareAsync(Request("delete_activities", setup.Fixture.OtherOwner, new { activityIds = new[] { setup.Fixture.Activity } }), CancellationToken.None);
        await setup.Writes.ApproveAsync(deleteActivities.PlanId, deleteActivities.ConfirmationPhrase, CancellationToken.None);
        Assert.Equal("completed", (await WaitAsync(setup.Writes, deleteActivities.PlanId)).Status);
        await using var unimported = new MemoryStream(Encoding.UTF8.GetBytes(TestSupport.Gpx()));
        var unimportedAsset = await setup.Writes.ApproveFileAsync(owner, "not-imported.gpx", unimported, 1024 * 1024, CancellationToken.None);
        var deleteProfile = await setup.Writes.PrepareAsync(Request("delete_profile", owner, new { }), CancellationToken.None);
        await setup.Writes.ApproveAsync(deleteProfile.PlanId, deleteProfile.ConfirmationPhrase, CancellationToken.None);
        Assert.Equal("completed", (await WaitAsync(setup.Writes, deleteProfile.PlanId)).Status);
        Assert.Equal("completed", (await setup.Writes.StatusAsync(deleteProfile.PlanId, CancellationToken.None)).Status);
        Assert.False(Directory.Exists(Path.Combine(setup.Fixture.DirectoryPath, "staging", "mcp-assets", unimportedAsset.Id.ToString("N"))));
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Writes.ContextAsync(new(owner, "assets"), CancellationToken.None));
        await using var receipts = await setup.Fixture.Factory.CreateDbContextAsync();
        Assert.Equal(McpChangeCatalog.Actions.Order(StringComparer.Ordinal), (await receipts.McpOperations.Select(item => item.Action).Distinct().ToArrayAsync()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Restart_recovery_distinguishes_a_committed_effect_from_an_unfinished_command()
    {
        await using var setup = await Setup.CreateAsync();
        var committed = Guid.NewGuid(); var unfinished = Guid.NewGuid();
        await using (var db = await setup.Fixture.Factory.CreateDbContextAsync())
        {
            db.McpOperations.AddRange(new McpOperation { Id = committed, Action = "create_tag", Status = "running", ProfileIdsJson = JsonSerializer.Serialize(new[] { setup.Fixture.Owner }), RequestJson = "private request" },
                new McpOperation { Id = unfinished, Action = "create_tag", Status = "running", ProfileIdsJson = JsonSerializer.Serialize(new[] { setup.Fixture.Owner }), RequestJson = "private request" });
            db.McpEffects.Add(new McpEffect { Id = committed, EntityId = setup.Fixture.TagId });
            await db.SaveChangesAsync();
        }
        await setup.Writes.RecoverAsync(CancellationToken.None);
        Assert.Equal("committed_with_warnings", (await setup.Writes.StatusAsync(committed, CancellationToken.None)).Status);
        Assert.Equal(setup.Fixture.TagId, (await setup.Writes.StatusAsync(committed, CancellationToken.None)).EntityId);
        Assert.Equal("interrupted", (await setup.Writes.StatusAsync(unfinished, CancellationToken.None)).Status);
        await using var check = await setup.Fixture.Factory.CreateDbContextAsync();
        Assert.All(await check.McpOperations.ToArrayAsync(), operation => Assert.Empty(operation.RequestJson));
        Assert.Single(await check.Tags.ToArrayAsync());
    }

    [Fact]
    public async Task Expired_previews_cannot_queue_changes()
    {
        var clock = new AdjustableClock();
        await using var setup = await Setup.CreateAsync(clock: clock);
        var preview = await setup.Writes.PrepareAsync(Request("create_tag", setup.Fixture.Owner, new { name = "Expired selection" }), CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Throws<KeyNotFoundException>(() => setup.Writes.GetPreview(preview.PlanId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => setup.Writes.ApproveAsync(preview.PlanId, null, CancellationToken.None));
        await using var db = await setup.Fixture.Factory.CreateDbContextAsync();
        Assert.Empty(await db.McpOperations.ToArrayAsync());
    }

    [Fact]
    public async Task Rolled_back_mutation_cannot_leave_a_success_receipt()
    {
        await using var fixture = await McpFixture.CreateAsync();
        var id = Guid.NewGuid();
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            using var scope = new McpOperationScope(id, typeof(Tag));
            db.Tags.Add(new Tag { OwnerId = fixture.Owner, Name = "Rolled back", NormalizedName = "ROLLED BACK" });
            await db.SaveChangesAsync();
            Assert.True(await db.McpEffects.AnyAsync(item => item.Id == id));
            await transaction.RollbackAsync();
        }
        await using var check = await fixture.Factory.CreateDbContextAsync();
        Assert.False(await check.Tags.AnyAsync(item => item.Name == "Rolled back"));
        Assert.False(await check.McpEffects.AnyAsync(item => item.Id == id));
    }

    [Fact]
    public async Task Only_one_web_writer_can_own_a_data_root_while_readers_remain_available()
    {
        await using var setup = await Setup.CreateAsync();
        Assert.Throws<InvalidOperationException>(() => new WebWriterLease(setup.Services.GetRequiredService<AppDataPaths>()));
        using var response = await setup.Client.GetAsync("/activities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Restart_cleans_orphan_selections_but_preserves_import_and_recovery_sources()
    {
        await using var setup = await Setup.CreateAsync();
        string MakeSelection()
        {
            var directory = Path.Combine(setup.Fixture.DirectoryPath, "staging", "mcp-assets", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "source.gpx");
            File.WriteAllText(file, TestSupport.Gpx());
            return file;
        }
        var orphan = MakeSelection(); var importing = MakeSelection(); var recovering = MakeSelection();
        await using (var db = await setup.Fixture.Factory.CreateDbContextAsync())
        {
            db.ImportBatches.Add(new ImportBatch { OwnerId = setup.Fixture.Owner, Status = ImportStatus.Interrupted, SourceKind = SourceKind.Gpx, StagedPath = Path.GetRelativePath(setup.Fixture.DirectoryPath, importing), DisplayName = "Pending selection" });
            db.FileOperations.Add(new FileOperationJournal { OwnerId = setup.Fixture.Owner, State = FileOperationState.Pending, Kind = FileOperationKind.Copy, SourceRelativePath = Path.GetRelativePath(setup.Fixture.DirectoryPath, recovering) });
            await db.SaveChangesAsync();
        }
        await setup.Writes.PublishConnectionAsync("http://localhost", CancellationToken.None);
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(importing));
        Assert.True(File.Exists(recovering));
    }

    [Fact]
    public async Task Path_import_previews_preserve_the_existing_web_upload_limits()
    {
        await using var setup = await Setup.CreateAsync(configure: builder => builder.UseSetting("Segments:MaxPathUploadBytes", "64").UseSetting("Routes:MaxGpxUploadBytes", "64"));
        await using var upload = new MemoryStream(Encoding.UTF8.GetBytes(TestSupport.Gpx()));
        var asset = await setup.Writes.ApproveFileAsync(setup.Fixture.Owner, "selected.gpx", upload, 1024 * 1024, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => setup.Writes.PrepareAsync(Request("import_segment", setup.Fixture.Owner,
            new { assetId = asset.Id, name = "Too large", sport = SportKind.Cycling, toleranceMeters = 30d }), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => setup.Writes.PrepareAsync(Request("import_route", setup.Fixture.Owner,
            new { assetId = asset.Id, name = "Too large", sport = SportKind.Cycling }), CancellationToken.None));
        await using var db = await setup.Fixture.Factory.CreateDbContextAsync();
        Assert.Empty(await db.McpOperations.ToArrayAsync());
    }

    [Fact]
    public async Task Write_bridge_keeps_the_readers_startup_data_root_if_the_environment_changes()
    {
        await using var fixture = await McpFixture.CreateAsync();
        var other = Path.Combine(fixture.DirectoryPath, "other-data-root");
        Directory.CreateDirectory(other);
        var connectionFile = Path.Combine(other, "mcp-connection.json");
        await File.WriteAllTextAsync(connectionFile, JsonSerializer.Serialize(new McpConnection("http://127.0.0.1:1", "fictional-test-token",
            McpWriteService.DataIdentity(other), [fixture.Owner], false, false), McpChangeJson.Options));
        using var host = fixture.BuildHost("--Mcp:AccessMode", "ReadWrite", "--Mcp:AllowedWriteProfileIds:0", fixture.Owner.ToString(), "--Mcp:ConnectionFile", connectionFile);
        var previous = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_DATA");
        Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", other + Path.DirectorySeparatorChar);
        try
        {
            Assert.Equal(other, new AppDataPaths().Root);
            var bridge = host.Services.GetRequiredService<McpWriteBridge>();
            var error = await Assert.ThrowsAsync<McpBridgeException>(() => bridge.ContextAsync(new(fixture.Owner, "tags"), CancellationToken.None));
            Assert.Equal("web_incompatible", error.Code);
        }
        finally { Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", previous); }
    }

    private sealed class AdjustableClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    internal static McpChangeRequest Request<T>(string action, Guid? owner, T args) => new(action, owner, McpChangeJson.Element(args));
    internal static async Task<McpOperationStatus> WaitAsync(McpWriteService writes, Guid id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (true)
        {
            var result = await writes.StatusAsync(id, deadline.Token);
            if (result.Status is not ("queued" or "running" or "Queued" or "Running")) return result;
            await Task.Delay(20, deadline.Token);
        }
    }

    private sealed class Setup(McpFixture fixture, WebApplicationFactory<Program> app, HttpClient client, string? previousRoot) : IAsyncDisposable
    {
        public McpFixture Fixture => fixture;
        public HttpClient Client => client;
        public IServiceProvider Services => app.Services;
        public McpWriteService Writes => Services.GetRequiredService<McpWriteService>();
        public static async Task<Setup> CreateAsync(bool allowExtra = true, TimeProvider? clock = null, Action<IWebHostBuilder>? configure = null)
        {
            var fixture = await McpFixture.CreateAsync();
            await using (var db = await fixture.Factory.CreateDbContextAsync())
            {
                await db.ImportBatches.ExecuteDeleteAsync();
                await db.FileOperations.ExecuteDeleteAsync();
            }
            var previous = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_DATA");
            Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", fixture.DirectoryPath);
            var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("McpWrites:Enabled", "true");
                builder.UseSetting("McpWrites:ProfileIds:0", fixture.Owner.ToString());
                if (allowExtra) builder.UseSetting("McpWrites:ProfileIds:1", fixture.OtherOwner.ToString());
                builder.UseSetting("McpWrites:AllowDestructive", allowExtra.ToString());
                builder.UseSetting("McpWrites:AllowAdministration", allowExtra.ToString());
                if (clock is not null) builder.ConfigureServices(services => services.AddSingleton(clock));
                configure?.Invoke(builder);
            });
            try { return new(fixture, app, app.CreateClient(), previous); }
            catch { await app.DisposeAsync(); Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", previous); await fixture.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
            Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", previousRoot);
            await fixture.DisposeAsync();
        }
    }
}
