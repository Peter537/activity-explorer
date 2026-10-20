using System.ComponentModel;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Mcp.Configuration;
using ActivityExplorer.Mcp.Models;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ActivityExplorer.Mcp.Tools;

[McpServerToolType]
public sealed class ActivityTools(ToolExecutionPolicy execution, IActivityReadService activities, IProfileReadService profiles, McpOptions options)
{
    [McpServerTool(Name = "get_server_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<ServerStatus>))]
    [Description("Read server version, configured access mode, database readiness and execution limits. ReadWrite capabilities also require the matching running Web app.")]
    public Task<CallToolResult> GetServerStatus(CancellationToken cancellationToken) =>
        execution.RunAsync<ServerStatus>("get_server_status", null, (_, _) => Task.FromResult<ServerStatus?>(new(McpHost.ApplicationVersion, "stdio", options.WritesEnabled ? "ReadWrite" : "ReadOnly", true, 100, 1000, options.MaxResponseBytes, options.RequestTimeoutSeconds)), cancellationToken);

    [McpServerTool(Name = "list_profiles", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<PagedResult<ProfileSummary>>))]
    [Description("List permitted local profiles and their reporting timezones. Choose a profile ID for subsequent calls.")]
    public Task<CallToolResult> ListProfiles(CancellationToken cancellationToken, int page = 1, int pageSize = 25) =>
        execution.RunAsync<PagedResult<ProfileSummary>>("list_profiles", null, async (_, token) => await profiles.SearchAsync(new(page, pageSize), options.AllowedProfileIds, token), cancellationToken);

    [McpServerTool(Name = "list_activity_filter_values", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<PagedResult<FilterValue>>))]
    [Description("List device, gear, or tag filter values for one profile. Tag values include IDs.")]
    public Task<CallToolResult> ListActivityFilterValues(Guid profileId, string kind, CancellationToken cancellationToken, string? search = null, int page = 1, int pageSize = 25) =>
        execution.RunAsync<PagedResult<FilterValue>>("list_activity_filter_values", profileId, async (_, token) => await activities.ListFilterValuesAsync(profileId, kind, search, new(page, pageSize), token), cancellationToken);

    [McpServerTool(Name = "search_activities", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<ActivitySearchResult>))]
    [Description("Search one profile using activity criteria and owner-local dates. Totals cover all matches, independently of the returned page. Distances are metres and durations are seconds.")]
    public Task<CallToolResult> SearchActivities(Guid profileId, CancellationToken cancellationToken, ActivityCriteria? criteria = null, DateSelection? dates = null, string sort = "start-desc", int page = 1, int pageSize = 25) =>
        execution.RunAsync<ActivitySearchResult>("search_activities", profileId, async (asOf, token) =>
        {
            _ = new ReadPage(page, pageSize).Offset;
            if (sort is not ("start-desc" or "start-asc" or "distance-desc" or "distance-asc" or "duration-desc" or "elevation-desc")) throw new ArgumentException("Choose a valid activity order.");
            var period = (dates ?? new()).Resolve();
            return await activities.SearchAsync(new(OwnerId: profileId, Page: page, PageSize: pageSize, Sort: sort, Period: period.Preset,
                From: period.From, To: period.To, AsOfUtc: asOf, Criteria: criteria), token);
        }, cancellationToken);

    [McpServerTool(Name = "get_activity", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<ActivityReadDetail>))]
    [Description("Read scalar activity measurements, tags and related collection counts. Full tracks, laps, metrics and original files are not returned.")]
    public Task<CallToolResult> GetActivity(Guid profileId, Guid activityId, CancellationToken cancellationToken) =>
        execution.RunAsync<ActivityReadDetail>("get_activity", profileId, async (_, token) => await activities.GetDetailAsync(profileId, activityId, token), cancellationToken);

}
