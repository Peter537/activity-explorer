using System.Text.Json;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Legacy_saved_search_reads_and_exports_do_not_rewrite_or_change_literal_membership()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Legacy saved search");
        var services = OrganizationLifecycleServices(setup);
        var tag = await services.Organization.CreateTagAsync(owner, "Climbing");
        var activity = await setup.SeedActivityAsync(owner, "Evening climb", SportKind.Cycling);
        await services.Organization.SetActivityTagsAsync(owner, activity, [tag], 0);
        var json = JsonSerializer.Serialize(new
        {
            Version = 1,
            Sport = SportKind.Cycling,
            Search = "climb evening",
            HasPower = (bool?)null,
            Device = (string?)null,
            Tags = new[] { new SavedTagReference(tag, "Climbing") },
            Sort = "start-desc",
            Dates = new ReportingDateSelection(ReportingPreset.AllTime)
        });
        var id = await SeedSavedSearchJsonAsync(setup, owner, "Literal legacy", json);

        var search = Assert.Single(await services.Organization.ListSavedSearchesAsync(owner));
        Assert.Null(search.Error);
        Assert.Equal(1, search.Criteria!.Version);
        Assert.Equal(ActivityTextMode.Literal, search.Criteria.ToActivityCriteria().TextMode);
        Assert.Empty((await services.Activities.SearchAsync(new(owner, Criteria: search.Criteria.ToActivityCriteria()))).Items);
        Assert.Single((await services.Activities.SearchAsync(new(owner,
            Criteria: search.Criteria.ToActivityCriteria() with { TextMode = ActivityTextMode.Words }))).Items);
        Assert.Equal(1, Assert.Single(await services.Organization.ListTagsAsync(owner)).SavedSearchCount);
        using (var exported = JsonDocument.Parse((await services.Profiles.ExportAsync(owner)).Json))
        {
            var criteria = Assert.Single(exported.RootElement.GetProperty("savedSearches").EnumerateArray()).GetProperty("criteria");
            Assert.Equal(1, criteria.GetProperty("Version").GetInt32());
            Assert.False(criteria.TryGetProperty("TextMode", out _));
        }
        await using (var db = await setup.Factory.CreateDbContextAsync())
            Assert.Equal(json, (await db.SavedSearches.SingleAsync()).CriteriaJson);

        await services.Organization.RenameTagAsync(owner, tag, "Hills");
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            using var renamed = JsonDocument.Parse((await db.SavedSearches.SingleAsync()).CriteriaJson);
            Assert.Equal(1, renamed.RootElement.GetProperty("Version").GetInt32());
            Assert.False(renamed.RootElement.TryGetProperty("TextMode", out _));
            Assert.Equal("Hills", Assert.Single(renamed.RootElement.GetProperty("Tags").EnumerateArray()).GetProperty("Name").GetString());
        }

        var legacy = Assert.Single(await services.Organization.ListSavedSearchesAsync(owner));
        await services.Organization.SaveSavedSearchAsync(owner, legacy.Name, legacy.Criteria!, id);
        var replaced = Assert.Single(await services.Organization.ListSavedSearchesAsync(owner));
        Assert.Equal(2, replaced.Criteria!.Version);
        Assert.Equal(ActivityTextMode.Literal, replaced.Criteria.TextMode);
        Assert.Empty((await services.Activities.SearchAsync(new(owner, Criteria: replaced.Criteria.ToActivityCriteria()))).Items);
    }

    [Fact]
    public async Task Version_two_searches_keep_included_and_excluded_references_owned_named_and_repairable()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Extended saved search");
        var other = await setup.SeedOwnerAsync("Foreign saved search tags");
        var services = OrganizationLifecycleServices(setup);
        var include = await services.Organization.CreateTagAsync(owner, "Training");
        var exclude = await services.Organization.CreateTagAsync(owner, "Commute");
        var foreign = await services.Organization.CreateTagAsync(other, "Foreign");
        var criteria = new SavedSearchCriteria(2, SportKind.Cycling, "hill \"evening ride\"", true, "Edge",
            [new(include, "Old include name")], "distance-desc", new(ReportingPreset.YearToDate),
            TextMode: ActivityTextMode.Words, DeviceExact: true, Gear: "Road bike", IsIndoor: false, HasGps: true,
            MinDistanceMeters: 1000, MaxDistanceMeters: 50000, MinMovingSeconds: 0, MaxMovingSeconds: 7200,
            MinAscentMeters: 0, MaxAscentMeters: 1000, TagMode: ActivityTagMode.All,
            ExcludedTags: [new(exclude, "Old exclude name")], TagPresence: ActivityTagPresence.Tagged);
        var id = await services.Organization.SaveSavedSearchAsync(owner, "Hilly training", criteria);

        var restored = Assert.Single(await services.Organization.ListSavedSearchesAsync(owner)).Criteria!;
        Assert.Equal(2, restored.Version);
        Assert.Equal(JsonSerializer.Serialize(criteria.ToActivityCriteria()), JsonSerializer.Serialize(restored.ToActivityCriteria()));
        Assert.Equal("Training", Assert.Single(restored.Tags).Name);
        Assert.Equal("Commute", Assert.Single(restored.ExcludedTags!).Name);
        Assert.All(await services.Organization.ListTagsAsync(owner), tag => Assert.Equal(1, tag.SavedSearchCount));
        await Assert.ThrowsAsync<InvalidOperationException>(() => services.Organization.SaveSavedSearchAsync(owner, "Foreign exclusion",
            criteria with { ExcludedTags = [new(foreign, "Foreign")] }));
        await Assert.ThrowsAsync<ArgumentException>(() => services.Organization.SaveSavedSearchAsync(owner, "Contradiction",
            criteria with { ExcludedTags = [new(include, "Training")] }));
        await Assert.ThrowsAsync<ArgumentException>(() => services.Organization.SaveSavedSearchAsync(owner, "Legacy extension", criteria with { Version = 1 }));

        await services.Organization.RenameTagAsync(owner, exclude, "Transport");
        using (var exported = JsonDocument.Parse((await services.Profiles.ExportAsync(owner)).Json))
        {
            var saved = Assert.Single(exported.RootElement.GetProperty("savedSearches").EnumerateArray()).GetProperty("criteria");
            Assert.Equal(2, saved.GetProperty("Version").GetInt32());
            Assert.Equal("Transport", Assert.Single(saved.GetProperty("ExcludedTags").EnumerateArray()).GetProperty("Name").GetString());
            Assert.Equal(50000, saved.GetProperty("MaxDistanceMeters").GetDouble());
        }
        await services.Organization.DeleteTagAsync(owner, exclude);
        var broken = Assert.Single(await services.Organization.ListSavedSearchesAsync(owner));
        Assert.Equal(new SavedTagReference(exclude, "Transport"), Assert.Single(broken.MissingTags));
        Assert.Equal(exclude, Assert.Single(broken.Criteria!.ExcludedTags!).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => services.Organization.SaveSavedSearchAsync(owner, broken.Name, broken.Criteria!, id));

        var replacement = await services.Organization.CreateTagAsync(owner, "Travel");
        await services.Organization.SaveSavedSearchAsync(owner, broken.Name,
            broken.Criteria! with { ExcludedTags = [new(replacement, "Travel")] }, id);
        var repaired = Assert.Single(await services.Organization.ListSavedSearchesAsync(owner));
        Assert.Empty(repaired.MissingTags);
        Assert.Equal(ActivityTagMode.All, repaired.Criteria!.TagMode);
        Assert.Equal(ActivityTextMode.Words, repaired.Criteria.TextMode);
        Assert.Equal(criteria.MaxMovingSeconds, repaired.Criteria.MaxMovingSeconds);
        Assert.Equal(replacement, Assert.Single(repaired.Criteria.ExcludedTags!).Id);
    }

    [Theory]
    [InlineData("{\"Version\":3}")]
    [InlineData("{\"Version\":2,\"Unexpected\":true}")]
    [InlineData("{\"Version\":\"2\"}")]
    [InlineData("null")]
    public async Task Unsupported_saved_json_is_reported_without_rewriting(string json)
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Unreadable saved criteria");
        await SeedSavedSearchJsonAsync(setup, owner, "Unsupported", json);
        var search = Assert.Single(await OrganizationService(setup).ListSavedSearchesAsync(owner));
        Assert.Null(search.Criteria);
        Assert.NotNull(search.Error);
        await using var db = await setup.Factory.CreateDbContextAsync();
        Assert.Equal(json, (await db.SavedSearches.SingleAsync()).CriteriaJson);
    }

    private static async Task<Guid> SeedSavedSearchJsonAsync(DatabaseSetup setup, Guid owner, string name, string json)
    {
        await using var db = await setup.Factory.CreateDbContextAsync();
        var saved = new SavedSearch { OwnerId = owner, Name = name, NormalizedName = name.ToUpperInvariant(), CriteriaJson = json };
        db.SavedSearches.Add(saved);
        await db.SaveChangesAsync();
        return saved.Id;
    }
}
