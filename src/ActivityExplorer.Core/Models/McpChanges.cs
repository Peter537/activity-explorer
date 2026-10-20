using System.Text.Json;
using System.Text.Json.Serialization;

namespace ActivityExplorer.Core.Models;

public sealed record McpChangeRequest(string Action, Guid? ProfileId, JsonElement Arguments);
public sealed record McpConnection(string Endpoint, string Token, string DataIdentity, Guid[] ProfileIds,
    bool AllowDestructive, bool AllowAdministration, int Version = 1);
public sealed record McpChangePreview(Guid PlanId, string Action, Guid[] ProfileIds, string Summary,
    JsonElement Details, DateTimeOffset ExpiresAt, string? ConfirmationPhrase, string ReviewUrl,
    string Status = "prepared");
public sealed record McpOperationStatus(Guid OperationId, string Action, string Status, Guid? EntityId,
    JsonElement? Result, string? Message, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, Guid[] ProfileIds);
public sealed record McpApplyResult(string Status, Guid PlanId, string? ReviewUrl = null, McpOperationStatus? Operation = null);
public sealed record McpApprovedAsset(Guid Id, Guid ProfileId, string Name, string Kind, long Length, DateTimeOffset ExpiresAt);
public sealed record McpWriteContext(string Kind, IReadOnlyList<JsonElement> Items, int Total, int Page, int PageSize);
public sealed record McpApplyRequest(string? ConfirmationPhrase = null);
public sealed record McpReadContextRequest(Guid ProfileId, string Kind, Guid? EntityId = null, int Page = 1, int PageSize = 25);

public static class McpChangeJson
{
    public static JsonSerializerOptions Options { get; } = Create();
    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 32
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}

public static class McpChangeCatalog
{
    public static IReadOnlySet<string> Actions { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "create_segment_from_activity", "create_segment_from_route", "create_segment_path", "create_subsegment",
        "attach_segment", "detach_segment", "recompute_segment", "update_activity", "transfer_activity", "delete_activities",
        "add_activity_metric", "update_activity_metric", "delete_activity_metric", "create_tag", "rename_tag", "delete_tag",
        "set_activity_tags", "apply_activity_batch", "save_saved_search", "rename_saved_search", "delete_saved_search",
        "create_route_from_activity", "create_route_path", "import_route", "import_segment", "import_activities",
        "create_goal", "edit_goal", "archive_goal", "create_profile", "update_profile_timezone", "delete_profile",
        "add_watched_folder", "remove_watched_folder", "recompute_statistics", "rebuild_exploration", "set_map_privacy",
        "export_profile", "export_route", "download_original"
    };
    public static bool Destructive(string action) => action.StartsWith("delete_", StringComparison.Ordinal)
        || action is "detach_segment" or "remove_watched_folder";
    public static bool Administration(string action) => action is "create_profile" or "delete_profile" or "set_map_privacy";
}
