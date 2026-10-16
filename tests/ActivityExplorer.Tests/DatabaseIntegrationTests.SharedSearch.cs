using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Shared_search_reconciles_complete_rows_comparison_calendar_and_eligible_map_lines()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Shared search athlete");
        var other = await setup.SeedOwnerAsync("Other shared search athlete");
        var organization = OrganizationService(setup);
        var training = await organization.CreateTagAsync(owner, "Training");
        var intervals = await organization.CreateTagAsync(owner, "Intervals");
        var commute = await organization.CreateTagAsync(owner, "Commute");
        var selected = Enumerable.Range(0, 31).Select(index => CriteriaActivity(owner, index)).ToArray();
        selected[0].HasGps = false;
        selected[1].HasGps = false;
        var comparison = Enumerable.Range(1, 5).Select(index => CriteriaActivity(owner, 100 + index)).ToArray();
        foreach (var activity in comparison) activity.StartTimeUtc = ReportingInstant("2025-10-08T22:00:00Z").AddSeconds(activity.DistanceMeters);
        var wrongText = CriteriaActivity(owner, 40); wrongText.Title = "Unrelated"; wrongText.Description = null;
        var wrongPower = CriteriaActivity(owner, 41); wrongPower.HasPower = false;
        var wrongGear = CriteriaActivity(owner, 42); wrongGear.GearName = "Other gear";
        var wrongIndoor = CriteriaActivity(owner, 43); wrongIndoor.IsIndoor = false;
        var wrongSport = CriteriaActivity(owner, 44); wrongSport.Sport = SportKind.Walking;
        var excluded = CriteriaActivity(owner, 45);
        var missingTag = CriteriaActivity(owner, 46);
        var wrongDate = CriteriaActivity(owner, 47); wrongDate.StartTimeUtc = ReportingInstant("2026-11-01T12:00:00Z");
        var comparisonExcluded = CriteriaActivity(owner, 48); comparisonExcluded.StartTimeUtc = ReportingInstant("2025-10-09T12:00:00Z");
        var foreign = CriteriaActivity(other, 49);
        Activity[] decoys = [wrongText, wrongPower, wrongGear, wrongIndoor, wrongSport, excluded, missingTag, wrongDate, comparisonExcluded];
        var geometry = GeometryCodec.ToWkb(TestSupport.Track(3));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            foreach (var activity in selected.Concat(comparison).Concat(decoys))
            {
                activity.SimplifiedGeometryWkb = activity == selected[2] ? null : geometry;
                activity.Tags.Add(new() { ActivityId = activity.Id, TagId = training });
                if (activity != missingTag) activity.Tags.Add(new() { ActivityId = activity.Id, TagId = intervals });
                if (activity == excluded || activity == comparisonExcluded)
                    activity.Tags.Add(new() { ActivityId = activity.Id, TagId = commute });
                db.Activities.Add(activity);
            }
            db.Activities.Add(foreign);
            db.StatisticSnapshots.Add(new()
            {
                OwnerId = owner,
                ActivityId = wrongPower.Id,
                Scope = RecordScope.All,
                Sport = SportKind.Cycling,
                Kind = RecordKind.Distance,
                Key = "longest",
                Value = wrongPower.DistanceMeters
            });
            await db.SaveChangesAsync();
        }
        var criteria = new ActivityCriteria(SportKind.Cycling, "øvelse \"TRÆNING VED SØEN\"", true, "RECORDER Æ",
            [training, intervals], ActivityTextMode.Words, true, "bike å", IsIndoor: true,
            MinDistanceMeters: 1000, MaxDistanceMeters: 4000, MinMovingSeconds: 100, MaxMovingSeconds: 400,
            MinAscentMeters: 10, MaxAscentMeters: 40, TagMode: ActivityTagMode.All, ExcludedTagIds: [commute]);
        var dates = new ReportingDateSelection(ReportingPreset.Custom, new(2026, 10, 9), new(2026, 10, 9));
        var service = ReportingService(setup);
        var filter = new ActivityFilter(OwnerId: owner, From: dates.From, To: dates.To, Criteria: criteria);
        var rows = await service.SearchAsync(filter);
        var secondPage = await service.SearchAsync(rows.EffectiveFilter with { Page = 2 });
        Assert.Equal(25, rows.Items.Count);
        Assert.Equal(6, secondPage.Items.Count);
        var dashboard = await service.GetDashboardAsync(new(owner, Sport: SportKind.Walking, Period: dates, Criteria: criteria));
        var calendar = await service.GetTrainingCalendarAsync(new(owner, Sport: SportKind.Walking, Month: new(2026, 10, 1), Criteria: criteria));
        var map = await new MapFeatureService(setup.Factory).GetActivitiesAsync(new(owner, Sport: SportKind.Walking,
            From: dates.From, To: dates.To, Criteria: criteria));
        Assert.Equal(31, rows.Total);
        Assert.Equal(new ActivityTotals(31, selected.Sum(x => x.DistanceMeters), selected.Sum(x => x.MovingTimeSeconds),
            selected.Sum(x => x.ElevationGainMeters)), rows.Totals);
        Assert.Equal(new ReportingTotals(rows.Total, rows.Totals.DistanceMeters, rows.Totals.MovingSeconds,
            rows.Totals.ElevationMeters, 1), dashboard.Totals);
        Assert.Equal(dashboard.Totals, calendar.Totals);
        Assert.Equal(dashboard.Totals, calendar.Days.Single(day => day.Date == dates.From).Totals);
        Assert.Equal(31, dashboard.Trend.Sum(bucket => bucket.Totals.ActivityCount));
        Assert.Equal(new ReportingTotals(5, comparison.Sum(x => x.DistanceMeters), comparison.Sum(x => x.MovingTimeSeconds),
            comparison.Sum(x => x.ElevationGainMeters), 1), dashboard.ComparisonTotals);
        Assert.All(dashboard.Recent, item => Assert.Contains(item.Id, selected.Select(x => x.Id)));
        var expectedMapIds = selected.Where(x => x.HasGps && x.SimplifiedGeometryWkb is not null).Select(x => x.Id).Order().ToArray();
        Assert.Equal(expectedMapIds, map.Features.Select(feature => (Guid)feature.Properties["id"]!).Order());
        Assert.Equal(new MapLayerCoverage(28, 28, 2000), map.Coverage);
        var independentHighlights = await service.GetDashboardAsync(new(owner, Period: dates, Criteria: criteria));
        Assert.Equal(wrongPower.Id, Assert.Single(independentHighlights.Highlights).ActivityId);
    }

    [Fact]
    public async Task Reporting_equipment_groups_match_exact_trimmed_case_insensitive_and_unassigned_drilldowns()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Equipment groups");
        var first = CriteriaActivity(owner, 1); first.DeviceName = " Recorder Æ "; first.GearName = " Bike Å ";
        var second = CriteriaActivity(owner, 2); second.DeviceName = "recorder æ"; second.GearName = "bike å";
        var prefix = CriteriaActivity(owner, 3); prefix.DeviceName = "Recorder Æ plus"; prefix.GearName = null;
        var unassigned = CriteriaActivity(owner, 4); unassigned.DeviceName = null; unassigned.GearName = "  ";
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.AddRange(first, second, prefix, unassigned);
            await db.SaveChangesAsync();
        }
        var service = ReportingService(setup);
        var dashboard = await service.GetDashboardAsync(new(owner, Period: new(ReportingPreset.AllTime)));
        Assert.Equal(2, dashboard.Devices.Count);
        Assert.Equal(2, dashboard.Gear.Count);
        Assert.Contains(new NamedTotal("Recorder Æ", 2), dashboard.Devices);
        Assert.Contains(new NamedTotal("Bike Å", 2), dashboard.Gear);
        Assert.Contains(new NamedTotal("Unassigned", 2, IsUnassigned: true), dashboard.Gear);
        foreach (var device in dashboard.Devices)
        {
            var matches = await service.SearchAsync(new(OwnerId: owner, Criteria: new(Device: device.Name, DeviceExact: true)));
            Assert.Equal(device.Count, matches.Total);
        }
        foreach (var gear in dashboard.Gear)
        {
            var matches = await service.SearchAsync(new(OwnerId: owner,
                Criteria: gear.IsUnassigned ? new(GearUnassigned: true) : new(Gear: gear.Name)));
            Assert.Equal(gear.Count, matches.Total);
        }
    }

    [Fact]
    public async Task Shared_search_relative_dates_resolve_each_owner_and_missing_tags_block_every_consumer()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var east = await setup.SeedOwnerAsync("Shared east");
        var west = await setup.SeedOwnerAsync("Shared west");
        var instant = ReportingInstant("2026-10-08T22:30:00Z");
        var current = new[] { CriteriaActivity(east, 1), CriteriaActivity(west, 2) };
        var previous = new[] { CriteriaActivity(east, 3), CriteriaActivity(west, 4) };
        var geometry = GeometryCodec.ToWkb(TestSupport.Track(3));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.Owners.SingleAsync(x => x.Id == west)).TimeZoneId = "America/Los_Angeles";
            foreach (var activity in current) { activity.StartTimeUtc = instant.AddMinutes(-15); activity.SimplifiedGeometryWkb = geometry; }
            foreach (var activity in previous) { activity.StartTimeUtc = instant.AddDays(-1).AddMinutes(-15); activity.SimplifiedGeometryWkb = geometry; }
            db.Activities.AddRange(current.Concat(previous));
            await db.SaveChangesAsync();
        }
        var service = ReportingService(setup, new ReportingClock(instant));
        var criteria = new ActivityCriteria(Search: "ØVELSE", TextMode: ActivityTextMode.Words, Gear: "bike å");
        var dashboard = await service.GetDashboardAsync(new(Period: new(ReportingPreset.Today), Comparison: PeriodComparison.PreviousPeriod,
            Criteria: criteria));
        var rows = await service.SearchAsync(new(Period: ReportingPreset.Today, Criteria: criteria));
        var maps = new MapFeatureService(setup.Factory, new ReportingClock(instant));
        var lines = await maps.GetActivitiesAsync(new(Period: ReportingPreset.Today, Criteria: criteria));
        Assert.Equal(2, dashboard.Totals.ActivityCount);
        Assert.Equal(2, dashboard.ComparisonTotals!.ActivityCount);
        Assert.Equal(rows.Total, dashboard.Totals.ActivityCount);
        Assert.Equal(current.Select(x => x.Id).Order(), lines.Features.Select(x => (Guid)x.Properties["id"]!).Order());
        var calendar = await service.GetTrainingCalendarAsync(new(Month: new(2026, 10, 1), Criteria: criteria));
        Assert.Equal(1, calendar.Days.Single(day => day.Date == new DateOnly(2026, 10, 9)).Totals.ActivityCount);
        Assert.Equal(2, calendar.Days.Single(day => day.Date == new DateOnly(2026, 10, 8)).Totals.ActivityCount);
        Assert.Equal(1, calendar.Days.Single(day => day.Date == new DateOnly(2026, 10, 7)).Totals.ActivityCount);
        var missing = criteria with { ExcludedTagIds = [Guid.NewGuid()] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetDashboardAsync(new(east, Criteria: missing)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetTrainingCalendarAsync(new(east, Criteria: missing)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => maps.GetActivitiesAsync(new(east, Criteria: missing)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetDashboardAsync(new(Criteria: missing)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetTrainingCalendarAsync(new(Criteria: missing)));
        await Assert.ThrowsAsync<ArgumentException>(() => maps.GetActivitiesAsync(new(Criteria: missing)));
    }
}
