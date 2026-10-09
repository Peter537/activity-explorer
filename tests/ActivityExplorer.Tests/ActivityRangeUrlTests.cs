using System.Globalization;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Services;

namespace ActivityExplorer.Tests;

public sealed class ActivityRangeUrlTests
{
    [Fact]
    public void Exact_boundaries_round_trip_independent_of_display_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("da-DK");
            var range = new ActivityRange(new(14, 19, 0.12345678912345678, DateTimeOffset.UnixEpoch.AddTicks(123456789)),
                new(700, 701, 0.9876543219876543, DateTimeOffset.UnixEpoch.AddTicks(987654321)));
            Assert.True(ActivityRangeUrl.TryParse(ActivityRangeUrl.Serialize(range), out var parsed));
            Assert.Equal(range, parsed);
            var url = ActivityRangeUrl.Build(Guid.Empty, range, "abc", ChartAxisKind.Distance, 2, "activity-streams");
            Assert.Contains("&stream=abc&axis=distance&section=2#activity-streams", url);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2,0,1,0.5,-,2,3,0.5,-")]
    [InlineData("1,-1,1,0.5,-,2,3,0.5,-")]
    [InlineData("1,0,1,NaN,-,2,3,0.5,-")]
    [InlineData("1,0,1,Infinity,-,2,3,0.5,-")]
    [InlineData("1,0,1,1.1,-,2,3,0.5,-")]
    [InlineData("1,0,1,0.5,3155378976000000000,2,3,0.5,-")]
    [InlineData("1,0,1,0.5,-,2,3,0.5,-,extra")]
    public void Malformed_links_do_not_throw_or_produce_a_range(string? text)
    {
        Assert.False(ActivityRangeUrl.TryParse(text, out var range));
        Assert.Null(range);
    }

    [Fact]
    public void Whole_activity_urls_do_not_invent_an_interval() =>
        Assert.Equal($"/activities/{Guid.Empty}", ActivityRangeUrl.Build(Guid.Empty, null, "unused"));
}
