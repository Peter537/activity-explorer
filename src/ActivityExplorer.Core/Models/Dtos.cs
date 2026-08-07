using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Core.Models;

public sealed record ImportRequest(Guid OwnerId, string StagedPath, string DisplayName, SourceKind SourceKind);
public sealed record ImportProgress(Guid ImportId, ImportStatus Status, int FilesDiscovered, int Created, int Updated, int Skipped, string? Message);
public sealed record ImporterDiagnostics(int Unsupported, int Warnings, string? Summary);
public sealed record ImportReport(Guid ImportId, ImportStatus Status, int Created, int Updated, int Duplicates, int Unsupported, int Warnings, string? Summary);

public sealed record ActivityFilter(
    Guid? OwnerId = null, SportKind? Sport = null, DateOnly? From = null, DateOnly? To = null,
    string? Search = null, bool? HasPower = null, string? Device = null,
    int Page = 1, int PageSize = 25, string Sort = "start-desc");

public sealed record ActivityDeletionResult(
    int DeletedCount, bool FileCleanupPending, bool StatisticsRefreshPending);

public sealed record ActivitySummary(
    Guid Id, Guid OwnerId, string OwnerName, string Title, SportKind Sport, DateTimeOffset StartTime,
    double DistanceMeters, double MovingSeconds, double ElevationMeters, double? AveragePowerWatts,
    string? DeviceName, bool HasGps, bool HasPower);

public sealed record ActivityDetail(
    ActivitySummary Summary, string? Description, string? GearName, double ElapsedSeconds, double? Calories,
    double? AverageSpeed, double? MaxSpeed, double? AverageHeartRate, double? MaxHeartRate,
    double? AverageCadence, double? MaxPower, double? Kilojoules,
    IReadOnlyList<TrackPoint> Points, IReadOnlyList<LapCandidate> Laps,
    IReadOnlyList<SourceFileSummary> Sources, IReadOnlyList<SegmentEffortSummary> SegmentEfforts,
    ActivityRichSummary Rich, IReadOnlyList<ActivityMetricSummary> Metrics);

public sealed record ActivityRichSummary(
    double? TimerSeconds, MovingTimeSource MovingTimeSource,
    double? ElevationLoss, double? MinElevation, double? MaxElevation,
    double? RestingCalories, double? ActiveCalories,
    double? MaxCadence, double? PedalRevolutions,
    double? AverageTemperature, double? MinTemperature, double? MaxTemperature,
    double? AverageRespiration, double? MinRespiration, double? MaxRespiration,
    double? AerobicTrainingEffect, double? AnaerobicTrainingEffect, double? TrainingLoad);

public sealed record ActivityMetricSummary(
    Guid Id, string Key, string Label, double? NumericValue, string? TextValue,
    string? Unit, ActivityMetricOrigin Origin, DateTimeOffset UpdatedAt);
public sealed record ActivityMetricRequest(string Key, string Label, double? NumericValue, string? TextValue, string? Unit);
public sealed record SourceFileSummary(
    Guid Id, string Name, SourceKind SourceKind, long Length, DateTimeOffset ImportedAt,
    SourceProvider Provider, AcquisitionMethod AcquisitionMethod, string? ExternalActivityId, int ParserVersion);
public sealed record SegmentEffortSummary(
    Guid Id, Guid ActivityId, Guid SegmentId, string SegmentName, double ElapsedSeconds, int Rank, DateTimeOffset StartTime, int StartPointIndex, int EndPointIndex,
    double MovingSeconds, double? AverageSpeed, double? MaxSpeed,
    double? AverageHeartRate, double? MaxHeartRate, double? AverageCadence, double? MaxCadence,
    double? AveragePower, double? MaxPower, double? AverageTemperature, double? AverageRespiration,
    double? ElevationGain, double? ElevationLoss, double? AverageGrade, double CoveragePercent);
public sealed record UpdateActivityRequest(string Title, string? Description, string? GearName, Guid OwnerId);

public sealed record DashboardSummary(
    int ActivityCount, double DistanceMeters, double MovingSeconds, double ElevationMeters,
    IReadOnlyList<SportTotal> Sports, IReadOnlyList<ActivitySummary> Recent,
    IReadOnlyList<PersonalRecord> Highlights, int ImportWarnings,
    IReadOnlyList<PeriodTotal> MonthlyTrend, IReadOnlyList<NamedTotal> Devices, IReadOnlyList<NamedTotal> Gear);

public sealed record SportTotal(SportKind Sport, int Count, double DistanceMeters, double MovingSeconds, double ElevationMeters);
public sealed record PeriodTotal(DateOnly Period, int Count, double DistanceMeters, double MovingSeconds);
public sealed record NamedTotal(string Name, int Count);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int TotalPages => Total == 0 ? 1 : (int)Math.Ceiling(Total / (double)PageSize);
}

public sealed record MapQuery(
    Guid? OwnerId = null, SportKind? Sport = null, DateOnly? From = null, DateOnly? To = null,
    double? West = null, double? South = null, double? East = null, double? North = null, int Zoom = 8);

public sealed record MapFeatureCollection(string Type, IReadOnlyList<MapFeature> Features)
{
    public static MapFeatureCollection Empty { get; } = new("FeatureCollection", []);
}

public sealed record MapFeature(string Type, MapGeometry Geometry, IReadOnlyDictionary<string, object?> Properties);
public sealed record MapGeometry(string Type, IReadOnlyList<double[]> Coordinates);
public sealed record SegmentMatch(int StartIndex, int EndIndex, double CoveragePercent, double MeanDistanceMeters);
public sealed record CreateSegmentRequest(Guid OwnerId, Guid ActivityId, string Name, int StartPointIndex, int EndPointIndex, double ToleranceMeters = 30);
public sealed record CreateSegmentPathRequest(
    Guid OwnerId, string Name, SportKind Sport, IReadOnlyList<TrackPoint> Points, double ToleranceMeters = 30,
    Guid? SourceActivityId = null);

public sealed record SegmentSummary(
    Guid Id, Guid OwnerId, string OwnerName, string Name, SportKind Sport,
    double DistanceMeters, double ToleranceMeters, int EffortCount, double? BestElapsedSeconds,
    double? AverageGrade, double? ElevationGain, double? ElevationLoss);

public sealed record SegmentDetail(
    SegmentSummary Summary, IReadOnlyList<TrackPoint> Points, IReadOnlyList<SegmentEffortSummary> Efforts,
    Guid? SelectedEffortId, IReadOnlyList<TrackPoint> SelectedEffortPoints);
public sealed record CreateRouteRequest(Guid OwnerId, Guid ActivityId, string Name, string? Description);
public sealed record CreateRoutePathRequest(
    Guid OwnerId, string Name, string? Description, SportKind Sport,
    IReadOnlyList<TrackPoint> Points, Guid? SourceActivityId = null);
public sealed record RouteSummary(Guid Id, Guid OwnerId, string OwnerName, string Name, SportKind Sport, double DistanceMeters, double ElevationMeters);
public sealed record RouteDetail(RouteSummary Summary, string? Description, IReadOnlyList<TrackPoint> Points);

public sealed record PersonalRecord(
    Guid Id, Guid OwnerId, string OwnerName, SportKind Sport, RecordKind Kind, string Key,
    double Value, Guid ActivityId, string ActivityTitle, double CoveragePercent, DateTimeOffset ActivityDate);
