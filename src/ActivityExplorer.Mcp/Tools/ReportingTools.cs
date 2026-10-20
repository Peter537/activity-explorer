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
public sealed class ReportingTools(ToolExecutionPolicy execution, IActivityReadService activities, IStatisticsReadService statistics)
{
    [McpServerTool(Name = "get_dashboard", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<DashboardResult>))]
    [Description("Read training totals, sport totals, comparison and at most 120 trend buckets. Dates use the profile timezone. Defaults to year-to-date and monthly trends.")]
    public Task<CallToolResult> GetDashboard(Guid profileId, CancellationToken cancellationToken, ActivityCriteria? criteria = null, DateSelection? dates = null, PeriodComparison comparison = PeriodComparison.PriorYear, TrendGranularity granularity = TrendGranularity.Monthly) =>
        execution.RunAsync<DashboardResult>("get_dashboard", profileId, async (asOf, token) =>
        {
            var result = await activities.GetBoundedDashboardAsync(new(OwnerId: profileId, Period: (dates ?? new("year-to-date")).Resolve(),
                Comparison: comparison, Granularity: granularity, AsOfUtc: asOf, Criteria: criteria), 120, token);
            return new(result.Totals, result.ComparisonTotals, result.ComparisonUnavailableReason, result.Periods, result.ComparisonPeriods, result.Trend, result.Sports);
        }, cancellationToken);

    [McpServerTool(Name = "get_training_calendar", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<TrainingCalendarSummary>))]
    [Description("Read one month of daily and weekly training summaries in the profile timezone. Defaults to the current local month.")]
    public Task<CallToolResult> GetTrainingCalendar(Guid profileId, CancellationToken cancellationToken, DateOnly? month = null, ActivityCriteria? criteria = null) =>
        execution.RunAsync<TrainingCalendarSummary>("get_training_calendar", profileId, async (asOf, token) => await activities.GetTrainingCalendarAsync(new(OwnerId: profileId, Month: month, AsOfUtc: asOf, Criteria: criteria), token), cancellationToken);

    [McpServerTool(Name = "get_records", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<ReadRecordOverview>))]
    [Description("Read benchmark definitions and achieved records for one sport. All-time results identify persisted snapshots and availability; date-limited results are calculated without persistence.")]
    public Task<CallToolResult> GetRecords(Guid profileId, SportKind sport, CancellationToken cancellationToken, DateSelection? dates = null, RecordScope scope = RecordScope.All) =>
        execution.RunAsync<ReadRecordOverview>("get_records", profileId, async (asOf, token) => await statistics.ReadOverviewAsync(new(profileId, sport, scope, (dates ?? new()).Resolve(), asOf), token), cancellationToken);

    [McpServerTool(Name = "get_record_attempts", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<RecordAttemptPage>))]
    [Description("Read ranked attempts for a benchmark kind and key returned by get_records. Pages contain 50 attempts. Results preserve coverage and recorded interval information.")]
    public Task<CallToolResult> GetRecordAttempts(Guid profileId, SportKind sport, RecordKind kind, string key, CancellationToken cancellationToken, DateSelection? dates = null, RecordScope scope = RecordScope.All, bool multiplePerActivity = false, int page = 1) =>
        execution.RunAsync<RecordAttemptPage>("get_record_attempts", profileId, async (asOf, token) =>
        {
            _ = new ReadPage(page, 50).Offset;
            if (!Enum.IsDefined(sport) || !Enum.IsDefined(kind) || !Enum.IsDefined(scope) || !statistics.GetBenchmarks(sport).Any(benchmark => benchmark.Kind == kind && benchmark.Key == key))
                throw new ArgumentException("Choose a benchmark kind and key returned by get_records.");
            return await statistics.GetAttemptsAsync(new(sport, kind, key, profileId, scope, multiplePerActivity, page, (dates ?? new()).Resolve(), asOf), token);
        }, cancellationToken);

}
