using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed partial class GoalServiceTests
{
    [Fact]
    public async Task Goal_finding_uses_displayed_definitions_and_distinguishes_unavailable_progress_from_unreached()
    {
        await using var setup = await Setup.CreateAsync("2026-10-10T12:00:00Z");
        var owner = await setup.Profiles.CreateAsync("Goal finder");
        var other = await setup.Profiles.CreateAsync("Other goal finder");
        await setup.SeedAsync(Row(owner, "2026-10-09T12:00:00Z", 1500, 60, 20));
        var schedule = new GoalSchedule(GoalMetric.Distance, GoalRecurrence.Monthly, new(2026, 10, 1), null);
        var reached = await setup.Goals.CreateAsync(new(owner, schedule, new("Old name", SportKind.Cycling, 1000)));
        await setup.Goals.EditAsync(Edit((await setup.Goals.GetDetailAsync(reached))!, new("Æble current", SportKind.Cycling, 1000), GoalEditScope.ThisEdition));
        await setup.Goals.EditAsync(Edit((await setup.Goals.GetDetailAsync(reached))!, new("Future running template", SportKind.Running, 1000), GoalEditScope.NextEditions));
        var unreached = await setup.Goals.CreateAsync(new(owner, schedule, new("Æble longer", SportKind.Cycling, 2000)));
        var ascent = await setup.Goals.CreateAsync(new(owner, schedule with { Metric = GoalMetric.Ascent }, new("Æble hills", SportKind.Cycling, 10)));
        var ended = await setup.Goals.CreateAsync(new(owner, new(GoalMetric.Distance, GoalRecurrence.Once, new(2026, 9, 1), new(2026, 9, 30)), new("Æble ended", SportKind.Cycling, 1000)));
        var skipped = await setup.Goals.CreateAsync(new(owner, schedule with { Start = new(2026, 11, 1) }, new("Æble skipped", SportKind.Cycling, 1000)));
        var skippedDetail = (await setup.Goals.GetDetailAsync(skipped))!;
        await setup.Goals.ArchiveAsync(new(owner, skipped, skippedDetail.Goal.MutationVersion, skippedDetail.Edition!.Key, skippedDetail.Edition.State));
        var damaged = await setup.Goals.CreateAsync(new(owner, schedule, new("Æble damaged", SportKind.Cycling, 1000)));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.GoalDefinitionRevisions.SingleAsync(definition => definition.GoalId == damaged)).Target = 0;
            await db.SaveChangesAsync();
        }
        await setup.Goals.CreateAsync(new(other, schedule, new("Æble other owner", SportKind.Cycling, 1000)));

        var criteria = new GoalListFilter(owner, GoalListView.All, "  æBLE  ", SportKind.Cycling, GoalMetric.Distance);
        var all = await setup.Goals.SearchAsync(criteria);
        Assert.Equal(new[] { reached, unreached, ended, skipped, damaged }.Order(), all.Select(item => item.Goal.Id).Order());
        Assert.Equal(reached, Assert.Single(await setup.Goals.SearchAsync(criteria with { Progress = GoalProgressFilter.TargetReached })).Goal.Id);
        Assert.Equal(new[] { unreached, ended }.Order(), (await setup.Goals.SearchAsync(criteria with { Progress = GoalProgressFilter.TargetNotReached })).Select(item => item.Goal.Id).Order());
        Assert.Equal(ascent, Assert.Single(await setup.Goals.SearchAsync(criteria with { Metric = GoalMetric.Ascent })).Goal.Id);
        Assert.Equal(unreached, Assert.Single(await setup.Goals.SearchAsync(criteria with { View = GoalListView.CurrentAndUpcoming, Progress = GoalProgressFilter.TargetNotReached })).Goal.Id);
        Assert.Equal(skipped, Assert.Single(await setup.Goals.SearchAsync(criteria with { View = GoalListView.Archived })).Goal.Id);
        Assert.Empty(await setup.Goals.SearchAsync(criteria with { Search = "Old name" }));
        Assert.Empty(await setup.Goals.SearchAsync(criteria with { Search = "Future running template", Sport = SportKind.Running }));
        Assert.Null(Assert.Single(all, item => item.Goal.Id == skipped).Edition);
        Assert.NotNull(Assert.Single(all, item => item.Goal.Id == damaged).Error);
        Assert.Equal((await setup.Goals.ListAsync(owner, GoalListView.All)).Select(item => item.Goal.Id),
            (await setup.Goals.SearchAsync(new(owner, GoalListView.All))).Select(item => item.Goal.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Goals.SearchAsync(criteria with { Sport = (SportKind)999 }));
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Goals.SearchAsync(criteria with { Metric = (GoalMetric)999 }));
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Goals.SearchAsync(criteria with { Progress = (GoalProgressFilter)999 }));
    }

    [Theory]
    [InlineData(GoalRecurrence.Weekly, "2020-12-30", null, "2021-01-03", "2020-12-28", 16)]
    [InlineData(GoalRecurrence.Weekly, "2020-12-30", null, "2021-01-04", "2021-01-04", 16)]
    [InlineData(GoalRecurrence.Monthly, "2020-01-15", null, "2024-02-29", "2024-02-01", 2)]
    [InlineData(GoalRecurrence.Yearly, "2000-02-29", null, "2004-02-29", "2004-01-01", 2)]
    [InlineData(GoalRecurrence.Once, "2024-02-15", "2024-03-01", "2024-02-29", "2024-02-15", 1)]
    public async Task Goal_date_lookup_resolves_calendar_edition_and_exact_history_page_without_activity_reads(
        GoalRecurrence recurrence, string start, string? end, string date, string expectedEdition, int expectedPage)
    {
        await using var setup = await Setup.CreateAsync("2026-10-15T12:00:00Z");
        var owner = await setup.Profiles.CreateAsync("Edition finder");
        var id = await setup.Goals.CreateAsync(new(owner, new(GoalMetric.Distance, recurrence, Date(start), end is null ? null : Date(end)), new("Long history", null, 1000)));
        var capture = new CommandCapture();
        var location = await setup.NewGoals(capture).FindEditionAsync(id, Date(date));
        Assert.Equal(new GoalEditionLocation(Date(expectedEdition), expectedPage), location);
        Assert.DoesNotContain(capture.Commands, command => command.Sql.Contains("FROM \"Activities\"", StringComparison.Ordinal));
        var detail = (await setup.Goals.GetDetailAsync(id, location!.Edition, location.HistoryPage))!;
        Assert.Equal(Date(expectedEdition), detail.Edition!.Key);
        Assert.Contains(detail.History, edition => edition.Key == location.Edition);
        Assert.Equal(expectedPage, detail.HistoryPage);
    }

    [Fact]
    public async Task Goal_date_lookup_rejects_outside_schedule_future_editions_and_archive_cutoff_without_substitution()
    {
        await using var setup = await Setup.CreateAsync("2026-10-10T12:00:00Z");
        var owner = await setup.Profiles.CreateAsync("Edition boundary finder");
        var id = await setup.Goals.CreateAsync(new(owner, new(GoalMetric.Distance, GoalRecurrence.Monthly, new(2020, 1, 15), null), new("Archived history", null, 1000)));
        Assert.Equal(new DateOnly(2020, 1, 1), (await setup.Goals.FindEditionAsync(id, new(2020, 1, 15)))!.Edition);
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Goals.FindEditionAsync(id, new(2020, 1, 14)));
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Goals.FindEditionAsync(id, new(2026, 11, 1)));
        var detail = (await setup.Goals.GetDetailAsync(id))!;
        await setup.Goals.ArchiveAsync(new(owner, id, detail.Goal.MutationVersion, detail.Edition!.Key, detail.Edition.State));
        setup.Clock.Now = Instant("2027-01-10T12:00:00Z");
        Assert.Equal(new DateOnly(2026, 10, 1), (await setup.Goals.FindEditionAsync(id, new(2026, 10, 31)))!.Edition);
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Goals.FindEditionAsync(id, new(2026, 11, 1)));

        var upcoming = await setup.Goals.CreateAsync(new(owner, new(GoalMetric.Distance, GoalRecurrence.Monthly, new(2027, 2, 15), null), new("Upcoming history", null, 1000)));
        Assert.Equal(new DateOnly(2027, 2, 1), (await setup.Goals.FindEditionAsync(upcoming, new(2027, 2, 28)))!.Edition);
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Goals.FindEditionAsync(upcoming, new(2027, 2, 14)));
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Goals.FindEditionAsync(upcoming, new(2027, 3, 1)));
        var upcomingDetail = (await setup.Goals.GetDetailAsync(upcoming))!;
        await setup.Goals.ArchiveAsync(new(owner, upcoming, upcomingDetail.Goal.MutationVersion, upcomingDetail.Edition!.Key, upcomingDetail.Edition.State));
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Goals.FindEditionAsync(upcoming, new(2027, 2, 15)));

        var once = await setup.Goals.CreateAsync(new(owner, new(GoalMetric.Distance, GoalRecurrence.Once, new(2024, 2, 15), new(2024, 3, 1)), new("One-off range", null, 1000)));
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Goals.FindEditionAsync(once, new(2024, 3, 2)));
        Assert.Null(await setup.Goals.FindEditionAsync(Guid.NewGuid(), new(2024, 2, 29)));
    }

    [Fact]
    public async Task Goal_date_lookup_available_editions_follow_owner_local_Monday_boundary()
    {
        await using var setup = await Setup.CreateAsync("2026-10-12T00:30:00Z");
        var owner = await setup.Profiles.CreateAsync("Western edition finder");
        await setup.Profiles.UpdateTimeZoneAsync(owner, "America/Los_Angeles");
        var id = await setup.Goals.CreateAsync(new(owner, new(GoalMetric.Distance, GoalRecurrence.Weekly, new(2026, 10, 5), null), new("Monday boundary", null, 1000)));
        Assert.Equal(new DateOnly(2026, 10, 5), (await setup.Goals.FindEditionAsync(id, new(2026, 10, 11)))!.Edition);
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Goals.FindEditionAsync(id, new(2026, 10, 12)));
        setup.Clock.Now = Instant("2026-10-12T07:00:00Z");
        Assert.Equal(new DateOnly(2026, 10, 12), (await setup.Goals.FindEditionAsync(id, new(2026, 10, 12)))!.Edition);
    }
}
