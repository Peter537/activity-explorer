using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Services;
using Microsoft.AspNetCore.Http;

namespace ActivityExplorer.Tests;

public sealed class MapQueryParserTests
{
    [Theory]
    [InlineData("?west=-20&south=-40&east=30&north=50&zoom=7", -20, -40, 30, 50, 7)]
    [InlineData("?west=-180&south=-90&east=180&north=90&zoom=0", -180, -90, 180, 90, 0)]
    [InlineData("?west=170&south=-10&east=-170&north=10&zoom=4", 170, -10, -170, 10, 4)]
    public void Parse_accepts_normal_full_world_and_antimeridian_bounds(
        string queryString,
        double west,
        double south,
        double east,
        double north,
        int zoom)
    {
        var query = Parse(queryString);

        Assert.Equal(west, query.West);
        Assert.Equal(south, query.South);
        Assert.Equal(east, query.East);
        Assert.Equal(north, query.North);
        Assert.Equal(zoom, query.Zoom);
    }

    [Fact]
    public void Parse_preserves_filters_with_normalized_bounds()
    {
        var ownerId = Guid.NewGuid();
        var query = Parse($"?ownerId={ownerId}&sport=Running&from=2026-01-02&to=2026-02-03&west=-170&south=-80&east=170&north=80");

        Assert.Equal(ownerId, query.OwnerId);
        Assert.Equal(SportKind.Running, query.Sport);
        Assert.Equal(new DateOnly(2026, 1, 2), query.From);
        Assert.Equal(new DateOnly(2026, 2, 3), query.To);
    }

    [Fact]
    public void Relative_period_takes_precedence_and_preserves_captured_reference_instant()
    {
        var query = Parse("?period=this-week&from=bad&to=2026-99-99&asOf=2026-10-09T23%3A30%3A00.0000000%2B02%3A00");

        Assert.Equal(ReportingPreset.ThisWeek, query.Period);
        Assert.Null(query.From);
        Assert.Null(query.To);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 21, 30, 0, TimeSpan.Zero), query.AsOfUtc);
    }

    [Theory]
    [InlineData("?west=-10&south=-20&east=30")]
    [InlineData("?west=NaN&south=-20&east=30&north=40")]
    [InlineData("?west=-181&south=-20&east=30&north=40")]
    [InlineData("?west=-10&south=-91&east=30&north=40")]
    [InlineData("?west=-10&south=50&east=30&north=40")]
    [InlineData("?west=-10&south=-20&east=30&north=40&zoom=25")]
    [InlineData("?from=2026-02-03&to=2026-01-02")]
    [InlineData("?from=2026-02-30")]
    [InlineData("?to=bad")]
    [InlineData("?period=unknown")]
    [InlineData("?period=custom&from=bad")]
    [InlineData("?period=today&asOf=not-a-time")]
    public void Parse_rejects_partial_non_finite_and_out_of_range_queries(string queryString)
    {
        Assert.Throws<BadHttpRequestException>(() => Parse(queryString));
    }

    private static Core.Models.MapQuery Parse(string queryString)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(queryString);
        return MapQueryParser.Parse(context.Request);
    }

    [Theory]
    [InlineData("?view=frequency&from=2026-03-01&to=2026-04-01", false)]
    [InlineData("?view=exploration&coverage=cumulative&through=2026-03-20&from=2026-03-01&to=2026-04-01", true)]
    public void Exploration_endpoint_preserves_date_range_independently_of_coverage(string queryString, bool cumulative)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new(queryString);
        var query = ExplorationQueryInput.FromRequest(context.Request, MapQueryParser.Parse(context.Request));

        Assert.Equal(ReportingPreset.Custom, query.Dates.Preset);
        Assert.Equal(new DateOnly(2026, 3, 1), query.Dates.From);
        Assert.Equal(new DateOnly(2026, 4, 1), query.Dates.To);
        Assert.Equal(cumulative, query.Cumulative);
        Assert.Equal(cumulative ? new DateOnly(2026, 3, 20) : (DateOnly?)null, query.Through);
    }

    [Theory]
    [InlineData("?view=unknown")]
    [InlineData("?view=exploration&coverage=unknown")]
    [InlineData("?view=exploration&through=2026-02-30")]
    public void Exploration_endpoint_rejects_invalid_view_coverage_and_dates(string queryString)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new(queryString);
        Assert.Throws<ArgumentException>(() => ExplorationQueryInput.FromRequest(context.Request, MapQueryParser.Parse(context.Request)));
    }
}
