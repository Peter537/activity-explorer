namespace ActivityExplorer.Core.Domain;

public sealed record TrackPoint(
    DateTimeOffset? Timestamp,
    double? Latitude,
    double? Longitude,
    double? DistanceMeters,
    double? ElevationMeters,
    double? SpeedMetersPerSecond,
    double? HeartRate,
    double? Cadence,
    double? PowerWatts,
    double? TemperatureCelsius,
    double? RespirationRate = null);

public sealed record LapCandidate(
    int Sequence,
    double DistanceMeters,
    double ElapsedSeconds,
    double MovingSeconds,
    double? AverageHeartRate,
    double? AveragePowerWatts);

public sealed class ParsedActivity
{
    public required SportKind Sport { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string? DeviceName { get; init; }
    public string? GearName { get; init; }
    public string? ExternalId { get; init; }
    public bool? IsIndoor { get; init; }
    public required DateTimeOffset StartTimeUtc { get; init; }
    public TimeSpan? OriginalUtcOffset { get; init; }
    public double DistanceMeters { get; init; }
    public double MovingTimeSeconds { get; init; }
    public double? TimerTimeSeconds { get; init; }
    public MovingTimeSource MovingTimeSource { get; init; } = MovingTimeSource.SourceSummary;
    public double ElapsedTimeSeconds { get; init; }
    public double ElevationGainMeters { get; init; }
    public double? ElevationLossMeters { get; init; }
    public double? MinElevationMeters { get; init; }
    public double? MaxElevationMeters { get; init; }
    public double? Calories { get; init; }
    public double? RestingCalories { get; init; }
    public double? ActiveCalories { get; init; }
    public double? AverageSpeedMetersPerSecond { get; init; }
    public double? MaxSpeedMetersPerSecond { get; init; }
    public double? AverageHeartRate { get; init; }
    public double? MaxHeartRate { get; init; }
    public double? AverageCadence { get; init; }
    public double? MaxCadence { get; init; }
    public double? PedalRevolutions { get; init; }
    public double? AveragePowerWatts { get; init; }
    public double? MaxPowerWatts { get; init; }
    public double? NormalizedPowerWatts { get; init; }
    public double? Kilojoules { get; init; }
    public double? AverageTemperatureCelsius { get; init; }
    public double? MinTemperatureCelsius { get; init; }
    public double? MaxTemperatureCelsius { get; init; }
    public double? AverageRespirationRate { get; init; }
    public double? MinRespirationRate { get; init; }
    public double? MaxRespirationRate { get; init; }
    public double? AerobicTrainingEffect { get; init; }
    public double? AnaerobicTrainingEffect { get; init; }
    public double? TrainingLoad { get; init; }
    public IReadOnlyList<ActivityMetricCandidate> Metrics { get; init; } = [];
    public IReadOnlyList<TrackPoint> Points { get; init; } = [];
    public IReadOnlyList<LapCandidate> Laps { get; init; } = [];
}

public sealed record ImportCandidate(
    string FilePath,
    string OriginalName,
    SourceKind SourceKind,
    string Sha256,
    long Length,
    ParsedActivity Parsed,
    string? ExternalId = null,
    SourceProvider Provider = SourceProvider.Unknown,
    AcquisitionMethod AcquisitionMethod = AcquisitionMethod.DirectUpload,
    int ParserVersion = 1);

public sealed record ActivityMetricCandidate(
    string Key,
    string Label,
    double? NumericValue = null,
    string? TextValue = null,
    string? Unit = null);
