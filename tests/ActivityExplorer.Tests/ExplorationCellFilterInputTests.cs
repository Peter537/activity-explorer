using ActivityExplorer.Web.Services;
using Microsoft.AspNetCore.Http;

namespace ActivityExplorer.Tests;

public sealed class ExplorationCellFilterInputTests
{
    [Theory]
    [InlineData("?view=exploration&newOnly=yes", true)]
    [InlineData("?view=exploration&newOnly=no", false)]
    [InlineData("?view=exploration", false)]
    [InlineData("?view=frequency", false)]
    public void New_cell_query_is_typed_and_defaults_to_all_visited(string queryString, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new(queryString);
        var query = ExplorationQueryInput.FromRequest(context.Request, MapQueryParser.Parse(context.Request));
        Assert.Equal(expected, query.NewOnly);
    }

    [Theory]
    [InlineData("?view=exploration&newOnly=invalid")]
    [InlineData("?view=frequency&newOnly=yes")]
    [InlineData("?view=lines&newOnly=yes")]
    public void New_cell_query_rejects_invalid_or_non_exploration_display_filters(string queryString)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new(queryString);
        Assert.Throws<ArgumentException>(() => ExplorationQueryInput.FromRequest(context.Request, MapQueryParser.Parse(context.Request)));
    }
}
