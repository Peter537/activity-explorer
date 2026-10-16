using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using Microsoft.AspNetCore.Http;

namespace ActivityExplorer.Tests;

public sealed class SharedSearchMapParserTests
{
    [Fact]
    public void Map_endpoint_preserves_the_complete_shared_activity_selection()
    {
        var owner = Guid.NewGuid();
        var tag = Guid.NewGuid();
        var excluded = Guid.NewGuid();
        var query = Parse($"?ownerId={owner}&tagOwner={owner}&sport=cycling&q=Tr%C3%A6ning&textMode=words&power=yes&device=Recorder&deviceExact=yes&gear=Bike&indoor=no&gps=yes&minDistance=1000&maxDistance=2000&minMoving=60&maxMoving=120&minAscent=0&maxAscent=50&tags={tag}&excludeTags={excluded}&tagMode=all&tagPresence=tagged");
        var criteria = query.Criteria!;
        Assert.Equal(SportKind.Cycling, criteria.Sport);
        Assert.Equal("Træning", criteria.Search);
        Assert.Equal(ActivityTextMode.Words, criteria.TextMode);
        Assert.True(criteria.HasPower);
        Assert.True(criteria.DeviceExact);
        Assert.Equal("Recorder", criteria.Device);
        Assert.Equal("Bike", criteria.Gear);
        Assert.False(criteria.IsIndoor);
        Assert.True(criteria.HasGps);
        Assert.Equal(1000, criteria.MinDistanceMeters);
        Assert.Equal(2000, criteria.MaxDistanceMeters);
        Assert.Equal(60, criteria.MinMovingSeconds);
        Assert.Equal(120, criteria.MaxMovingSeconds);
        Assert.Equal(0, criteria.MinAscentMeters);
        Assert.Equal(50, criteria.MaxAscentMeters);
        Assert.Equal([tag], criteria.TagIds);
        Assert.Equal([excluded], criteria.ExcludedTagIds);
        Assert.Equal(ActivityTagMode.All, criteria.TagMode);
        Assert.Equal(ActivityTagPresence.Tagged, criteria.TagPresence);
        Assert.Equal(ActivityTextMode.Literal, Parse("?q=Literal+phrase").Criteria!.TextMode);
    }

    [Fact]
    public void Map_endpoint_rejects_tag_scope_mismatch_before_querying()
    {
        var owner = Guid.NewGuid();
        var tag = Guid.NewGuid();
        Assert.Throws<BadHttpRequestException>(() => Parse($"?ownerId={owner}&tagOwner={Guid.NewGuid()}&tags={tag}"));
        Assert.Throws<BadHttpRequestException>(() => Parse($"?tagOwner={owner}&tags={tag}"));
        Assert.Throws<BadHttpRequestException>(() => Parse($"?ownerId={owner}&tags={tag}"));
        Assert.Throws<BadHttpRequestException>(() => Parse("?minDistance=NaN"));
        Assert.Throws<BadHttpRequestException>(() => Parse("?gps=invalid"));
    }

    private static MapQuery Parse(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new(query);
        return MapQueryParser.Parse(context.Request);
    }
}
