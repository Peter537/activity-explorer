using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Web.Components.Shared;

namespace ActivityExplorer.Tests;

public sealed class RowingRecordTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Rowing_targets_and_units_are_sport_specific()
    {
        Assert.Equal(4, (int)SportKind.Rowing);
        Assert.Equal(3, (int)RecordScope.Indoor);
        Assert.Equal(
            ["100 m", "500 m", "1 km", "2 km", "5 km", "6 km", "10 km", "Half marathon", "Marathon"],
            RecordCatalog.DistanceTargets(SportKind.Rowing).Select(target => target.Key));
        Assert.Equal([100d, 500, 1000, 2000, 5000, 6000, 10000, 21097, 42195],
            RecordCatalog.DistanceTargets(SportKind.Rowing).Select(target => target.Target));
        Assert.Equal(["1 min", "4 min", "30 min", "1 hour"],
            RecordCatalog.TimedDistanceTargets(SportKind.Rowing).Select(target => target.Key));
        Assert.Equal([60d, 240, 1800, 3600], RecordCatalog.TimedDistanceTargets(SportKind.Rowing).Select(target => target.Target));
        Assert.Equal("3:20 /500 m", Format.Speed(2.5, SportKind.Rowing));
        Assert.Equal("min/500 m", Format.SpeedUnit(SportKind.Rowing));
        Assert.Equal(500d / 2.5 / 60, Format.SpeedOrPace(2.5, SportKind.Rowing));
        Assert.Equal("Stroke rate", Format.CadenceLabel(SportKind.Rowing));
        Assert.Equal("spm", Format.CadenceUnit(SportKind.Rowing));
        Assert.Equal("--", Format.Speed(double.NaN, SportKind.Rowing));
        Assert.Null(Format.SpeedOrPace(0, SportKind.Rowing));
    }

    [Fact]
    public void Fixed_distance_and_timed_windows_include_pauses_and_interpolate_boundaries()
    {
        TrackPoint[] points = [Point(0, 0), Point(10, 50), Point(40, 50), Point(70, 200)];
        var distance = BestEffortCalculator.BestDistance(points, 175, SportKind.Rowing);
        Assert.Equal(65, distance!.Value.Value, 6);
        Assert.Equal(100, distance.Value.CoveragePercent);
        Assert.Equal(150, BestEffortCalculator.BestTimedDistance(points, 60, SportKind.Rowing)!.Value.Value, 6);
        foreach (var sport in new[] { SportKind.Cycling, SportKind.Running, SportKind.Walking })
            Assert.Null(BestEffortCalculator.BestDistance(points, 100, sport));
    }

    [Theory]
    [InlineData("missing-distance")]
    [InlineData("invalid-distance")]
    [InlineData("reset")]
    [InlineData("missing-time")]
    [InlineData("reversed-time")]
    [InlineData("duplicate-time")]
    [InlineData("speed-spike")]
    public void Distance_windows_do_not_cross_bad_edges(string boundary)
    {
        var middle = boundary switch
        {
            "missing-distance" => Point(30, null),
            "invalid-distance" => Point(30, double.NaN),
            "reset" => Point(30, -1),
            "missing-time" => Point(30, 50) with { Timestamp = null },
            "reversed-time" => Point(10, 50),
            "duplicate-time" => Point(20, 50),
            _ => Point(21, 80)
        };
        TrackPoint[] points = [Point(0, 0), Point(20, 40), middle, Point(60, boundary == "reset" ? 50 : 100)];
        Assert.Null(BestEffortCalculator.BestDistance(points, 100, SportKind.Rowing));
        Assert.Null(BestEffortCalculator.BestTimedDistance(points, 60, SportKind.Rowing));
    }

    [Fact]
    public void Exact_speed_ceiling_is_allowed_and_summary_only_data_cannot_qualify()
    {
        TrackPoint[] exact = [Point(0, 0), Point(60, 500)];
        Assert.Equal(60, BestEffortCalculator.BestDistance(exact, 500, SportKind.Rowing)!.Value.Value);
        Assert.Equal(500, BestEffortCalculator.BestTimedDistance(exact, 60, SportKind.Rowing)!.Value.Value);
        TrackPoint[] tooFast = [Point(0, 0), Point(60, 500.01)];
        Assert.Null(BestEffortCalculator.BestDistance(tooFast, 500, SportKind.Rowing));
        Assert.Null(BestEffortCalculator.BestTimedDistance(tooFast, 60, SportKind.Rowing));
        TrackPoint[] missing = [Point(0, null), Point(600, null)];
        Assert.Null(BestEffortCalculator.BestDistance(missing, 100, SportKind.Rowing));
    }

    [Fact]
    public void Rowing_can_use_outdoor_geometry_and_power_gaps_still_split_windows()
    {
        TrackPoint[] gps = [Point(0, null) with { Latitude = 0, Longitude = 0 },
            Point(60, null) with { Latitude = 0, Longitude = 0.001 }];
        Assert.NotNull(BestEffortCalculator.BestDistance(gps, 100, SportKind.Rowing));
        var power = Enumerable.Range(0, 61).Select(second => Point(second, second * 2) with { PowerWatts = 100 }).ToArray();
        Assert.Equal(100, BestEffortCalculator.BestPower(power, 60)!.Value.Value);
        var gap = power.Where(point => (point.Timestamp!.Value - Start).TotalSeconds is < 20 or > 25).ToArray();
        Assert.Null(BestEffortCalculator.BestPower(gap, 60));
    }

    private static TrackPoint Point(double seconds, double? meters) =>
        new(Start.AddSeconds(seconds), null, null, meters, null, null, null, null, null, null);
}
