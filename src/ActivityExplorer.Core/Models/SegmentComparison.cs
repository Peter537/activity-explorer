using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Core.Models;

public enum SegmentComparisonUnavailableReason
{
    None,
    InvalidEfforts,
    MissingStream,
    InvalidInterval,
    InsufficientTiming,
    Discontinuity,
    DurationMismatch,
    InsufficientAlignment,
    AmbiguousAlignment,
    AnalysisLimit
}

public sealed record SegmentAlignedPoint(
    double DistanceMeters, SourceBoundary Source, double ElapsedSeconds, TrackPoint Point);

public sealed record SegmentEffortAlignment(
    Guid EffortId, IReadOnlyList<SegmentAlignedPoint> Points, ActivityMapProjection Map,
    SegmentComparisonUnavailableReason UnavailableReason = SegmentComparisonUnavailableReason.None,
    string? Message = null)
{
    public bool IsAvailable => UnavailableReason == SegmentComparisonUnavailableReason.None && Points.Count > 1;
}

public sealed record SegmentComparisonValue(
    SourceBoundary Source, double ElapsedSeconds, double? SpeedMetersPerSecond,
    double? HeartRate, double? PowerWatts, ActivityMapPoint? Position,
    bool SpeedStartsNewRun = false, bool HeartRateStartsNewRun = false, bool PowerStartsNewRun = false);

public sealed record SegmentComparisonSample(
    double DistanceMeters, bool IsArrival, SegmentComparisonValue Baseline, SegmentComparisonValue Comparison)
{
    public double DeltaSeconds => Comparison.ElapsedSeconds - Baseline.ElapsedSeconds;
}

public enum SegmentChildComparisonStatus { Matched, Missing, Ambiguous, AlignmentUnavailable }

public sealed record SegmentChildComparison(
    SegmentSummary Child, int Occurrence, SegmentPlacement Placement,
    SegmentEffortSummary? Baseline, SegmentChildComparisonStatus BaselineStatus,
    SegmentEffortSummary? Comparison, SegmentChildComparisonStatus ComparisonStatus)
{
    public double? DeltaSeconds => BaselineStatus == SegmentChildComparisonStatus.Matched &&
        ComparisonStatus == SegmentChildComparisonStatus.Matched
        ? Comparison!.ElapsedSeconds - Baseline!.ElapsedSeconds : null;
}

public sealed record SegmentComparisonResult(
    SegmentDetail Detail, SegmentEffortSummary? Baseline, SegmentEffortSummary? Comparison,
    SegmentEffortAlignment? BaselineAlignment, SegmentEffortAlignment? ComparisonAlignment,
    IReadOnlyList<SegmentComparisonSample> Samples, IReadOnlyList<SegmentChildComparison> Children,
    SegmentComparisonUnavailableReason UnavailableReason = SegmentComparisonUnavailableReason.None,
    string? Message = null);
