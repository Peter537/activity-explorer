using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Activity_criteria_share_complete_membership_across_pages_totals_ids_and_batch_review()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Criteria owner");
        var other = await setup.SeedOwnerAsync("Other criteria owner");
        var organization = OrganizationService(setup);
        var training = await organization.CreateTagAsync(owner, "Training");
        var intervals = await organization.CreateTagAsync(owner, "Intervals");
        var commute = await organization.CreateTagAsync(owner, "Commute");
        var matching = Enumerable.Range(0, 31).Select(index => CriteriaActivity(owner, index)).ToArray();
        var wrongSport = CriteriaActivity(owner, 40); wrongSport.Sport = SportKind.Walking;
        var wrongText = CriteriaActivity(owner, 41); wrongText.Description = "No required phrase";
        var wrongPower = CriteriaActivity(owner, 42); wrongPower.HasPower = false;
        var wrongDevice = CriteriaActivity(owner, 43); wrongDevice.DeviceName = "Recorder Æ plus";
        var wrongGear = CriteriaActivity(owner, 44); wrongGear.GearName = "Bike Å plus";
        var wrongIndoor = CriteriaActivity(owner, 45); wrongIndoor.IsIndoor = false;
        var wrongGps = CriteriaActivity(owner, 46); wrongGps.HasGps = false;
        var tooShort = CriteriaActivity(owner, 47); tooShort.DistanceMeters = 999;
        var tooLong = CriteriaActivity(owner, 48); tooLong.DistanceMeters = 4001;
        var tooFast = CriteriaActivity(owner, 49); tooFast.MovingTimeSeconds = 99;
        var tooSlow = CriteriaActivity(owner, 50); tooSlow.MovingTimeSeconds = 401;
        var tooFlat = CriteriaActivity(owner, 51); tooFlat.ElevationGainMeters = 9;
        var tooSteep = CriteriaActivity(owner, 52); tooSteep.ElevationGainMeters = 41;
        var before = CriteriaActivity(owner, 53); before.StartTimeUtc = ReportingInstant("2026-10-08T21:59:59Z");
        var after = CriteriaActivity(owner, 54); after.StartTimeUtc = ReportingInstant("2026-10-09T22:00:00Z");
        var missingTag = CriteriaActivity(owner, 55);
        var excludedTag = CriteriaActivity(owner, 56);
        var foreign = CriteriaActivity(other, 57);
        Activity[] decoys = [wrongSport, wrongText, wrongPower, wrongDevice, wrongGear, wrongIndoor, wrongGps,
            tooShort, tooLong, tooFast, tooSlow, tooFlat, tooSteep, before, after, missingTag, excludedTag];
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.AddRange(matching.Concat(decoys).Append(foreign));
            foreach (var activity in matching.Concat(decoys))
            {
                activity.Tags.Add(new ActivityTag { ActivityId = activity.Id, TagId = training });
                if (activity != missingTag)
                    activity.Tags.Add(new ActivityTag { ActivityId = activity.Id, TagId = intervals });
                if (activity == excludedTag)
                    activity.Tags.Add(new ActivityTag { ActivityId = activity.Id, TagId = commute });
            }
            await db.SaveChangesAsync();
        }
        var criteria = new ActivityCriteria(SportKind.Cycling, "øVELSE \"TRÆNING VED SØEN\"", true, "recorder æ",
            [training, intervals], ActivityTextMode.Words, true, "bike å", false, true, true,
            1000, 4000, 100, 400, 10, 40, ActivityTagMode.All, [commute], ActivityTagPresence.Tagged);
        var filter = new ActivityFilter(OwnerId: owner, From: new(2026, 10, 9), To: new(2026, 10, 9),
            Sort: "distance-asc", Criteria: criteria);
        var service = ReportingService(setup);
        var first = await service.SearchAsync(filter);
        var second = await service.SearchAsync(first.EffectiveFilter with { Page = 2 });
        var expected = matching.Select(activity => activity.Id).ToArray();
        Assert.Equal(expected[..25], first.Items.Select(activity => activity.Id));
        Assert.Equal(expected[25..], second.Items.Select(activity => activity.Id));
        Assert.Equal(31, first.Total);
        var totals = new ActivityTotals(31, matching.Sum(x => x.DistanceMeters), matching.Sum(x => x.MovingTimeSeconds),
            matching.Sum(x => x.ElevationGainMeters));
        Assert.Equal(totals, first.Totals);
        Assert.Equal(totals, second.Totals);
        Assert.Equal(expected, await service.GetMatchingActivityIdsAsync(second.EffectiveFilter));
        var batch = await organization.PrepareBatchAsync(owner, null, second.EffectiveFilter,
            new([], [], GearEditMode.Set, "Reviewed bike"));
        Assert.Equal(expected.Order(), batch.Members.Select(x => x.Id).Order());
        var savedCriteria = new SavedSearchCriteria(2, criteria.Sport, criteria.Search, criteria.HasPower, criteria.Device,
            [new(training, "Training"), new(intervals, "Intervals")], filter.Sort,
            new(ReportingPreset.Custom, filter.From, filter.To), criteria.TextMode, criteria.DeviceExact,
            criteria.Gear, criteria.GearUnassigned, criteria.IsIndoor, criteria.HasGps,
            criteria.MinDistanceMeters, criteria.MaxDistanceMeters, criteria.MinMovingSeconds, criteria.MaxMovingSeconds,
            criteria.MinAscentMeters, criteria.MaxAscentMeters, criteria.TagMode, [new(commute, "Commute")], criteria.TagPresence);
        await organization.SaveSavedSearchAsync(owner, "Combined criteria", savedCriteria);
        var saved = Assert.Single(await organization.ListSavedSearchesAsync(owner)).Criteria!;
        Assert.Equal(expected, await service.GetMatchingActivityIdsAsync(filter with { Criteria = saved.ToActivityCriteria() }));
    }

    [Fact]
    public async Task Activity_word_search_matches_Unicode_across_fields_and_phrases_while_literal_scope_stays_unchanged()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Text criteria");
        var across = CriteriaActivity(owner, 1);
        across.Title = "Æble Øvelse"; across.Description = "Træning ved søen Århus";
        var reversedPhrase = CriteriaActivity(owner, 2);
        reversedPhrase.Title = "Århus øvelse"; reversedPhrase.Description = "Søen ved træning";
        var literal = CriteriaActivity(owner, 3);
        literal.Title = "Æble Øvelse Træning ved søen Århus";
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.AddRange(across, reversedPhrase, literal);
            await db.SaveChangesAsync();
        }
        var service = ReportingService(setup);
        var words = new ActivityFilter(OwnerId: owner, Criteria: new(Search: "ÅRHUS øvelse \"TRÆNING VED SØEN\"",
            TextMode: ActivityTextMode.Words));
        Assert.Equal(new[] { across.Id, literal.Id }.Order(), (await service.GetMatchingActivityIdsAsync(words)).Order());
        Assert.Equal([literal.Id], await service.GetMatchingActivityIdsAsync(new(OwnerId: owner, Search: "Øvelse Træning")));
        Assert.Empty(await service.GetMatchingActivityIdsAsync(new(OwnerId: owner, Search: "øvelse træning")));
        var legacy = new SavedSearchCriteria(1, null, "Øvelse Træning", null, null, [], "start-desc", new(ReportingPreset.AllTime));
        Assert.Equal([literal.Id], await service.GetMatchingActivityIdsAsync(new(OwnerId: owner, Criteria: legacy.ToActivityCriteria())));
    }

    [Fact]
    public async Task Activity_tag_presence_any_all_exclusion_and_equipment_options_are_owner_scoped()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Tag criteria");
        var other = await setup.SeedOwnerAsync("Other tag criteria");
        var organization = OrganizationService(setup);
        var firstTag = await organization.CreateTagAsync(owner, "First");
        var secondTag = await organization.CreateTagAsync(owner, "Second");
        var foreignTag = await organization.CreateTagAsync(other, "Foreign");
        var first = CriteriaActivity(owner, 1);
        first.DeviceName = "Recorder Æ"; first.GearName = "Bike Å";
        var both = CriteriaActivity(owner, 2);
        both.DeviceName = "recorder æ"; both.GearName = "bike å";
        var none = CriteriaActivity(owner, 3);
        none.DeviceName = "  "; none.GearName = null;
        var whitespace = CriteriaActivity(owner, 4);
        whitespace.DeviceName = null; whitespace.GearName = "  ";
        var foreign = CriteriaActivity(other, 5);
        foreign.DeviceName = "Other recorder"; foreign.GearName = "Other bike";
        first.Tags.Add(new() { ActivityId = first.Id, TagId = firstTag });
        both.Tags.Add(new() { ActivityId = both.Id, TagId = firstTag });
        both.Tags.Add(new() { ActivityId = both.Id, TagId = secondTag });
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.AddRange(first, both, none, whitespace, foreign);
            await db.SaveChangesAsync();
        }
        var service = ReportingService(setup);
        async Task<Guid[]> Ids(ActivityCriteria criteria) => (await service.GetMatchingActivityIdsAsync(
            new(OwnerId: owner, Criteria: criteria))).Order().ToArray();
        Assert.Equal(new[] { first.Id, both.Id }.Order(), await Ids(new(TagIds: [firstTag, secondTag])));
        Assert.Equal([both.Id], await Ids(new(TagIds: [firstTag, secondTag], TagMode: ActivityTagMode.All)));
        Assert.Equal([first.Id], await Ids(new(TagIds: [firstTag], ExcludedTagIds: [secondTag])));
        Assert.Equal(new[] { none.Id, whitespace.Id }.Order(), await Ids(new(TagPresence: ActivityTagPresence.Untagged)));
        Assert.Equal(new[] { first.Id, both.Id }.Order(), await Ids(new(TagPresence: ActivityTagPresence.Tagged)));
        Assert.Equal(new[] { none.Id, whitespace.Id }.Order(), await Ids(new(GearUnassigned: true)));
        Assert.Equal(new[] { first.Id, both.Id }.Order(), await Ids(new(Device: "RECORDER Æ", DeviceExact: true, Gear: "BIKE Å")));
        Assert.Equal([first.Id], await Ids(new(Device: "Recorder")));
        var options = await service.GetFilterOptionsAsync(owner);
        Assert.Equal(["Recorder Æ"], options.Devices);
        Assert.Equal(["Bike Å"], options.Gear);
        Assert.Equal(["Other recorder"], (await service.GetFilterOptionsAsync(other)).Devices);
        Assert.Equal(2, (await service.GetFilterOptionsAsync(null)).Devices.Count);
        Assert.Empty((await service.GetFilterOptionsAsync(Guid.NewGuid())).Gear);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(new(Criteria: new(TagIds: [firstTag]))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync(new(OwnerId: owner, Criteria: new(ExcludedTagIds: [foreignTag]))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync(new(OwnerId: owner, Criteria: new(TagIds: [Guid.NewGuid()]))));
    }

    private static Activity CriteriaActivity(Guid owner, int index) => new()
    {
        OwnerId = owner,
        Sport = SportKind.Cycling,
        Title = $"Øvelse {index}",
        Description = "Træning ved søen",
        NaturalFingerprint = Guid.NewGuid().ToString("N"),
        StartTimeUtc = ReportingInstant("2026-10-08T22:00:00Z").AddSeconds(index),
        DistanceMeters = 1000 + index % 31 * 100,
        MovingTimeSeconds = 100 + index % 31 * 10,
        ElevationGainMeters = 10 + index % 31,
        DeviceName = " Recorder Æ ",
        GearName = " Bike Å ",
        HasPower = true,
        HasGps = true,
        IsIndoor = true
    };
}
