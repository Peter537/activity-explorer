using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Services;
using Microsoft.AspNetCore.WebUtilities;

namespace ActivityExplorer.Tests;

public sealed class RecordPeriodNavigationTests
{
    [Theory]
    [InlineData(ReportingPreset.ThisMonth, "this-month")]
    [InlineData(ReportingPreset.LastYear, "last-year")]
    [InlineData(ReportingPreset.YearToDate, "year-to-date")]
    public void Record_and_attempt_links_preserve_relative_period_scope_and_profile(ReportingPreset preset, string slug)
    {
        var owner = Guid.Parse("00000000-0000-0000-0000-000000000041");
        var selection = new ReportingDateSelection(preset);
        var overview = RecordDisplay.RecordsUrl(SportKind.Rowing, RecordScope.Indoor, selection);
        var attempts = RecordDisplay.AttemptsUrl(SportKind.Rowing, RecordKind.PowerCurve, "2 min", RecordScope.Indoor,
            owner, true, 3, selection);
        var query = QueryHelpers.ParseQuery(new Uri("https://local.invalid" + attempts).Query);
        Assert.Equal($"/records?sport=rowing&scope=indoor&period={slug}", overview);
        Assert.Equal("rowing", query["sport"].ToString());
        Assert.Equal("indoor", query["scope"].ToString());
        Assert.Equal(owner.ToString(), query["owner"].ToString());
        Assert.Equal("2 min", query["key"].ToString());
        Assert.Equal("multiple", query["mode"].ToString());
        Assert.Equal("3", query["page"].ToString());
        Assert.Equal(slug, query["period"].ToString());
        Assert.False(query.ContainsKey("from"));
        Assert.False(query.ContainsKey("to"));
    }

    [Fact]
    public void Record_navigation_preserves_custom_period_without_changing_all_time_or_all_profile_defaults()
    {
        var period = new ReportingDateSelection(ReportingPreset.Custom, new(2026, 1, 2), new(2026, 1, 31));
        var url = RecordDisplay.AttemptsUrl(SportKind.Cycling, RecordKind.DistanceEffort, "1 km", RecordScope.Outdoor, null, period: period);
        var query = QueryHelpers.ParseQuery(new Uri("https://local.invalid" + url).Query);
        Assert.False(query.ContainsKey("owner"));
        Assert.Equal("custom", query["period"].ToString());
        Assert.Equal("2026-01-02", query["from"].ToString());
        Assert.Equal("2026-01-31", query["to"].ToString());
        Assert.Equal(period, ReportingDates.Parse(query["period"], query["from"], query["to"]));
        Assert.Equal("/records?sport=cycling&scope=outdoor&period=custom&from=2026-01-02&to=2026-01-31",
            RecordDisplay.RecordsUrl(SportKind.Cycling, RecordScope.Outdoor, period));
        Assert.Equal("/records", RecordDisplay.RecordsUrl(null, RecordScope.All, new(ReportingPreset.AllTime)));
        Assert.Equal("/records/attempts?sport=cycling&kind=distanceeffort&key=1%20km",
            RecordDisplay.AttemptsUrl(SportKind.Cycling, RecordKind.DistanceEffort, "1 km", RecordScope.All, null));
    }
}
