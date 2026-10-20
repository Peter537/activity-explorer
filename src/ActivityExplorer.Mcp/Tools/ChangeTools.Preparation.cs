using System.ComponentModel;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Mcp.Models;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ActivityExplorer.Mcp.Tools;

public sealed partial class ChangeTools
{
    [McpServerTool(Name = "prepare_create_segment_from_activity", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Create a segment from an exact inclusive activity stream range. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> CreateSegmentFromActivity(Guid profileId, Guid activityId, string name, int startPointIndex, int endPointIndex, CancellationToken cancellationToken, double toleranceMeters = 30, bool reverseDirection = false) =>
        Prepare("create_segment_from_activity", profileId, new { activityId, name, startPointIndex, endPointIndex, toleranceMeters, reverseDirection }, cancellationToken);

    [McpServerTool(Name = "prepare_create_segment_from_route", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Create a segment from an inclusive saved-route point range. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> CreateSegmentFromRoute(Guid profileId, Guid routeId, string name, int startPointIndex, int endPointIndex, CancellationToken cancellationToken, double toleranceMeters = 30, bool reverseDirection = false) =>
        Prepare("create_segment_from_route", profileId, new { routeId, name, startPointIndex, endPointIndex, toleranceMeters, reverseDirection }, cancellationToken);

    [McpServerTool(Name = "prepare_create_segment_path", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Create a segment from explicitly supplied path points. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> CreateSegmentPath(Guid profileId, string name, SportKind sport, TrackPoint[] points, CancellationToken cancellationToken, double toleranceMeters = 30) =>
        Prepare("create_segment_path", profileId, new { name, sport, points, toleranceMeters }, cancellationToken);

    [McpServerTool(Name = "prepare_create_subsegment", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Create and attach a smaller child segment from its parent's inclusive point range. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> CreateSubsegment(Guid profileId, Guid segmentId, string name, int startPointIndex, int endPointIndex, CancellationToken cancellationToken, double toleranceMeters = 30) =>
        Prepare("create_subsegment", profileId, new { segmentId, name, startPointIndex, endPointIndex, toleranceMeters }, cancellationToken);

    [McpServerTool(Name = "prepare_attach_segment", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Attach an existing child to a parent after containment, direction and cycle checks. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> AttachSegment(Guid profileId, Guid parentSegmentId, Guid childSegmentId, CancellationToken cancellationToken) =>
        Prepare("attach_segment", profileId, new { parentSegmentId, childSegmentId }, cancellationToken);

    [McpServerTool(Name = "prepare_detach_segment", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Remove a parent-child relationship while retaining both segments. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> DetachSegment(Guid profileId, Guid parentSegmentId, Guid childSegmentId, CancellationToken cancellationToken) =>
        Prepare("detach_segment", profileId, new { parentSegmentId, childSegmentId }, cancellationToken);

    [McpServerTool(Name = "prepare_recompute_segment", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Recompute historical segment efforts. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> RecomputeSegment(Guid profileId, Guid segmentId, CancellationToken cancellationToken) =>
        Prepare("recompute_segment", profileId, new { segmentId }, cancellationToken);

    [McpServerTool(Name = "prepare_update_activity", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Edit title, description and gear. request.ownerId must match profileId. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> UpdateActivity(Guid profileId, Guid activityId, UpdateActivityRequest request, CancellationToken cancellationToken) =>
        Prepare("update_activity", profileId, new { activityId, request }, cancellationToken);

    [McpServerTool(Name = "prepare_transfer_activity", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Move an activity, originals and provenance between explicitly permitted profiles. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> TransferActivity(Guid profileId, Guid activityId, Guid destinationProfileId, CancellationToken cancellationToken) =>
        Prepare("transfer_activity", profileId, new { activityId, destinationProfileId }, cancellationToken);

    [McpServerTool(Name = "prepare_delete_activities", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Delete a frozen explicit set of 1 to 1000 activities and eligible originals. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> DeleteActivities(Guid profileId, Guid[] activityIds, CancellationToken cancellationToken) =>
        Prepare("delete_activities", profileId, new { activityIds }, cancellationToken);

    [McpServerTool(Name = "prepare_add_activity_metric", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Add a manual activity metric. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> AddActivityMetric(Guid profileId, Guid activityId, ActivityMetricRequest request, CancellationToken cancellationToken) =>
        Prepare("add_activity_metric", profileId, new { activityId, request }, cancellationToken);

    [McpServerTool(Name = "prepare_update_activity_metric", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Update an existing manual metric. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> UpdateActivityMetric(Guid profileId, Guid activityId, Guid metricId, ActivityMetricRequest request, CancellationToken cancellationToken) =>
        Prepare("update_activity_metric", profileId, new { activityId, metricId, request }, cancellationToken);

    [McpServerTool(Name = "prepare_delete_activity_metric", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Delete an existing manual metric. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> DeleteActivityMetric(Guid profileId, Guid activityId, Guid metricId, CancellationToken cancellationToken) =>
        Prepare("delete_activity_metric", profileId, new { activityId, metricId }, cancellationToken);

    [McpServerTool(Name = "prepare_create_tag", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Create a tag for the selected profile. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> CreateTag(Guid profileId, string name, CancellationToken cancellationToken) =>
        Prepare("create_tag", profileId, new { name }, cancellationToken);

    [McpServerTool(Name = "prepare_rename_tag", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Rename a tag and update saved references. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> RenameTag(Guid profileId, Guid tagId, string name, CancellationToken cancellationToken) =>
        Prepare("rename_tag", profileId, new { tagId, name }, cancellationToken);

    [McpServerTool(Name = "prepare_delete_tag", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Delete a tag and its assignments; saved searches may require repair. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> DeleteTag(Guid profileId, Guid tagId, CancellationToken cancellationToken) =>
        Prepare("delete_tag", profileId, new { tagId }, cancellationToken);

    [McpServerTool(Name = "prepare_set_activity_tags", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Replace the activity's tag assignments with the reviewed exact set. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> SetActivityTags(Guid profileId, Guid activityId, Guid[] tagIds, CancellationToken cancellationToken) =>
        Prepare("set_activity_tags", profileId, new { activityId, tagIds }, cancellationToken);

    [McpServerTool(Name = "prepare_apply_activity_batch", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Apply tag and gear edits to an explicit frozen activity selection. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> ApplyActivityBatch(Guid profileId, Guid[] activityIds, BatchChangeRequest changes, CancellationToken cancellationToken) =>
        Prepare("apply_activity_batch", profileId, new { activityIds, changes }, cancellationToken);

    [McpServerTool(Name = "prepare_save_saved_search", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Create or replace a saved search; id selects an existing search to replace. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> SaveSavedSearch(Guid profileId, string name, SavedSearchCriteria criteria, CancellationToken cancellationToken, Guid? id = null) =>
        Prepare("save_saved_search", profileId, new { name, criteria, id }, cancellationToken);

    [McpServerTool(Name = "prepare_rename_saved_search", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Rename an existing saved search. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> RenameSavedSearch(Guid profileId, Guid savedSearchId, string name, CancellationToken cancellationToken) =>
        Prepare("rename_saved_search", profileId, new { savedSearchId, name }, cancellationToken);

    [McpServerTool(Name = "prepare_delete_saved_search", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Delete an existing saved search. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> DeleteSavedSearch(Guid profileId, Guid savedSearchId, CancellationToken cancellationToken) =>
        Prepare("delete_saved_search", profileId, new { savedSearchId }, cancellationToken);

    [McpServerTool(Name = "prepare_create_route_from_activity", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Create a saved route from activity GPS geometry. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> CreateRouteFromActivity(Guid profileId, Guid activityId, string name, CancellationToken cancellationToken, string? description = null) =>
        Prepare("create_route_from_activity", profileId, new { activityId, name, description }, cancellationToken);

    [McpServerTool(Name = "prepare_create_route_path", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Create a saved route from explicitly supplied path points. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> CreateRoutePath(Guid profileId, string name, SportKind sport, TrackPoint[] points, CancellationToken cancellationToken, string? description = null) =>
        Prepare("create_route_path", profileId, new { name, sport, points, description }, cancellationToken);

    [McpServerTool(Name = "prepare_import_route", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Import a Web-approved GPX asset as a route with retained provenance. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> ImportRoute(Guid profileId, Guid assetId, string name, SportKind sport, CancellationToken cancellationToken, string? description = null) =>
        Prepare("import_route", profileId, new { assetId, name, sport, description }, cancellationToken);

    [McpServerTool(Name = "prepare_import_segment", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Import a Web-approved path file as a segment. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> ImportSegment(Guid profileId, Guid assetId, string name, SportKind sport, CancellationToken cancellationToken, double toleranceMeters = 30) =>
        Prepare("import_segment", profileId, new { assetId, name, sport, toleranceMeters }, cancellationToken);

    [McpServerTool(Name = "prepare_import_activities", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Preview supported activity-file or archive import from a Web-approved asset. Actual import is incremental. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> ImportActivities(Guid profileId, Guid assetId, SourceKind sourceKind, CancellationToken cancellationToken) =>
        Prepare("import_activities", profileId, new { assetId, sourceKind }, cancellationToken);

    [McpServerTool(Name = "prepare_create_goal", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Create a goal; request.ownerId must match profileId. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> CreateGoal(Guid profileId, CreateGoalRequest request, CancellationToken cancellationToken) =>
        Prepare("create_goal", profileId, new { request }, cancellationToken);

    [McpServerTool(Name = "prepare_edit_goal", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Edit the specified goal edition scope using its expected version and state. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> EditGoal(Guid profileId, EditGoalRequest request, CancellationToken cancellationToken) =>
        Prepare("edit_goal", profileId, new { goalId = request.GoalId, request }, cancellationToken);

    [McpServerTool(Name = "prepare_archive_goal", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Archive future goal editions while preserving history. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> ArchiveGoal(Guid profileId, ArchiveGoalRequest request, CancellationToken cancellationToken) =>
        Prepare("archive_goal", profileId, new { goalId = request.GoalId, request }, cancellationToken);

    [McpServerTool(Name = "prepare_create_profile", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Create a profile. New profiles require a separate permission grant before use. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> CreateProfile(string name, CancellationToken cancellationToken) =>
        Prepare("create_profile", null, new { name }, cancellationToken);

    [McpServerTool(Name = "prepare_update_profile_timezone", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Change the profile's reporting timezone. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> UpdateProfileTimezone(Guid profileId, string timeZoneId, CancellationToken cancellationToken) =>
        Prepare("update_profile_timezone", profileId, new { timeZoneId }, cancellationToken);

    [McpServerTool(Name = "prepare_delete_profile", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Delete the profile's records and eligible originals. Requires exact typed confirmation. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> DeleteProfile(Guid profileId, CancellationToken cancellationToken) =>
        Prepare("delete_profile", profileId, new { }, cancellationToken);

    [McpServerTool(Name = "prepare_add_watched_folder", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Authorize ongoing imports from a folder previously selected in the Web app. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> AddWatchedFolder(Guid profileId, Guid assetId, CancellationToken cancellationToken) =>
        Prepare("add_watched_folder", profileId, new { assetId }, cancellationToken);

    [McpServerTool(Name = "prepare_remove_watched_folder", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Stop watching a folder; retain imported activities and source files. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> RemoveWatchedFolder(Guid profileId, Guid folderId, CancellationToken cancellationToken) =>
        Prepare("remove_watched_folder", profileId, new { folderId }, cancellationToken);

    [McpServerTool(Name = "prepare_recompute_statistics", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Recompute persisted records for the selected profile. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> RecomputeStatistics(Guid profileId, CancellationToken cancellationToken) =>
        Prepare("recompute_statistics", profileId, new { }, cancellationToken);

    [McpServerTool(Name = "prepare_rebuild_exploration", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Build exploration indexes, optionally rechecking limited sources. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> RebuildExploration(Guid profileId, CancellationToken cancellationToken, bool recheckLimited = false) =>
        Prepare("rebuild_exploration", profileId, new { recheckLimited }, cancellationToken);

    [McpServerTool(Name = "prepare_set_map_privacy", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Change global map mode; OpenFreeMap enables browser requests for external tiles. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> SetMapPrivacy(MapPrivacyMode mode, CancellationToken cancellationToken) =>
        Prepare("set_map_privacy", null, new { mode }, cancellationToken);

    [McpServerTool(Name = "prepare_export_profile", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Prepare a Web profile download without returning the export contents to the model. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> ExportProfile(Guid profileId, CancellationToken cancellationToken) =>
        Prepare("export_profile", profileId, new { }, cancellationToken);

    [McpServerTool(Name = "prepare_export_route", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Prepare a Web GPX download without returning GPS coordinates to the model. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> ExportRoute(Guid profileId, Guid routeId, CancellationToken cancellationToken) =>
        Prepare("export_route", profileId, new { routeId }, cancellationToken);

    [McpServerTool(Name = "prepare_download_original", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Dry-run only: Prepare a Web download of an existing retained original. Returns an expiring immutable preview; does not apply the change.")]
    public Task<CallToolResult> DownloadOriginal(Guid profileId, Guid sourceFileId, CancellationToken cancellationToken) =>
        Prepare("download_original", profileId, new { sourceFileId }, cancellationToken);

}
