using ActivityExplorer.Core.Domain;
using ActivityExplorer.Web.Components.Shared;

namespace ActivityExplorer.Tests;

public sealed class FormatTests
{
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
