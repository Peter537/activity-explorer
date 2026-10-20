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
public sealed class SegmentTools(ToolExecutionPolicy execution, ISegmentReadService segments)
{
    [McpServerTool(Name = "search_segments", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<PagedResult<SegmentSummary>>))]
    [Description("Search saved segment definitions for one profile. Returns matching rows with effort counts and best elapsed seconds.")]
    public Task<CallToolResult> SearchSegments(Guid profileId, CancellationToken cancellationToken, string? search = null, SportKind? sport = null, string sort = "name-asc", int page = 1, int pageSize = 25) =>
        execution.RunAsync<PagedResult<SegmentSummary>>("search_segments", profileId, async (_, token) => await segments.SearchAsync(profileId, search, sport, sort, new(page, pageSize), token), cancellationToken);

    [McpServerTool(Name = "get_segment", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<SegmentReadDetail>))]
    [Description("Read a segment definition summary, relationship counts and effort metric calculation status without geometry or tracks.")]
    public Task<CallToolResult> GetSegment(Guid profileId, Guid segmentId, CancellationToken cancellationToken) =>
        execution.RunAsync<SegmentReadDetail>("get_segment", profileId, async (_, token) => await segments.GetAsync(profileId, segmentId, token), cancellationToken);

    [McpServerTool(Name = "list_segment_efforts", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<PagedResult<SegmentEffortSummary>>))]
    [Description("List segment efforts with owner-local date filters, elapsed bounds in seconds, and rank/date/elapsed/speed/heart-rate/power ordering.")]
    public Task<CallToolResult> ListSegmentEfforts(Guid profileId, Guid segmentId, CancellationToken cancellationToken, SegmentEffortFilter? filter = null, int page = 1, int pageSize = 25) =>
        execution.RunAsync<PagedResult<SegmentEffortSummary>>("list_segment_efforts", profileId, async (_, token) => await segments.GetEffortsAsync(profileId, segmentId, filter ?? new(), new(page, pageSize), token), cancellationToken);

    [McpServerTool(Name = "compare_segment_efforts", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<SegmentReadComparison>))]
    [Description("Compare two explicit efforts from the same segment and profile. Returns elapsed deltas and at most 100 distance-aligned timing samples, without GPS coordinates. Unavailable alignments have explicit reasons.")]
    public Task<CallToolResult> CompareSegmentEfforts(Guid profileId, Guid segmentId, Guid baselineEffortId, Guid comparisonEffortId, CancellationToken cancellationToken) =>
        execution.RunAsync<SegmentReadComparison>("compare_segment_efforts", profileId, async (_, token) => await segments.CompareAsync(profileId, segmentId, baselineEffortId, comparisonEffortId, token), cancellationToken);

}
