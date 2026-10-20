using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Mcp.Models;

public sealed record ToolError(string Code, string Message, bool Retryable = false);
public sealed record ToolEnvelope<T>(T? Data, ToolError? Error, DateTimeOffset AsOfUtc);
public sealed record ServerStatus(string ApplicationVersion, string Transport, string AccessMode, bool DatabaseReady,
    int MaxPageSize, int MaxPage, int MaxResponseBytes, int RequestTimeoutSeconds);
public sealed record DashboardResult(ReportingTotals Totals, ReportingTotals? ComparisonTotals, string? ComparisonUnavailableReason,
    IReadOnlyList<ResolvedOwnerPeriod> Periods, IReadOnlyList<ResolvedOwnerPeriod> ComparisonPeriods,
    IReadOnlyList<ReportingBucket> Trend, IReadOnlyList<SportTotal> Sports);

public sealed record DateSelection(string Period = "all-time", DateOnly? From = null, DateOnly? To = null)
{
    public ReportingDateSelection Resolve()
    {
        var selection = new ReportingDateSelection(ReportingDates.FromSlug(Period), From, To);
        if (selection.Preset != ReportingPreset.Custom && (From.HasValue || To.HasValue))
            throw new ArgumentException("From and To require the custom reporting period.");
        ReportingDates.Validate(selection);
        return selection;
    }
}
