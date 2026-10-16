using System.Text.Json;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace ActivityExplorer.Tests;

public sealed class ActivitySearchQueryTests
{
    [Fact]
    public void Shared_search_round_trip_preserves_unicode_phrases_exact_units_and_owner_scoped_tags()
    {
        var owner = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var excluded = Guid.NewGuid();
        var criteria = new ActivityCriteria(SportKind.Cycling, "Træning \"ved søen\" + intervaller & kaffe", true,
            " Recorder Æ ", [second, first, second], ActivityTextMode.Words, true, " Bike Å ", IsIndoor: false,
            HasGps: true, MinDistanceMeters: 1000.125, MaxDistanceMeters: 9000.25,
            MinMovingSeconds: 60.000001, MaxMovingSeconds: 3600.5, MinAscentMeters: 0, MaxAscentMeters: 50.25,
            TagMode: ActivityTagMode.All, ExcludedTagIds: [excluded], TagPresence: ActivityTagPresence.Tagged);
        var url = ActivitySearchQuery.Build("/map", criteria, owner, new Dictionary<string, string?>
        {
            ["period"] = "custom",
            ["from"] = "2026-10-01",
            ["to"] = "2026-10-31",
            ["layers"] = "activities"
        });
        var restored = ActivitySearchQuery.Read(url);
        Assert.Equal(JsonSerializer.Serialize(ActivityCriteriaValidation.Normalize(criteria)), JsonSerializer.Serialize(restored.Criteria));
        Assert.Equal(owner, restored.TagOwner);
        var values = QueryHelpers.ParseQuery(new Uri("http://localhost" + url).Query);
        Assert.Equal("2026-10-01", values["from"].ToString());
        Assert.Equal("2026-10-31", values["to"].ToString());
        Assert.Equal("activities", values["layers"].ToString());
        Assert.Equal("1000.125", values["minDistance"].ToString());
        Assert.Equal("60.000001", values["minMoving"].ToString());
    }

    [Fact]
    public void Replacing_criteria_clears_old_scope_fields_and_preserves_reporting_context_and_fragment()
    {
        var oldOwner = Guid.NewGuid();
        var oldTag = Guid.NewGuid();
        var original = $"http://localhost:8342/map?period=custom&from=2026-10-01&to=2026-10-31&layers=routes&q=old&textMode=words&gear=Old&tags={oldTag}&excludeTags={Guid.NewGuid()}&tagOwner={oldOwner}&deviceExact=yes&minDistance=5000#map-filters";
        var replaced = ActivitySearchQuery.WithCriteria(original,
            new(Sport: SportKind.Rowing, Search: "Literal phrase", TagPresence: ActivityTagPresence.Untagged), null);
        var uri = new Uri(replaced);
        var values = QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal("#map-filters", uri.Fragment);
        Assert.Equal("custom", values["period"].ToString());
        Assert.Equal("2026-10-01", values["from"].ToString());
        Assert.Equal("2026-10-31", values["to"].ToString());
        Assert.Equal("routes", values["layers"].ToString());
        Assert.False(values.ContainsKey("gear"));
        Assert.False(values.ContainsKey("tags"));
        Assert.False(values.ContainsKey("excludeTags"));
        Assert.False(values.ContainsKey("tagOwner"));
        Assert.False(values.ContainsKey("deviceExact"));
        Assert.False(values.ContainsKey("minDistance"));
        var restored = ActivitySearchQuery.Read(replaced);
        Assert.Equal("Literal phrase", restored.Criteria.Search);
        Assert.Equal(ActivityTextMode.Literal, restored.Criteria.TextMode);
        Assert.Equal(SportKind.Rowing, restored.Criteria.Sport);
        Assert.Equal(ActivityTagPresence.Untagged, restored.Criteria.TagPresence);
        Assert.Null(restored.TagOwner);
    }

    [Fact]
    public void Shared_view_uses_the_applied_sport_and_blocks_profile_changes_for_tagged_bookmarks()
    {
        var owner = Guid.NewGuid();
        var tag = Guid.NewGuid();
        var navigation = new SearchNavigation($"http://localhost/map?sport=cycling&tags={tag}&tagOwner={owner}");
        var view = new SearchView(navigation);
        Assert.Equal(SportKind.Rowing, view.Read(SportKind.Rowing, owner).Sport);
        Assert.Throws<InvalidOperationException>(() => view.Read(null, Guid.NewGuid()));
        Assert.Equal(owner, view.TagOwner);
        Assert.Throws<InvalidOperationException>(() => view.Read(null, null));
        Assert.Throws<ArgumentException>(() => ActivitySearchQuery.Build("/map", new(TagIds: [tag]), null));
        navigation.SetUri("http://localhost/map?q=Legacy+phrase");
        var cleared = view.Read(null, null);
        Assert.Null(view.TagOwner);
        Assert.Equal(ActivityTextMode.Literal, cleared.TextMode);
        Assert.Equal("Legacy phrase", cleared.Search);
    }

    private sealed class SearchView(NavigationManager navigation) : ActivitySearchViewBase
    {
        public Guid? TagOwner => SearchTagOwner;
        public ActivityCriteria Read(SportKind? sport, Guid? owner)
        {
            SearchNavigation = navigation;
            return ReadSharedCriteria(sport, owner);
        }
    }

    private sealed class SearchNavigation : NavigationManager
    {
        public SearchNavigation(string uri) => Initialize("http://localhost/", uri);
        public void SetUri(string uri) => Uri = uri;
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }
}
