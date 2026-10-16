using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Services;

namespace ActivityExplorer.Tests;

public sealed class ActivitySearchInputTests
{
    [Theory]
    [InlineData(1001, 1011)]
    [InlineData(30001, 3599)]
    public void Scaled_units_preserve_inclusive_bounds_when_reopened(double meters, double seconds)
    {
        var saved = new ActivityCriteria(MinDistanceMeters: meters, MaxDistanceMeters: meters,
            MinMovingSeconds: seconds, MaxMovingSeconds: seconds);
        var reopened = ActivitySearchInput.FromCriteria(saved).Build(null, null, null, null, [], []);
        Assert.Equal(meters, reopened.MinDistanceMeters);
        Assert.Equal(meters, reopened.MaxDistanceMeters);
        Assert.Equal(seconds, reopened.MinMovingSeconds);
        Assert.Equal(seconds, reopened.MaxMovingSeconds);
        var bookmark = new ActivitySearchInput
        {
            MaxDistance = ActivitySearchInput.DisplayRaw(meters.ToString(System.Globalization.CultureInfo.InvariantCulture), 1000),
            MinMoving = ActivitySearchInput.DisplayRaw(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), 60)
        }.Build(null, null, null, null, [], []);
        Assert.Equal(meters, bookmark.MaxDistanceMeters);
        Assert.Equal(seconds, bookmark.MinMovingSeconds);
    }
}
