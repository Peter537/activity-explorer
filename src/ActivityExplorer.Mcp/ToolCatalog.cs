using System.Collections.Frozen;
using System.Reflection;
using ActivityExplorer.Mcp.Tools;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Mcp.Configuration;
using ModelContextProtocol.Server;

namespace ActivityExplorer.Mcp;

public static class ToolCatalog
{
    public static bool IsAvailable(string name, McpOptions options)
    {
        if (Names.Contains(name)) return true;
        if (!options.WritesEnabled) return false;
        if (name is "get_write_context" or "get_prepared_change" or "get_operation_status" or "apply_prepared_change") return true;
        if (!name.StartsWith("prepare_", StringComparison.Ordinal)) return false;
        var action = name[8..];
        return McpChangeCatalog.Actions.Contains(action) && (!McpChangeCatalog.Destructive(action) || options.AllowDestructive)
            && (!McpChangeCatalog.Administration(action) || options.AllowAdministration);
    }
    public static IReadOnlySet<string> Names { get; } = new[]
    {
        "get_server_status", "list_profiles", "list_activity_filter_values", "search_activities", "get_activity",
        "get_dashboard", "get_training_calendar", "get_records", "get_record_attempts", "search_segments",
        "get_segment", "list_segment_efforts", "compare_segment_efforts", "search_routes", "get_route"
    }.ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> Analyses { get; } = new[] { "get_records", "get_record_attempts", "compare_segment_efforts" }.ToFrozenSet(StringComparer.Ordinal);
    public static IReadOnlyList<Type> ToolTypes { get; } = Array.AsReadOnly<Type>([typeof(ActivityTools), typeof(ReportingTools), typeof(SegmentTools), typeof(RouteTools)]);

    public static void Validate()
    {
        var attributes = ToolTypes.SelectMany(type => type.GetMethods()).Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute is not null).Select(attribute => attribute!).ToArray();
        if (attributes.Length != Names.Count || !Names.SetEquals(attributes.Select(attribute => attribute.Name!)) ||
            attributes.Any(attribute => !attribute.ReadOnly || attribute.Destructive || !attribute.Idempotent || attribute.OpenWorld || !attribute.UseStructuredContent || attribute.OutputSchemaType is null))
            throw new InvalidOperationException("The registered MCP tools do not match the approved read-only catalogue.");
    }
}
