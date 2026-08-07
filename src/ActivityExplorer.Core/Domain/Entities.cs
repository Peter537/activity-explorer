using System.ComponentModel.DataAnnotations;

namespace ActivityExplorer.Core.Domain;

public sealed class OwnerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(120)] public string DisplayName { get; set; } = string.Empty;
    [MaxLength(80)] public string? Culture { get; set; }
    [MaxLength(120)] public string? TimeZoneId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ImportBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public OwnerProfile? Owner { get; set; }
    public SourceKind SourceKind { get; set; }
    public ImportBatchKind Kind { get; set; } = ImportBatchKind.FileImport;
    public ImportStatus Status { get; set; } = ImportStatus.Queued;
    [MaxLength(260)] public string DisplayName { get; set; } = string.Empty;
    [MaxLength(1024)] public string StagedPath { get; set; } = string.Empty;
    public int FilesDiscovered { get; set; }
    public int ActivitiesCreated { get; set; }
    public int ActivitiesUpdated { get; set; }
    public int DuplicatesSkipped { get; set; }
    public int UnsupportedSkipped { get; set; }
    public int Warnings { get; set; }
    [MaxLength(4000)] public string? Summary { get; set; }
    [MaxLength(4000)] public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed class SourceFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public Guid ImportBatchId { get; set; }
    public ImportBatch? ImportBatch { get; set; }
    public Guid? ActivityId { get; set; }
    public Activity? Activity { get; set; }
    public Guid? RouteId { get; set; }
    public Route? Route { get; set; }
    public SourceKind SourceKind { get; set; }
    public SourceProvider Provider { get; set; }
    public AcquisitionMethod AcquisitionMethod { get; set; } = AcquisitionMethod.DirectUpload;
    [MaxLength(260)] public string OriginalName { get; set; } = string.Empty;
    [MaxLength(1024)] public string StoredPath { get; set; } = string.Empty;
    [MaxLength(64)] public string Sha256 { get; set; } = string.Empty;
    [MaxLength(200)] public string? ExternalId { get; set; }
    public int ParserVersion { get; set; } = 1;
    public long Length { get; set; }
    public DateTimeOffset ImportedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Activity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public OwnerProfile? Owner { get; set; }
    public SportKind Sport { get; set; }
    [MaxLength(240)] public string Title { get; set; } = string.Empty;
    [MaxLength(4000)] public string? Description { get; set; }
    [MaxLength(160)] public string? DeviceName { get; set; }
    [MaxLength(160)] public string? GearName { get; set; }
    [MaxLength(200)] public string? GarminId { get; set; }
    [MaxLength(200)] public string? StravaId { get; set; }
    [MaxLength(128)] public string NaturalFingerprint { get; set; } = string.Empty;
    public DateTimeOffset StartTimeUtc { get; set; }
    public TimeSpan? OriginalUtcOffset { get; set; }
    public double DistanceMeters { get; set; }
    public double MovingTimeSeconds { get; set; }
    public double? TimerTimeSeconds { get; set; }
    public MovingTimeSource MovingTimeSource { get; set; } = MovingTimeSource.SourceSummary;
    public double ElapsedTimeSeconds { get; set; }
    public double ElevationGainMeters { get; set; }
    public double? ElevationLossMeters { get; set; }
    public double? MinElevationMeters { get; set; }
    public double? MaxElevationMeters { get; set; }
    public double? Calories { get; set; }
    public double? RestingCalories { get; set; }
    public double? ActiveCalories { get; set; }
    public double? AverageSpeedMetersPerSecond { get; set; }
    public double? MaxSpeedMetersPerSecond { get; set; }
    public double? AverageHeartRate { get; set; }
    public double? MaxHeartRate { get; set; }
    public double? AverageCadence { get; set; }
    public double? MaxCadence { get; set; }
    public double? PedalRevolutions { get; set; }
    public double? AveragePowerWatts { get; set; }
    public double? MaxPowerWatts { get; set; }
    public double? NormalizedPowerWatts { get; set; }
    public double? Kilojoules { get; set; }
    public double? AverageTemperatureCelsius { get; set; }
    public double? MinTemperatureCelsius { get; set; }
    public double? MaxTemperatureCelsius { get; set; }
    public double? AverageRespirationRate { get; set; }
    public double? MinRespirationRate { get; set; }
    public double? MaxRespirationRate { get; set; }
    public double? AerobicTrainingEffect { get; set; }
    public double? AnaerobicTrainingEffect { get; set; }
    public double? TrainingLoad { get; set; }
    public int TechnicalDataVersion { get; set; } = 1;

    public double? MinLatitude { get; set; }
    public double? MinLongitude { get; set; }
    public double? MaxLatitude { get; set; }
    public double? MaxLongitude { get; set; }
    public byte[]? GeometryWkb { get; set; }
    public byte[]? SimplifiedGeometryWkb { get; set; }
    public bool HasGps { get; set; }
    public bool HasPower { get; set; }
    public bool IsIndoor { get; set; }
    public bool UserEdited { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public ActivityStream? Stream { get; set; }
    public List<ActivityLap> Laps { get; set; } = [];
    public List<SourceFile> SourceFiles { get; set; } = [];
    public List<ActivityMetric> Metrics { get; set; } = [];
}

public sealed class ActivityMetric
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public Guid ActivityId { get; set; }
    public Activity? Activity { get; set; }
    [MaxLength(100)] public string Key { get; set; } = string.Empty;
    [MaxLength(160)] public string Label { get; set; } = string.Empty;
    public double? NumericValue { get; set; }
    [MaxLength(1000)] public string? TextValue { get; set; }
    [MaxLength(40)] public string? Unit { get; set; }
    public ActivityMetricOrigin Origin { get; set; }
    public Guid? SourceFileId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}


public sealed class ActivityLap
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public Guid ActivityId { get; set; }
    public Activity? Activity { get; set; }
    public int Sequence { get; set; }
    public double DistanceMeters { get; set; }
    public double ElapsedSeconds { get; set; }
    public double MovingSeconds { get; set; }
    public double? AverageHeartRate { get; set; }
    public double? AveragePowerWatts { get; set; }
}

public sealed class ActivityStream
{
    public Guid ActivityId { get; set; }
    public Guid OwnerId { get; set; }
    public Activity? Activity { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public byte[] CompressedPayload { get; set; } = [];
    public int PointCount { get; set; }
}

public sealed class Gear
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    [MaxLength(160)] public string Name { get; set; } = string.Empty;
    [MaxLength(100)] public string? SourceId { get; set; }
    public SportKind? Sport { get; set; }
}

public sealed class Route
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public OwnerProfile? Owner { get; set; }
    public Guid? SourceActivityId { get; set; }
    public SportKind Sport { get; set; }
    [MaxLength(240)] public string Name { get; set; } = string.Empty;
    [MaxLength(4000)] public string? Description { get; set; }
    public double DistanceMeters { get; set; }
    public double ElevationGainMeters { get; set; }
    public byte[] GeometryWkb { get; set; } = [];
    public double MinLatitude { get; set; }
    public double MinLongitude { get; set; }
    public double MaxLatitude { get; set; }
    public double MaxLongitude { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<SourceFile> SourceFiles { get; set; } = [];
}

public sealed class Segment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public OwnerProfile? Owner { get; set; }
    public Guid? SourceActivityId { get; set; }
    public SportKind Sport { get; set; }
    [MaxLength(240)] public string Name { get; set; } = string.Empty;
    public double DistanceMeters { get; set; }
    public double? AverageGradePercent { get; set; }
    public double? ElevationGainMeters { get; set; }
    public double? ElevationLossMeters { get; set; }
    public double ToleranceMeters { get; set; } = 30;
    public byte[] GeometryWkb { get; set; } = [];
    public double MinLatitude { get; set; }
    public double MinLongitude { get; set; }
    public double MaxLatitude { get; set; }
    public double MaxLongitude { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ApplicationSetting
{
    [Key, MaxLength(100)] public string Key { get; set; } = string.Empty;
    [MaxLength(4000)] public string Value { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FileOperationJournal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public FileOperationKind Kind { get; set; }
    public FileOperationState State { get; set; } = FileOperationState.Pending;
    public Guid? OwnerId { get; set; }
    public Guid? EntityId { get; set; }
    public bool DeleteSourceOnCommit { get; set; }
    [MaxLength(1024)] public string? SourceRelativePath { get; set; }
    [MaxLength(1024)] public string? TargetRelativePath { get; set; }
    [MaxLength(4000)] public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SegmentEffort
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public Guid SegmentId { get; set; }
    public Segment? Segment { get; set; }
    public Guid ActivityId { get; set; }
    public Activity? Activity { get; set; }
    public int StartPointIndex { get; set; }
    public int EndPointIndex { get; set; }
    public DateTimeOffset StartTimeUtc { get; set; }
    public double ElapsedSeconds { get; set; }
    public double MovingSeconds { get; set; }
    public double? AverageHeartRate { get; set; }
    public double? AverageCadence { get; set; }
    public double? AveragePowerWatts { get; set; }
    public double? ElevationGainMeters { get; set; }
    public double? ElevationLossMeters { get; set; }
    public double? AverageGradePercent { get; set; }
    public double? AverageSpeedMetersPerSecond { get; set; }
    public double? MaxSpeedMetersPerSecond { get; set; }
    public double? MaxHeartRate { get; set; }
    public double? MaxCadence { get; set; }
    public double? MaxPowerWatts { get; set; }
    public double? AverageTemperatureCelsius { get; set; }
    public double? AverageRespirationRate { get; set; }
    public double CoveragePercent { get; set; }

    public int Rank { get; set; }
}

public sealed class StatisticSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public SportKind Sport { get; set; }
    public RecordKind Kind { get; set; }
    public RecordScope Scope { get; set; } = RecordScope.All;
    [MaxLength(80)] public string Key { get; set; } = string.Empty;
    public double Value { get; set; }
    public Guid ActivityId { get; set; }
    public double CoveragePercent { get; set; }
    public int ComputationVersion { get; set; } = 1;
    public DateTimeOffset ComputedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WatchedFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    [MaxLength(1024)] public string Path { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastScanAtUtc { get; set; }
}
