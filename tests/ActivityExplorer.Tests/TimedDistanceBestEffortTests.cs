using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Processing;

namespace ActivityExplorer.Tests;

public sealed class TimedDistanceBestEffortTests
{
    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_target_durations_are_rejected(double targetSeconds)
    {
        var start = Start();
        var points = new[]
        {
            Point(start, distance: 0),
            Point(start.AddSeconds(10), distance: 100)
        };

        Assert.Null(BestEffortCalculator.BestTimedDistance(points, targetSeconds, SportKind.Cycling));
    }

    [Fact]
    public void Timed_distance_interpolates_an_exact_finish_boundary()
    {
        var start = Start();
        var points = new[]
        {
            Point(start, distance: 0),
            Point(start.AddSeconds(4), distance: 40),
            Point(start.AddSeconds(10), distance: 46),
            Point(start.AddSeconds(14), distance: 46)
        };

        var effort = BestEffortCalculator.BestTimedDistance(points, 8, SportKind.Cycling);

        Assert.NotNull(effort);
        Assert.Equal(44, effort.Value.Value, precision: 6);
        Assert.Equal(100, effort.Value.CoveragePercent);
    }

    [Fact]
    public void Timed_distance_interpolates_an_exact_begin_boundary()
    {
        var start = Start();
        var points = new[]
        {
            Point(start, distance: 0),
            Point(start.AddSeconds(4), distance: 0),
            Point(start.AddSeconds(10), distance: 6),
            Point(start.AddSeconds(14), distance: 46)
        };

        var effort = BestEffortCalculator.BestTimedDistance(points, 8, SportKind.Cycling);

        Assert.NotNull(effort);
        Assert.Equal(44, effort.Value.Value, precision: 6);
    }

    [Fact]
    public void Timed_distance_prefers_recorded_distance_over_gps_geometry()
    {
        var start = Start();
        var points = new[]
        {
            Point(start, latitude: 0, longitude: 0, distance: 0),
            Point(start.AddSeconds(10), latitude: 0, longitude: 0.0001, distance: 100)
        };

        var effort = BestEffortCalculator.BestTimedDistance(points, 10, SportKind.Cycling);

        Assert.NotNull(effort);
        Assert.Equal(100, effort.Value.Value, precision: 6);
    }

    [Fact]
    public void Timed_distance_falls_back_to_gps_geometry()
    {
        var start = Start();
        var points = new[]
        {
            Point(start, latitude: 0, longitude: 0),
            Point(start.AddSeconds(10), latitude: 0, longitude: 0.001)
        };
        var expected = GeometryCodec.HaversineMeters(0, 0, 0, 0.001);

        var effort = BestEffortCalculator.BestTimedDistance(points, 10, SportKind.Cycling);

        Assert.NotNull(effort);
        Assert.Equal(expected, effort.Value.Value, precision: 6);
    }

    [Fact]
    public void Timed_distance_falls_back_to_gps_when_recorded_distance_is_nonfinite()
    {
        var start = Start();
        var points = new[]
        {
            Point(start, latitude: 0, longitude: 0, distance: double.NaN),
            Point(start.AddSeconds(10), latitude: 0, longitude: 0.001, distance: double.NaN)
        };
        var expected = GeometryCodec.HaversineMeters(0, 0, 0, 0.001);

        var effort = BestEffortCalculator.BestTimedDistance(points, 10, SportKind.Cycling);

        Assert.NotNull(effort);
        Assert.Equal(expected, effort.Value.Value, precision: 6);
    }

    [Fact]
    public void Timed_distance_allows_recorded_distance_without_gps_but_fixed_distance_does_not()
    {
        var start = Start();
        var points = new[]
        {
            Point(start, distance: 0),
            Point(start.AddSeconds(10), distance: 100)
        };

        var timed = BestEffortCalculator.BestTimedDistance(points, 10, SportKind.Cycling);
        var fixedDistance = BestEffortCalculator.BestDistance(points, 50, SportKind.Cycling);

        Assert.NotNull(timed);
        Assert.Equal(100, timed.Value.Value, precision: 6);
        Assert.Null(fixedDistance);
    }

    [Fact]
    public void Timed_distance_counts_stationary_pauses_and_recording_gaps_as_elapsed_time()
    {
        var start = Start();
        var points = new[]
        {
            Point(start, distance: 0),
            Point(start.AddSeconds(10), distance: 100),
            Point(start.AddSeconds(70), distance: 100),
            Point(start.AddSeconds(80), distance: 200)
        };

        var effort = BestEffortCalculator.BestTimedDistance(points, 80, SportKind.Cycling);

        Assert.NotNull(effort);
        Assert.Equal(200, effort.Value.Value, precision: 6);
    }

    [Fact]
    public void Timed_distance_splits_at_a_missing_timestamp()
    {
        var start = Start();
        var points = new[]
        {
            Point(start, distance: 0),
            Point(null, distance: 100),
            Point(start.AddSeconds(20), distance: 200),
            Point(start.AddSeconds(30), distance: 300)
        };

        Assert.Null(BestEffortCalculator.BestTimedDistance(points, 30, SportKind.Cycling));
    }

    [Fact]
    public void Timed_distance_splits_at_a_reversed_timestamp()
    {
        var start = Start();
        var points = new[]
        {
            Point(start, distance: 0),
            Point(start.AddSeconds(10), distance: 100),
            Point(start.AddSeconds(5), distance: 150),
            Point(start.AddSeconds(15), distance: 250)
        };

        Assert.Null(BestEffortCalculator.BestTimedDistance(points, 15, SportKind.Cycling));
    }

    [Fact]
    public void Timed_distance_splits_at_an_edge_without_recorded_distance_or_gps()
    {
        var start = Start();
        var points = new[]
        {
            Point(start, distance: 0),
            Point(start.AddSeconds(10), distance: 100),
            Point(start.AddSeconds(20)),
            Point(start.AddSeconds(30), distance: 300),
            Point(start.AddSeconds(40), distance: 400)
        };

        Assert.Null(BestEffortCalculator.BestTimedDistance(points, 20, SportKind.Cycling));
    }

    [Fact]
    public void Timed_distance_splits_at_a_nonfinite_recorded_distance_without_gps()
    {
        var start = Start();
        var points = new[]
        {
            Point(start, distance: 0),
            Point(start.AddSeconds(10), distance: double.NaN),
            Point(start.AddSeconds(20), distance: 200)
        };

        Assert.Null(BestEffortCalculator.BestTimedDistance(points, 20, SportKind.Cycling));
    }

    [Fact]
    public void Timed_distance_splits_at_a_counter_reset_even_when_gps_is_available()
    {
        var start = Start();
        var points = new[]
        {
            Point(start, latitude: 0, longitude: 0, distance: 0),
            Point(start.AddSeconds(5), latitude: 0, longitude: 0, distance: 100),
            Point(start.AddSeconds(10), latitude: 0, longitude: 0, distance: 10),
            Point(start.AddSeconds(15), latitude: 0, longitude: 0, distance: 110)
        };

        Assert.Null(BestEffortCalculator.BestTimedDistance(points, 15, SportKind.Cycling));
    }

    [Theory]
    [InlineData(SportKind.Cycling, 200)]
    [InlineData(SportKind.Running, 60)]
    [InlineData(SportKind.Walking, 30)]
    public void Timed_distance_rejects_edges_above_the_sport_speed_cap(SportKind sport, double capKmh)
    {
        var start = Start();
        var points = new[]
        {
            Point(start, distance: 0),
            Point(start.AddSeconds(10), distance: (capKmh + 1) / 3.6 * 10)
        };

        Assert.Null(BestEffortCalculator.BestTimedDistance(points, 10, sport));
    }

    [Theory]
    [InlineData(SportKind.Cycling, 200)]
    [InlineData(SportKind.Running, 60)]
    [InlineData(SportKind.Walking, 30)]
    public void Timed_distance_accepts_edges_at_the_sport_speed_cap(SportKind sport, double capKmh)
    {
        var start = Start();
        var expected = capKmh / 3.6 * 10;
        var points = new[]
        {
            Point(start, distance: 0),
            Point(start.AddSeconds(10), distance: expected)
        };

        var effort = BestEffortCalculator.BestTimedDistance(points, 10, sport);

        Assert.NotNull(effort);
        Assert.Equal(expected, effort.Value.Value, precision: 6);
    }

    [Fact]
    public void Timed_distance_requires_a_complete_window()
    {
        var start = Start();
        var points = new[]
        {
            Point(start, distance: 0),
            Point(start.AddSeconds(9), distance: 90)
        };

        Assert.Null(BestEffortCalculator.BestTimedDistance(points, 10, SportKind.Cycling));
    }

    [Fact]
    public void Timed_distance_rejects_a_zero_distance_window()
    {
        var start = Start();
        var points = new[]
        {
            Point(start, distance: 100),
            Point(start.AddSeconds(10), distance: 100)
        };

        Assert.Null(BestEffortCalculator.BestTimedDistance(points, 10, SportKind.Cycling));
    }

    private static DateTimeOffset Start() =>
        new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private static TrackPoint Point(
        DateTimeOffset? timestamp,
        double? latitude = null,
        double? longitude = null,
        double? distance = null) =>
        new(timestamp, latitude, longitude, distance, null, null, null, null, null, null);
}
