using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Core.Contracts;

public interface IProfileReadService
{
    Task<bool> ExistsAsync(Guid ownerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProfileSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<PagedResult<ProfileSummary>> SearchAsync(ReadPage page, IReadOnlyList<Guid>? allowedProfiles = null, CancellationToken cancellationToken = default);
}

public interface IActivityReadService
{
    Task<ActivityFilterOptions> GetFilterOptionsAsync(Guid? ownerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<SportKind, int>> GetSportActivityCountsAsync(Guid? ownerId, CancellationToken cancellationToken = default);
    Task<ActivitySearchResult> SearchAsync(ActivityFilter filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> GetMatchingActivityIdsAsync(ActivityFilter filter, CancellationToken cancellationToken = default);
    Task<DashboardSummary> GetDashboardAsync(DashboardQuery query, CancellationToken cancellationToken = default);
    Task<DashboardSummary> GetBoundedDashboardAsync(DashboardQuery query, int maximumBuckets, CancellationToken cancellationToken = default);
    Task<TrainingCalendarSummary> GetTrainingCalendarAsync(TrainingCalendarQuery query, CancellationToken cancellationToken = default);
    Task<ActivityReadDetail?> GetDetailAsync(Guid ownerId, Guid activityId, CancellationToken cancellationToken = default);
    Task<PagedResult<FilterValue>> ListFilterValuesAsync(Guid ownerId, string kind, string? search, ReadPage page, CancellationToken cancellationToken = default);
}

public interface IStatisticsReadService
{
    Task<IReadOnlyList<PersonalRecord>> GetRecordsAsync(Guid? ownerId, RecordScope scope = RecordScope.All, CancellationToken cancellationToken = default);
    Task<RecordOverviewResult> GetOverviewAsync(RecordOverviewQuery query, CancellationToken cancellationToken = default);
    Task<RecordAttemptPage?> GetAttemptsAsync(RecordAttemptQuery query, CancellationToken cancellationToken = default);
    Task<RecordHistory?> GetHistoryAsync(RecordHistoryQuery query, CancellationToken cancellationToken = default);
    IReadOnlyList<RecordBenchmark> GetBenchmarks(SportKind sport);
    Task<ReadRecordOverview> ReadOverviewAsync(RecordOverviewQuery query, CancellationToken cancellationToken = default);
}

public interface ISegmentReadService
{
    Task<IReadOnlyList<SegmentSummary>> ListAsync(Guid? ownerId, CancellationToken cancellationToken = default);
    Task<PagedResult<SegmentSummary>> SearchAsync(Guid ownerId, string? search, SportKind? sport, string sort, ReadPage page, CancellationToken cancellationToken = default);
    Task<SegmentReadDetail?> GetAsync(Guid ownerId, Guid segmentId, CancellationToken cancellationToken = default);
    Task<PagedResult<SegmentEffortSummary>?> GetEffortsAsync(Guid ownerId, Guid segmentId, SegmentEffortFilter filter, ReadPage page, CancellationToken cancellationToken = default);
    Task<SegmentReadComparison?> CompareAsync(Guid ownerId, Guid segmentId, Guid baselineId, Guid comparisonId, CancellationToken cancellationToken = default);
}

public interface IRouteReadService
{
    Task<IReadOnlyList<RouteSummary>> SearchAsync(RouteFilter filter, CancellationToken cancellationToken = default);
    Task<PagedResult<RouteSummary>> SearchAsync(RouteFilter filter, ReadPage page, CancellationToken cancellationToken = default);
    Task<RouteReadDetail?> GetAsync(Guid ownerId, Guid routeId, CancellationToken cancellationToken = default);
}
