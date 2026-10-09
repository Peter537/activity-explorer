using ActivityExplorer.Core.Domain;
using ActivityExplorer.Web.Components.Shared;
using ActivityExplorer.Web.Services;

namespace ActivityExplorer.Tests;

public sealed class FormatTests
{
    [Fact]
    public void Progress_comparisons_keep_negative_duration_and_zero_baselines_truthful()
    {
        Assert.Contains("−1:00:00", ProgressDisplay.Comparison("moving", 3600, 7200), StringComparison.Ordinal);
        Assert.Contains("-50", ProgressDisplay.Comparison("moving", 3600, 7200), StringComparison.Ordinal);
        Assert.Contains("percentage unavailable", ProgressDisplay.Comparison("activities", 0, 0), StringComparison.Ordinal);
        Assert.DoesNotContain('%', ProgressDisplay.Comparison("distance", 5000, 0));
        Assert.Contains("25:00:00", ProgressDisplay.FormatValue("moving", 90_000), StringComparison.Ordinal);
    }

    [Fact]
    public void Formats_distance_duration_speed_and_missing_values()
    {
        Assert.EndsWith(" km", Format.Distance(1_500), StringComparison.Ordinal);
        Assert.EndsWith(" m", Format.Distance(999), StringComparison.Ordinal);
        Assert.Equal("1:01:01", Format.Duration(3_661));
        Assert.Equal("0:00", Format.Duration(-1));
        Assert.Equal("5:00 /km", Format.Speed(10d / 3d, SportKind.Running));
        Assert.EndsWith(" km/h", Format.Speed(10, SportKind.Cycling), StringComparison.Ordinal);
        Assert.Equal("--", Format.Speed(null, SportKind.Walking));
        Assert.Equal("--", Format.Speed(0, SportKind.Cycling));
    }
}
