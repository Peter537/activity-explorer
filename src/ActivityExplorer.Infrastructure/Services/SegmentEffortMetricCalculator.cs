using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Infrastructure.Services;

internal sealed record SegmentEffortMetrics(
    double RecordedDistanceMeters,
    double? ElevationGainMeters,
    double? ElevationLossMeters,
    double? AverageGradePercent,
    double? AverageSpeedMetersPerSecond,
    double? AverageHeartRate,
    double? AverageCadence,
    double? AveragePowerWatts,
    double? AverageTemperatureCelsius,
    double? AverageRespirationRate,
    double? MaxSpeedMetersPerSecond,
    double? MaxHeartRate,
    double? MaxCadence,
    double? MaxPowerWatts);

internal static class SegmentEffortMetricCalculator
{
    private const double MaximumIntervalSeconds = 30;

    public static SegmentEffortMetrics Calculate(
        double segmentDistanceMeters,
        IReadOnlyList<TrackPoint> points,
        double elapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(points);
        var path = new TrackPathAnalysis(points).Slice(0, points.Count - 1);
        return new SegmentEffortMetrics(
            path.DistanceMeters,
            path.ElevationGainMeters,
            path.ElevationLossMeters,
            path.AverageGradePercent,
            double.IsFinite(segmentDistanceMeters) && segmentDistanceMeters > 0 &&
            double.IsFinite(elapsedSeconds) && elapsedSeconds > 0
                ? segmentDistanceMeters / elapsedSeconds
                : null,
            TimeWeightedAverage(points, point => point.HeartRate),
            TimeWeightedAverage(points, point => point.Cadence),
            TimeWeightedAverage(points, point => point.PowerWatts),
            TimeWeightedAverage(points, point => point.TemperatureCelsius),
            TimeWeightedAverage(points, point => point.RespirationRate),
            Maximum(points, point => point.SpeedMetersPerSecond),
            Maximum(points, point => point.HeartRate),
            Maximum(points, point => point.Cadence),
            Maximum(points, point => point.PowerWatts));
    }

    internal static double? TimeWeightedAverage(
        IReadOnlyList<TrackPoint> points,
        Func<TrackPoint, double?> selector)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(selector);
        var fallback = points.Select(selector)
            .Where(value => value.HasValue && double.IsFinite(value.Value))
            .Select(value => value!.Value)
            .ToArray();
        if (fallback.Length == 0) return null;

        var weightedTotal = 0d;
        var weightedSeconds = 0d;
        for (var index = 1; index < points.Count; index++)
        {
            var previous = points[index - 1];
            var current = points[index];
            if (!previous.Timestamp.HasValue || !current.Timestamp.HasValue) continue;
            var seconds = (current.Timestamp.Value - previous.Timestamp.Value).TotalSeconds;
            if (seconds is <= 0 or > MaximumIntervalSeconds) continue;

            var previousValue = selector(previous);
            var currentValue = selector(current);
            if (!previousValue.HasValue || !currentValue.HasValue ||
                !double.IsFinite(previousValue.Value) || !double.IsFinite(currentValue.Value)) continue;
            weightedTotal += (previousValue.Value + currentValue.Value) / 2d * seconds;
            weightedSeconds += seconds;
        }

        return weightedSeconds > 0 ? weightedTotal / weightedSeconds : fallback.Average();
    }

    internal static double? Maximum(
        IEnumerable<TrackPoint> points,
        Func<TrackPoint, double?> selector)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(selector);
        var values = points.Select(selector)
            .Where(value => value.HasValue && double.IsFinite(value.Value))
            .Select(value => value!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Max();
    }
}
