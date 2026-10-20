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
public sealed class RouteTools(ToolExecutionPolicy execution, IRouteReadService routes)
{
    [McpServerTool(Name = "search_routes", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<PagedResult<RouteSummary>>))]
    [Description("Search saved routes for one profile with bounded pagination. Distances and elevation are in metres.")]
    public Task<CallToolResult> SearchRoutes(Guid profileId, CancellationToken cancellationToken, string? search = null, SportKind? sport = null, string sort = "name-asc", int page = 1, int pageSize = 25) =>
        execution.RunAsync<PagedResult<RouteSummary>>("search_routes", profileId, async (_, token) => await routes.SearchAsync(new(profileId, search, sport, sort), new(page, pageSize), token), cancellationToken);

    [McpServerTool(Name = "get_route", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<RouteReadDetail>))]
    [Description("Read scalar route measurements and description without geometry or source files.")]
    public Task<CallToolResult> GetRoute(Guid profileId, Guid routeId, CancellationToken cancellationToken) =>
        execution.RunAsync<RouteReadDetail>("get_route", profileId, async (_, token) => await routes.GetAsync(profileId, routeId, token), cancellationToken);

}
