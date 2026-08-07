using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Core.Contracts;

public interface IAppDataPaths
{
    string Root { get; }
    string DatabasePath { get; }
    string OriginalsPath { get; }
    string StagingPath { get; }
    string LogsPath { get; }
    string QuarantinePath { get; }
    void EnsureCreated();
    string GetOwnerOriginalsPath(Guid ownerId);
}

public interface IMapSettingsService
{
    Task<MapPrivacyMode> GetModeAsync(CancellationToken cancellationToken = default);
    Task SetModeAsync(MapPrivacyMode mode, CancellationToken cancellationToken = default);
}

public interface IOwnerMutationLock
{
    ValueTask<IAsyncDisposable> AcquireAsync(IEnumerable<Guid> ownerIds, CancellationToken cancellationToken = default);
}

public sealed record PreparedFileOperation(Guid OperationId, string TargetRelativePath);

public interface IFileOperationCoordinator
{
    Task<PreparedFileOperation> PrepareCopyAsync(
        Guid ownerId, Guid? entityId, string sourcePath, string targetPath, string expectedSha256,
        bool deleteSourceOnCommit = false, CancellationToken cancellationToken = default);
    Task<Guid?> QuarantineOwnerAsync(Guid ownerId, CancellationToken cancellationToken = default);
    Task<Guid?> QuarantineFileAsync(
        Guid ownerId, Guid? entityId, string storedPath, CancellationToken cancellationToken = default);
    Task CommitAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task RollbackAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task RecoverAsync(CancellationToken cancellationToken = default);
}

public interface IOriginalStore
{
    string ResolveStoredPath(string storedPath);
    string ToStoredPath(string fullPath);
    string GetOriginalTarget(Guid ownerId, string sha256, string extension);
}

public interface IActivityImporter
{
    string Name { get; }
    bool CanImport(string path);
    Task<IReadOnlyList<ImportCandidate>> ReadAsync(string path, SourceKind sourceKind, CancellationToken cancellationToken = default);
}
public interface IImporterDiagnosticsSource
{
    ImporterDiagnostics ConsumeDiagnostics();
}


public interface IImportQueue
{
    ValueTask<Guid> EnqueueAsync(ImportRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ImportProgress> WatchAsync(Guid importId, CancellationToken cancellationToken = default);
}

public interface IImportProcessor
{
    Task ProcessAsync(Guid importBatchId, CancellationToken cancellationToken = default);
}

public interface IActivityQueryService
{
    Task<PagedResult<ActivitySummary>> SearchAsync(ActivityFilter filter, CancellationToken cancellationToken = default);
    Task<ActivityDetail?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DashboardSummary> GetDashboardAsync(Guid? ownerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> GetMatchingActivityIdsAsync(ActivityFilter filter, CancellationToken cancellationToken = default);
    Task<ActivityDeletionResult> DeleteAsync(
        IReadOnlyCollection<Guid> activityIds, CancellationToken cancellationToken = default);
    Task UpdateAsync(Guid id, UpdateActivityRequest request, CancellationToken cancellationToken = default);
    Task<Guid> AddMetricAsync(Guid activityId, ActivityMetricRequest request, CancellationToken cancellationToken = default);
    Task UpdateMetricAsync(Guid activityId, Guid metricId, ActivityMetricRequest request, CancellationToken cancellationToken = default);
    Task DeleteMetricAsync(Guid activityId, Guid metricId, CancellationToken cancellationToken = default);
}

public interface ISegmentMatcher
{
    Task<IReadOnlyList<SegmentMatch>> MatchAsync(
        IReadOnlyList<TrackPoint> activity,
        IReadOnlyList<TrackPoint> segment,
        double toleranceMeters,
        CancellationToken cancellationToken = default);
}

public interface ISegmentService
{
    Task<IReadOnlyList<SegmentSummary>> ListAsync(Guid? ownerId, CancellationToken cancellationToken = default);
    Task<SegmentDetail?> GetAsync(Guid id, Guid? effortId = null, CancellationToken cancellationToken = default);
    Task<Guid> CreateFromActivityAsync(CreateSegmentRequest request, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(CreateSegmentPathRequest request, CancellationToken cancellationToken = default);
    Task RecomputeAsync(Guid segmentId, CancellationToken cancellationToken = default);
}

public interface IRouteService
{
    Task<IReadOnlyList<RouteSummary>> ListAsync(Guid? ownerId, CancellationToken cancellationToken = default);
    Task<RouteDetail?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Guid> CreateFromActivityAsync(CreateRouteRequest request, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(CreateRoutePathRequest request, CancellationToken cancellationToken = default);
    Task<Guid> ImportGpxAsync(CreateRoutePathRequest request, string stagedPath, string originalName, string sha256, long length, CancellationToken cancellationToken = default);
    Task<string?> ExportGpxAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IStatisticsService
{
    Task RecomputeAsync(Guid ownerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PersonalRecord>> GetRecordsAsync(
        Guid? ownerId, RecordScope scope = RecordScope.All, CancellationToken cancellationToken = default);
}

public interface IMapFeatureService
{
    Task<MapFeatureCollection> GetActivitiesAsync(MapQuery query, CancellationToken cancellationToken = default);
    Task<MapFeatureCollection> GetRoutesAsync(MapQuery query, CancellationToken cancellationToken = default);
    Task<MapFeatureCollection> GetSegmentsAsync(MapQuery query, CancellationToken cancellationToken = default);
}
