namespace ActivityExplorer.Core.Models;

public sealed record ReadPage(int Page = 1, int PageSize = 25)
{
    public int Offset
    {
        get
        {
            if (Page is < 1 or > 1000 || PageSize is < 1 or > 100)
                throw new ArgumentException("Choose a page from 1 to 1000 and a page size from 1 to 100.");
            return checked((Page - 1) * PageSize);
        }
    }
}

public sealed record FilterValue(Guid? Id, string Value);
public sealed record ActivityReadDetail(ActivitySummary Summary, string? Description, string? GearName,
    double ElapsedSeconds, double? Calories, double? AverageSpeedMetersPerSecond, double? MaxSpeedMetersPerSecond,
    double? AverageHeartRate, double? MaxHeartRate, double? AverageCadence, double? MaxPowerWatts,
    double? NormalizedPowerWatts, int LapCount, int SourceCount, int SegmentEffortCount, int MetricCount);
public sealed record RouteReadDetail(RouteSummary Summary, string? Description);
public sealed record SegmentReadDetail(SegmentSummary Summary, int ParentCount, int ChildCount, bool MetricsCurrent);
public sealed record SegmentTimingSample(double DistanceMeters, double BaselineElapsedSeconds, double ComparisonElapsedSeconds, double DeltaSeconds);
public sealed record SegmentReadComparison(SegmentSummary Segment, SegmentEffortSummary Baseline, SegmentEffortSummary Comparison,
    IReadOnlyList<SegmentTimingSample> Samples, int OriginalSampleCount, SegmentComparisonUnavailableReason UnavailableReason, string? Message)
{
    public double DeltaSeconds => Comparison.ElapsedSeconds - Baseline.ElapsedSeconds;
}
public sealed record ReadRecordOverview(RecordOverviewResult Overview, IReadOnlyList<RecordBenchmark> Benchmarks,
    string Source, bool Available, string? UnavailableReason, int ComputationVersion);
