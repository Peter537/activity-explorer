using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Services;

namespace ActivityExplorer.Tests;

public sealed class SegmentEffortMetricCalculatorTests
{
    [Fact]
    public void Uneven_intervals_use_trapezoidal_time_weighting_and_include_zeroes()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var points = new[]
        {
            Point(start, heartRate: 0),
            Point(start.AddSeconds(1), heartRate: 10),
            Point(start.AddSeconds(5), heartRate: 20)
        };

        var average = SegmentEffortMetricCalculator.TimeWeightedAverage(points, point => point.HeartRate);

        Assert.Equal(13, average!.Value, 8);
    }

    [Fact]
    public void Missing_values_and_invalid_intervals_are_excluded()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var points = new[]
        {
            Point(start, heartRate: 10),
            Point(start.AddSeconds(2), heartRate: null),
            Point(start.AddSeconds(4), heartRate: 30),
            Point(start.AddSeconds(5), heartRate: 50),
            Point(start.AddSeconds(5), heartRate: 500),
            Point(start.AddSeconds(36), heartRate: 700)
        };

        var average = SegmentEffortMetricCalculator.TimeWeightedAverage(points, point => point.HeartRate);

        Assert.Equal(40, average!.Value, 8);
    }

    [Fact]
    public void Single_sample_and_streams_without_a_usable_interval_fall_back_to_finite_sample_mean()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var single = new[] { Point(start, cadence: 42) };
        var unusable = new[]
        {
            Point(start, cadence: 10),
            Point(start, cadence: 20),
            Point(start.AddSeconds(31), cadence: 30)
        };

        Assert.Equal(42, SegmentEffortMetricCalculator.TimeWeightedAverage(single, point => point.Cadence));
        Assert.Equal(20, SegmentEffortMetricCalculator.TimeWeightedAverage(unusable, point => point.Cadence));
    }

    [Fact]
    public void Calculation_uses_saved_distance_for_average_speed_and_ignores_non_finite_maxima()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var points = new[]
        {
            Point(start, latitude: 55, longitude: 12, speed: double.NaN, power: double.PositiveInfinity),
            Point(start.AddSeconds(5), latitude: 55, longitude: 12.001, speed: 9, power: 200),
            Point(start.AddSeconds(10), latitude: 55, longitude: 12.002, speed: double.NegativeInfinity, power: 250)
        };

        var metrics = SegmentEffortMetricCalculator.Calculate(462.59, points, 105);

        Assert.Equal(462.59 / 105, metrics.AverageSpeedMetersPerSecond!.Value, 8);
        Assert.InRange(metrics.RecordedDistanceMeters, 127, 129);
        Assert.Equal(9, metrics.MaxSpeedMetersPerSecond);
        Assert.Equal(250, metrics.MaxPowerWatts);
    }

    private static TrackPoint Point(
        DateTimeOffset timestamp,
        double? latitude = null,
        double? longitude = null,
        double? speed = null,
        double? heartRate = null,
        double? cadence = null,
        double? power = null) =>
        new(timestamp, latitude, longitude, null, null, speed, heartRate, cadence, power, null);
}
