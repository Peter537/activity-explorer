using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace ActivityExplorer.Tests;

public sealed class GoalServiceTests
{
    [Theory]
    [InlineData(GoalMetric.Distance, null, 10000)]
    [InlineData(GoalMetric.MovingTime, null, 210)]
    [InlineData(GoalMetric.Ascent, null, 100)]
    [InlineData(GoalMetric.ActivityCount, null, 4)]
    [InlineData(GoalMetric.ActiveDays, null, 3)]
    [InlineData(GoalMetric.Distance, SportKind.Cycling, 6000)]
    [InlineData(GoalMetric.MovingTime, SportKind.Cycling, 90)]
    [InlineData(GoalMetric.Ascent, SportKind.Cycling, 60)]
    [InlineData(GoalMetric.ActivityCount, SportKind.Cycling, 3)]
    [InlineData(GoalMetric.ActiveDays, SportKind.Cycling, 2)]
    public async Task Every_metric_reconciles_with_reporting_and_evidence_including_future_and_zero_movement_activities(
        GoalMetric metric, SportKind? sport, double expected)
    {
        await using var setup = await Setup.CreateAsync("2026-10-10T12:00:00Z");
        var owner = await setup.Profiles.CreateAsync("Goal athlete");
        var other = await setup.Profiles.CreateAsync("Other athlete");
        var first = Row(owner, "2026-09-30T22:00:00Z", 1000, 0, 10);
        first.MovingTimeSource = MovingTimeSource.Unavailable;
        await setup.SeedAsync(first,
            Row(owner, "2026-10-01T20:00:00Z", 2000, 30, 20),
            Row(owner, "2026-10-31T22:59:59Z", 3000, 60, 30),
            Row(owner, "2026-10-02T12:00:00Z", 4000, 120, 40, SportKind.Running),
            Row(owner, "2026-09-30T21:59:59Z", 999999, 999999, 999999),
            Row(owner, "2026-10-31T23:00:00Z", 999999, 999999, 999999),
            Row(other, "2026-10-03T12:00:00Z", 999999, 999999, 999999));
        var id = await setup.Goals.CreateAsync(new(owner,
            new(metric, GoalRecurrence.Once, new(2026, 10, 1), new(2026, 10, 31)), new("October target", sport, 1)));

        var detail = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        var edition = Assert.IsType<GoalEdition>(detail.Edition);
        var reporting = await setup.Activities.GetDashboardAsync(new(owner, sport,
            new(ReportingPreset.Custom, edition.Start, edition.End)));

        var reported = metric switch
        {
            GoalMetric.Distance => reporting.Totals.DistanceMeters,
            GoalMetric.MovingTime => reporting.Totals.MovingSeconds,
            GoalMetric.Ascent => reporting.Totals.ElevationMeters,
            GoalMetric.ActivityCount => reporting.Totals.ActivityCount,
            GoalMetric.ActiveDays => reporting.Totals.ActiveDays,
            _ => throw new InvalidOperationException()
        };
        Assert.Equal(expected, edition.Actual);
        Assert.Equal(reported, edition.Actual);
        Assert.Equal(expected, detail.Contributions.Sum(day => day.Amount));
        Assert.Equal(expected * 100, edition.Percentage);
        Assert.Equal(0, edition.Remaining);
        Assert.True(edition.Achieved);
        Assert.Equal(GoalPeriodState.Active, edition.State);
        Assert.Equal(sport is null ? 4 : 3, detail.Contributions.Sum(day => day.Activities.Count));
        Assert.Equal(2, detail.Contributions.Single(day => day.Date == new DateOnly(2026, 10, 1)).Activities.Count);
        if (metric == GoalMetric.ActiveDays) Assert.All(detail.Contributions, day => Assert.Equal(1, day.Amount));
        Assert.Contains(detail.Contributions.SelectMany(day => day.Activities), activity => activity.StartTime > setup.Clock.Now);
    }

    [Theory]
    [InlineData(GoalRecurrence.Weekly, "2020-12-30", "2021-01-01T12:00:00Z", "2020-12-28", "2020-12-30", "2021-01-03", 2, 3)]
    [InlineData(GoalRecurrence.Weekly, "2020-12-30", "2021-01-04T12:00:00Z", "2021-01-04", "2021-01-04", "2021-01-10", 0, 7)]
    [InlineData(GoalRecurrence.Monthly, "2024-02-15", "2024-02-29T12:00:00Z", "2024-02-01", "2024-02-15", "2024-02-29", 14, 1)]
    [InlineData(GoalRecurrence.Monthly, "2024-02-15", "2024-03-01T12:00:00Z", "2024-03-01", "2024-03-01", "2024-03-31", 0, 31)]
    [InlineData(GoalRecurrence.Yearly, "2024-02-29", "2024-12-31T12:00:00Z", "2024-01-01", "2024-02-29", "2024-12-31", 306, 1)]
    [InlineData(GoalRecurrence.Yearly, "2024-02-29", "2025-01-01T12:00:00Z", "2025-01-01", "2025-01-01", "2025-12-31", 0, 365)]
    [InlineData(GoalRecurrence.Weekly, "2026-03-23", "2026-03-29T12:00:00Z", "2026-03-23", "2026-03-23", "2026-03-29", 6, 1)]
    [InlineData(GoalRecurrence.Weekly, "2026-10-19", "2026-10-25T12:00:00Z", "2026-10-19", "2026-10-19", "2026-10-25", 6, 1)]
    public async Task Recurring_editions_use_calendar_boundaries_full_targets_and_local_day_pace(
        GoalRecurrence recurrence, string start, string now, string key, string expectedStart, string end,
        int completedDays, int remainingDays)
    {
        await using var setup = await Setup.CreateAsync(now);
        var owner = await setup.Profiles.CreateAsync("Calendar athlete");
        var id = await setup.Goals.CreateAsync(new(owner,
            new(GoalMetric.Distance, recurrence, Date(start), null), new("Calendar goal", null, 70000)));

        var detail = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        var edition = Assert.IsType<GoalEdition>(detail.Edition);

        Assert.Equal(Date(key), edition.Key);
        Assert.Equal(Date(expectedStart), edition.Start);
        Assert.Equal(Date(end), edition.End);
        Assert.Equal(70000, edition.Definition.Target);
        Assert.Equal(0, edition.Actual);
        Assert.Equal(0, edition.Percentage);
        Assert.False(edition.Achieved);
        Assert.Equal(70000, edition.Remaining);
        Assert.Equal(completedDays, edition.CompletedDays);
        Assert.Equal(remainingDays, edition.RemainingDays);
        Assert.Equal(70000d * completedDays / (completedDays + remainingDays), edition.PaceTarget!.Value, 7);
        Assert.Equal(70000d / remainingDays, edition.RemainingPerDay!.Value, 7);
        Assert.DoesNotContain(detail.History, historical => historical.Start < Date(start));
    }

    [Fact]
    public async Task Future_initial_edition_is_editable_and_one_off_edition_locks_only_after_its_inclusive_end()
    {
        await using var setup = await Setup.CreateAsync("2026-10-10T12:00:00Z");
        var owner = await setup.Profiles.CreateAsync("One off athlete");
        var id = await setup.Goals.CreateAsync(new(owner,
            new(GoalMetric.ActivityCount, GoalRecurrence.Once, new(2026, 10, 12), new(2026, 10, 14)), new("Original", null, 3)));
        var before = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        Assert.Equal(GoalPeriodState.Upcoming, before.Edition!.State);
        Assert.Null(before.Edition.PaceTarget);
        Assert.Null(before.Edition.RemainingPerDay);
        Assert.NotNull(before.EditableEdition);
        await setup.Goals.EditAsync(Edit(before, new("Initial edit", SportKind.Walking, 5)));
        setup.Clock.Now = Instant("2026-10-14T21:59:59Z");
        var last = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        Assert.Equal(GoalPeriodState.Active, last.Edition!.State);
        Assert.Equal(1, last.Edition.RemainingDays);
        await setup.Goals.EditAsync(Edit(last, new("Last day edit", null, 7)));
        setup.Clock.Now = Instant("2026-10-14T22:00:00Z");
        var ended = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        Assert.Equal(GoalPeriodState.Ended, ended.Edition!.State);
        Assert.Null(ended.EditableEdition);
        Assert.Null(ended.Edition.PaceTarget);
        Assert.Null(ended.Edition.RemainingPerDay);
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Goals.EditAsync(
            new(owner, id, ended.Goal.MutationVersion, ended.Edition.Key, GoalEditScope.ThisEdition, new("Too late", null, 9), ended.Edition.State)));
        Assert.Empty(await setup.Goals.ListAsync(owner));
        Assert.Single(await setup.Goals.ListAsync(owner, GoalListView.All));
    }

    [Fact]
    public async Task Repeated_current_edits_preserve_the_template_and_explicit_future_edits_survive_later_current_edits()
    {
        await using var setup = await Setup.CreateAsync("2026-10-10T12:00:00Z");
        var owner = await setup.Profiles.CreateAsync("Revision athlete");
        var id = await Weekly(setup, owner, new("Original", SportKind.Cycling, 10000));
        var original = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        await setup.Goals.EditAsync(Edit(original, new("First adjustment", SportKind.Running, 20000)));
        var first = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        Assert.Equal(new GoalDefinition("Original", SportKind.Cycling, 10000), first.NextDefinition);
        await setup.Goals.EditAsync(Edit(first, new("Second adjustment", null, 30000)));
        var second = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        Assert.Equal(new GoalDefinition("Original", SportKind.Cycling, 10000), second.NextDefinition);
        var future = new GoalDefinition("New template", SportKind.Rowing, 40000);
        await setup.Goals.EditAsync(Edit(second, future, GoalEditScope.NextEditions));
        var scheduled = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        await setup.Goals.EditAsync(Edit(scheduled, new("Final current", SportKind.Walking, 50000)));
        var current = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        Assert.Equal(future, current.NextDefinition);
        Assert.Equal(new DateOnly(2026, 10, 12), current.NextEditionStart);
        Assert.Equal(4, current.Goal.MutationVersion - original.Goal.MutationVersion);

        setup.Clock.Now = Instant("2026-10-19T12:00:00Z");
        var restarted = setup.NewGoals();
        var after = Assert.IsType<GoalDetail>(await restarted.GetDetailAsync(id));
        Assert.Equal(future, after.Edition!.Definition);
        var old = Assert.IsType<GoalDetail>(await restarted.GetDetailAsync(id, original.Edition!.Key));
        Assert.Equal(new GoalDefinition("Final current", SportKind.Walking, 50000), old.Edition!.Definition);
        Assert.Equal(GoalPeriodState.Ended, old.Edition.State);
        Assert.Equal(future, old.EditableEdition!.Definition);
    }

    [Fact]
    public async Task Editing_the_upcoming_template_applies_from_its_first_partial_edition_onward()
    {
        await using var setup = await Setup.CreateAsync("2026-10-01T12:00:00Z");
        var owner = await setup.Profiles.CreateAsync("Upcoming athlete");
        var id = await setup.Goals.CreateAsync(new(owner,
            new(GoalMetric.Distance, GoalRecurrence.Weekly, new(2026, 10, 7), null), new("Original", null, 1000)));
        var upcoming = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        var definition = new GoalDefinition("Upcoming template", SportKind.Rowing, 2000);
        await setup.Goals.EditAsync(Edit(upcoming, definition, GoalEditScope.NextEditions));
        var initial = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        Assert.Equal(new DateOnly(2026, 10, 7), initial.Edition!.Start);
        Assert.Equal(definition, initial.Edition.Definition);
        Assert.Equal(GoalPeriodState.Upcoming, initial.Edition.State);
        setup.Clock.Now = Instant("2026-11-01T12:00:00Z");
        var later = Assert.IsType<GoalDetail>(await setup.NewGoals().GetDetailAsync(id));
        Assert.All(later.History, edition => Assert.Equal(definition, edition.Definition));
    }

    [Fact]
    public async Task Competing_edits_from_the_same_version_accept_exactly_one_and_preserve_future_definition()
    {
        await using var setup = await Setup.CreateAsync("2026-10-10T12:00:00Z");
        var owner = await setup.Profiles.CreateAsync("Concurrent athlete");
        var id = await Weekly(setup, owner);
        var original = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        var outcomes = await Task.WhenAll(TryEditAsync("First editor"), TryEditAsync("Second editor"));
        Assert.Single(outcomes, accepted => accepted);
        var current = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        Assert.Equal(original.Goal.MutationVersion + 1, current.Goal.MutationVersion);
        Assert.Equal(original.Edition!.Definition, current.NextDefinition);
        await using var db = await setup.Factory.CreateDbContextAsync();
        Assert.Equal(2, await db.GoalDefinitionRevisions.CountAsync());

        async Task<bool> TryEditAsync(string name)
        {
            try
            {
                await setup.Goals.EditAsync(Edit(original, new(name, null, 20000)));
                return true;
            }
            catch (InvalidOperationException exception)
            {
                Assert.Contains("Reload", exception.Message, StringComparison.Ordinal);
                return false;
            }
        }
    }

    [Fact]
    public async Task Stale_versions_wrong_owners_and_calendar_rollover_reject_mutations_without_partial_revisions()
    {
        await using var setup = await Setup.CreateAsync("2026-10-11T21:59:59Z");
        var owner = await setup.Profiles.CreateAsync("Race athlete");
        var other = await setup.Profiles.CreateAsync("Wrong owner");
        var id = await Weekly(setup, owner);
        var original = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        var edit = Edit(original, new("Accepted", null, 20000));
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Goals.EditAsync(edit with { OwnerId = other }));
        await setup.Goals.EditAsync(edit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Goals.EditAsync(edit with { Definition = new("Stale", null, 30000) }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Goals.ArchiveAsync(new(owner, id, original.Goal.MutationVersion, original.Edition!.Key, original.Edition.State)));
        var current = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        setup.Clock.Now = Instant("2026-10-11T22:00:00Z");
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Goals.EditAsync(Edit(current, new("Crossed midnight", null, 40000))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Goals.ArchiveAsync(new(owner, id, current.Goal.MutationVersion, current.Edition!.Key, current.Edition.State)));
        var next = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        Assert.Equal(current.Goal.MutationVersion, next.Goal.MutationVersion);
        Assert.False(next.Goal.Archived);
        Assert.Equal("Weekly goal", next.Edition!.Definition.Name);
        Assert.Equal("Accepted", (await setup.Goals.GetDetailAsync(id, original.Edition!.Key))!.Edition!.Definition.Name);
    }

    [Fact]
    public async Task Upcoming_edit_and_archive_reviews_reject_the_first_start_even_when_the_edition_key_is_unchanged()
    {
        await using var setup = await Setup.CreateAsync("2026-10-06T12:00:00Z");
        var owner = await setup.Profiles.CreateAsync("First start athlete");
        var id = await setup.Goals.CreateAsync(new(owner,
            new(GoalMetric.Distance, GoalRecurrence.Weekly, new(2026, 10, 7), null), new("Initial target", null, 1000)));
        var upcoming = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        var initialEdit = Edit(upcoming, new("Initial edit", SportKind.Rowing, 2000), GoalEditScope.NextEditions);
        var initialArchive = new ArchiveGoalRequest(owner, id, upcoming.Goal.MutationVersion,
            upcoming.Edition!.Key, upcoming.Edition.State);
        setup.Clock.Now = Instant("2026-10-06T22:00:00Z");
        var active = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        Assert.Equal(upcoming.Edition.Key, active.Edition!.Key);
        Assert.Equal(GoalPeriodState.Active, active.Edition.State);
        var editError = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Goals.EditAsync(initialEdit));
        var archiveError = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Goals.ArchiveAsync(initialArchive));
        Assert.Contains("Reload", editError.Message, StringComparison.Ordinal);
        Assert.Contains("Reload", archiveError.Message, StringComparison.Ordinal);
        var unchanged = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        Assert.Equal(upcoming.Goal.MutationVersion, unchanged.Goal.MutationVersion);
        Assert.False(unchanged.Goal.Archived);
        Assert.Equal(upcoming.Edition.Definition, unchanged.Edition!.Definition);
        Assert.Equal(upcoming.Edition.Definition, unchanged.NextDefinition);
        await using var db = await setup.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.GoalDefinitionRevisions.CountAsync());
    }

    [Fact]
    public async Task Active_one_off_archive_review_must_reload_after_the_inclusive_end()
    {
        await using var setup = await Setup.CreateAsync("2026-10-10T21:59:59Z");
        var owner = await setup.Profiles.CreateAsync("Archive boundary athlete");
        var id = await setup.Goals.CreateAsync(new(owner,
            new(GoalMetric.ActivityCount, GoalRecurrence.Once, new(2026, 10, 1), new(2026, 10, 10)), new("One off", null, 10)));
        var active = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        var request = new ArchiveGoalRequest(owner, id, active.Goal.MutationVersion, active.Edition!.Key, active.Edition.State);
        setup.Clock.Now = Instant("2026-10-10T22:00:00Z");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Goals.ArchiveAsync(request));
        Assert.Contains("Reload", error.Message, StringComparison.Ordinal);
        var ended = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        Assert.False(ended.Goal.Archived);
        Assert.Equal(active.Goal.MutationVersion, ended.Goal.MutationVersion);
        Assert.Equal(GoalPeriodState.Ended, ended.Edition!.State);
        await setup.Goals.ArchiveAsync(request with { ExpectedState = ended.Edition.State });
        Assert.True((await setup.Goals.GetDetailAsync(id))!.Goal.Archived);
    }

    [Theory]
    [InlineData("2026-10-01T12:00:00Z", false)]
    [InlineData("2026-10-10T12:00:00Z", true)]
    public async Task Archival_stops_future_editions_and_retains_only_an_edition_that_has_started(string now, bool started)
    {
        await using var setup = await Setup.CreateAsync(now);
        var owner = await setup.Profiles.CreateAsync("Archive athlete");
        var id = await Weekly(setup, owner);
        var original = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        await setup.Goals.ArchiveAsync(new(owner, id, original.Goal.MutationVersion, original.Edition!.Key, original.Edition.State));
        setup.Clock.Now = Instant("2026-11-20T12:00:00Z");
        var archived = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        Assert.True(archived.Goal.Archived);
        Assert.Null(archived.EditableEdition);
        Assert.Null(archived.NextEditionStart);
        Assert.Empty(await setup.Goals.ListAsync(owner));
        Assert.Single(await setup.Goals.ListAsync(owner, GoalListView.Archived));
        if (started)
        {
            Assert.Equal(original.Edition.End, archived.Edition!.End);
            Assert.Equal(GoalPeriodState.Ended, archived.Edition.State);
            Assert.Single(archived.History);
        }
        else
        {
            Assert.Null(archived.Edition);
            Assert.Empty(archived.History);
        }
    }

    [Fact]
    public async Task Progress_follows_import_transfer_correction_timezone_and_deletion_while_goals_stay_with_their_owner()
    {
        await using var setup = await Setup.CreateAsync("2026-10-10T12:00:00Z");
        var first = await setup.Profiles.CreateAsync("First goal owner");
        var second = await setup.Profiles.CreateAsync("Second goal owner");
        var schedule = new GoalSchedule(GoalMetric.Distance, GoalRecurrence.Monthly, new(2026, 10, 1), null);
        var firstId = await setup.Goals.CreateAsync(new(first, schedule, new("First goal", null, 500)));
        var secondId = await setup.Goals.CreateAsync(new(second, schedule, new("Second goal", null, 500)));
        var row = Row(first, "2026-09-30T22:30:00Z", 1000, 10, 0);
        await setup.SeedAsync(row);
        Assert.Equal(1000, (await setup.Goals.GetDetailAsync(firstId))!.Edition!.Actual);
        await setup.Profiles.UpdateTimeZoneAsync(first, "UTC");
        Assert.Equal(0, (await setup.Goals.GetDetailAsync(firstId))!.Edition!.Actual);
        await setup.Activities.UpdateAsync(row.Id, new("Transferred activity", null, null, second));
        Assert.Equal(first, (await setup.Goals.GetDetailAsync(firstId))!.Goal.OwnerId);
        Assert.Equal(1000, (await setup.Goals.GetDetailAsync(secondId))!.Edition!.Actual);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.Activities.SingleAsync()).DistanceMeters = 250;
            await db.SaveChangesAsync();
        }
        var corrected = (await setup.NewGoals().GetDetailAsync(secondId))!.Edition!;
        Assert.Equal(250, corrected.Actual);
        Assert.False(corrected.Achieved);
        await setup.Activities.DeleteAsync([row.Id]);
        Assert.Equal(0, (await setup.Goals.GetDetailAsync(secondId))!.Edition!.Actual);
        Assert.Equal(2, (await setup.Goals.ListAsync()).Count);
        Assert.Single(await setup.Goals.ListAsync(first));
    }

    [Fact]
    public async Task Actual_import_duplicate_and_reimport_recalculate_retrospective_progress_without_double_counting()
    {
        await using var setup = await Setup.CreateAsync("2026-10-10T12:00:00Z");
        var owner = await setup.Profiles.CreateAsync("Import athlete");
        var id = await setup.Goals.CreateAsync(new(owner,
            new(GoalMetric.Distance, GoalRecurrence.Once, new(2026, 1, 1), new(2026, 1, 31)), new("Historical rows", SportKind.Rowing, 1000)));
        Assert.Equal(0, (await setup.Goals.GetDetailAsync(id))!.Edition!.Actual);
        var input = TestSupport.RowingFit(TestSupport.NewDirectory(), durationSeconds: 600);
        await setup.ImportAsync(owner, input);
        var imported = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        Assert.Equal(1500, imported.Edition!.Actual);
        Assert.True(imported.Edition.Achieved);
        Assert.Equal(GoalPeriodState.Ended, imported.Edition.State);
        await setup.ImportAsync(owner, input);
        Assert.Equal(1500, (await setup.Goals.GetDetailAsync(id))!.Edition!.Actual);
        var activity = Assert.Single(Assert.Single(imported.Contributions).Activities);
        await setup.Activities.DeleteAsync([activity.ActivityId]);
        Assert.Equal(0, (await setup.Goals.GetDetailAsync(id))!.Edition!.Actual);
        await setup.ImportAsync(owner, input);
        Assert.Equal(1500, (await setup.Goals.GetDetailAsync(id))!.Edition!.Actual);
    }

    [Fact]
    public async Task Long_history_and_contributors_paginate_without_losing_entries_or_creating_read_time_rows()
    {
        await using var setup = await Setup.CreateAsync("2026-10-10T12:00:00Z");
        var owner = await setup.Profiles.CreateAsync("History athlete");
        var id = await setup.Goals.CreateAsync(new(owner,
            new(GoalMetric.ActiveDays, GoalRecurrence.Monthly, new(2023, 1, 15), null), new("Monthly days", null, 20)));
        await setup.SeedAsync(Enumerable.Range(1, 25).Select(day => Row(owner, $"2026-10-{day:00}T12:00:00Z", 0, 0, 0)).ToArray());
        var first = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        var second = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id, historyPage: 2, contributionPage: 2));
        var third = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id, historyPage: 3));
        Assert.Equal(46, first.HistoryCount);
        Assert.Equal(20, first.History.Count);
        Assert.Equal(20, second.History.Count);
        Assert.Equal(6, third.History.Count);
        var editions = first.History.Concat(second.History).Concat(third.History).ToArray();
        Assert.Equal(46, editions.Select(edition => edition.Key).Distinct().Count());
        Assert.Equal(new DateOnly(2023, 1, 15), editions[^1].Start);
        Assert.Equal(25, first.Edition!.Actual);
        Assert.Equal(25, first.ContributionCount);
        Assert.Equal(20, first.Contributions.Count);
        Assert.Equal(5, second.Contributions.Count);
        Assert.Equal(25, first.Contributions.Concat(second.Contributions).Select(day => day.Date).Distinct().Count());
        var invalid = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id, new(2022, 12, 1)));
        Assert.Null(invalid.Edition);
        Assert.NotEmpty(invalid.History);
        Assert.NotNull(invalid.EditableEdition);
        Assert.Null(await setup.Goals.GetDetailAsync(Guid.NewGuid()));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Goals.GetDetailAsync(id, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Reads_share_one_snapshot_and_never_read_stream_geometry_or_write_derived_progress()
    {
        await using var setup = await Setup.CreateAsync("2026-10-10T12:00:00Z");
        var owner = await setup.Profiles.CreateAsync("Snapshot athlete");
        var id = await Weekly(setup, owner);
        var row = Row(owner, "2026-10-09T12:00:00Z", 1000, 10, 0);
        row.Stream = new ActivityStream { OwnerId = owner, CompressedPayload = [1, 2, 3], PointCount = 100000 };
        row.GeometryWkb = [1, 2, 3];
        row.SimplifiedGeometryWkb = [1, 2, 3];
        await setup.SeedAsync(row);
        var capture = new CommandCapture();
        var service = setup.NewGoals(capture);

        Assert.Equal(1000, (await service.GetDetailAsync(id))!.Edition!.Actual);
        AssertReadSnapshot(capture);
        capture.Commands.Clear();
        Assert.Single(await service.ListAsync(owner));
        AssertReadSnapshot(capture);
        await using var db = await setup.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.Goals.CountAsync());
        Assert.Equal(1, await db.GoalDefinitionRevisions.CountAsync());
    }

    [Fact]
    public async Task Export_retains_authoritative_revisions_and_profile_deletion_cascades_without_touching_other_goals()
    {
        await using var setup = await Setup.CreateAsync("2026-10-10T12:00:00Z");
        var owner = await setup.Profiles.CreateAsync("Export athlete");
        var other = await setup.Profiles.CreateAsync("Retained athlete");
        var id = await Weekly(setup, owner);
        var retained = await Weekly(setup, other);
        var before = Assert.IsType<GoalDetail>(await setup.Goals.GetDetailAsync(id));
        await setup.Goals.EditAsync(Edit(before, new("Revised", SportKind.Rowing, 20000)));
        using var export = JsonDocument.Parse((await setup.Profiles.ExportAsync(owner)).Json);
        Assert.Equal(1, export.RootElement.GetProperty("schemaVersion").GetInt32());
        var goal = Assert.Single(export.RootElement.GetProperty("goals").EnumerateArray());
        Assert.Equal(id, goal.GetProperty("Id").GetGuid());
        Assert.Equal((int)GoalMetric.Distance, goal.GetProperty("Metric").GetInt32());
        Assert.Equal((int)GoalRecurrence.Weekly, goal.GetProperty("Recurrence").GetInt32());
        Assert.Equal("2026-10-05", goal.GetProperty("Start").GetString());
        Assert.Equal(before.Goal.MutationVersion + 1, goal.GetProperty("MutationVersion").GetInt64());
        Assert.False(goal.TryGetProperty("Actual", out _));
        Assert.Collection(goal.GetProperty("definitions").EnumerateArray(),
            revision =>
            {
                Assert.Equal("2026-10-05", revision.GetProperty("EffectiveFromEdition").GetString());
                Assert.Equal("Revised", revision.GetProperty("Name").GetString());
                Assert.Equal((int)SportKind.Rowing, revision.GetProperty("Sport").GetInt32());
                Assert.Equal(20000, revision.GetProperty("Target").GetDouble());
            },
            revision =>
            {
                Assert.Equal("2026-10-12", revision.GetProperty("EffectiveFromEdition").GetString());
                Assert.Equal("Weekly goal", revision.GetProperty("Name").GetString());
                Assert.Equal(10000, revision.GetProperty("Target").GetDouble());
            });
        await setup.Profiles.DeleteAsync(owner, "DELETE Export athlete");
        Assert.Null(await setup.Goals.GetDetailAsync(id));
        Assert.Equal(retained, Assert.Single(await setup.Goals.ListAsync()).Goal.Id);
        await using var db = await setup.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.Goals.CountAsync());
        Assert.Equal(1, await db.GoalDefinitionRevisions.CountAsync());
    }

    [Fact]
    public async Task Invalid_owner_or_timezone_is_actionable_and_does_not_break_other_owners()
    {
        await using var setup = await Setup.CreateAsync("2026-10-10T12:00:00Z");
        var owner = await setup.Profiles.CreateAsync("Damaged timezone");
        var other = await setup.Profiles.CreateAsync("Healthy timezone");
        await Weekly(setup, owner);
        var retained = await Weekly(setup, other);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.Owners.SingleAsync(profile => profile.Id == owner)).TimeZoneId = "Missing/Zone";
            await db.SaveChangesAsync();
        }
        var all = await setup.Goals.ListAsync();
        Assert.Contains("timezone", all.Single(goal => goal.Goal.OwnerId == owner).Error!, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(all.Single(goal => goal.Goal.Id == retained).Edition);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Weekly(setup, Guid.NewGuid()));
        await setup.Profiles.UpdateTimeZoneAsync(owner, "UTC");
        Assert.Null(Assert.Single(await setup.Goals.ListAsync(owner)).Error);
    }

    [Fact]
    public async Task Corrupt_persisted_target_is_reported_without_hiding_healthy_goals()
    {
        await using var setup = await Setup.CreateAsync("2026-10-10T12:00:00Z");
        var owner = await setup.Profiles.CreateAsync("Target recovery athlete");
        var damaged = await Weekly(setup, owner);
        var healthy = await Weekly(setup, owner);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.GoalDefinitionRevisions.SingleAsync(revision => revision.GoalId == damaged)).Target = -1;
            await db.SaveChangesAsync();
        }
        var goals = await setup.Goals.ListAsync(owner);
        var invalid = goals.Single(goal => goal.Goal.Id == damaged);
        Assert.Null(invalid.Edition);
        Assert.NotNull(invalid.Error);
        Assert.NotNull(goals.Single(goal => goal.Goal.Id == healthy).Edition);
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Goals.GetDetailAsync(damaged));
    }

    [Theory]
    [InlineData(GoalMetric.Distance, 0)]
    [InlineData(GoalMetric.Distance, -1)]
    [InlineData(GoalMetric.Distance, double.NaN)]
    [InlineData(GoalMetric.Distance, double.PositiveInfinity)]
    [InlineData(GoalMetric.Distance, double.NegativeInfinity)]
    [InlineData(GoalMetric.ActivityCount, 1.5)]
    [InlineData(GoalMetric.ActiveDays, 1.5)]
    public async Task Invalid_targets_are_rejected_without_persistence(GoalMetric metric, double target)
    {
        await using var setup = await Setup.CreateAsync("2026-10-10T12:00:00Z");
        var owner = await setup.Profiles.CreateAsync("Validation athlete");
        await Assert.ThrowsAnyAsync<ArgumentException>(() => setup.Goals.CreateAsync(new(owner,
            new(metric, GoalRecurrence.Weekly, new(2026, 10, 5), null), new("Invalid", null, target))));
        Assert.Empty(await setup.Goals.ListAsync(owner, GoalListView.All));
    }

    [Fact]
    public async Task Invalid_definitions_and_schedules_are_rejected_and_names_are_trimmed()
    {
        await using var setup = await Setup.CreateAsync("2026-10-10T12:00:00Z");
        var owner = await setup.Profiles.CreateAsync("Definition athlete");
        var schedule = new GoalSchedule(GoalMetric.Distance, GoalRecurrence.Weekly, new(2026, 10, 5), null);
        var definition = new GoalDefinition("Valid", null, 1.5);
        foreach (var invalid in new[] { definition with { Name = "   " }, definition with { Name = new string('x', 121) }, definition with { Sport = (SportKind)999 } })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => setup.Goals.CreateAsync(new(owner, schedule, invalid)));
        foreach (var invalid in new[]
        {
            schedule with { Metric = (GoalMetric)999 }, schedule with { Recurrence = (GoalRecurrence)999 },
            schedule with { End = new(2026, 10, 10) }, schedule with { Recurrence = GoalRecurrence.Once },
            schedule with { Recurrence = GoalRecurrence.Once, End = new(2026, 10, 4) },
            schedule with { Start = DateOnly.MaxValue }
        })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => setup.Goals.CreateAsync(new(owner, invalid, definition)));
        Assert.Empty(await setup.Goals.ListAsync(owner, GoalListView.All));
        var id = await setup.Goals.CreateAsync(new(owner, schedule, definition with { Name = "  Trimmed  " }));
        Assert.Equal("Trimmed", (await setup.Goals.GetDetailAsync(id))!.Goal.Definition.Name);
    }

    private static Task<Guid> Weekly(Setup setup, Guid owner, GoalDefinition? definition = null) =>
        setup.Goals.CreateAsync(new(owner, new(GoalMetric.Distance, GoalRecurrence.Weekly, new(2026, 10, 5), null),
            definition ?? new("Weekly goal", null, 10000)));

    private static EditGoalRequest Edit(GoalDetail detail, GoalDefinition definition, GoalEditScope scope = GoalEditScope.ThisEdition) =>
        new(detail.Goal.OwnerId, detail.Goal.Id, detail.Goal.MutationVersion, detail.EditableEdition!.Key, scope, definition, detail.EditableEdition.State);

    private static Activity Row(Guid owner, string start, double distance, double moving, double ascent, SportKind sport = SportKind.Cycling) => new()
    {
        OwnerId = owner,
        Title = "Synthetic goal activity",
        NaturalFingerprint = Guid.NewGuid().ToString("N"),
        Sport = sport,
        StartTimeUtc = Instant(start),
        OriginalUtcOffset = TimeSpan.FromHours(-12),
        DistanceMeters = distance,
        MovingTimeSeconds = moving,
        ElapsedTimeSeconds = moving + 3600,
        ElevationGainMeters = ascent
    };

    private static void AssertReadSnapshot(CommandCapture capture)
    {
        Assert.NotEmpty(capture.Commands);
        var transaction = capture.Commands[0].Transaction;
        Assert.NotNull(transaction);
        Assert.All(capture.Commands, command =>
        {
            Assert.Same(transaction, command.Transaction);
            Assert.StartsWith("SELECT", command.Sql.TrimStart(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ActivityStreams", command.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("CompressedPayload", command.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("GeometryWkb", command.Sql, StringComparison.Ordinal);
        });
    }

    private static DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateTimeOffset Instant(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private sealed class Clock(string now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Instant(now);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CommandCapture : DbCommandInterceptor
    {
        public List<(string Sql, DbTransaction? Transaction)> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add((command.CommandText, command.Transaction));
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands.Add((command.CommandText, command.Transaction));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Factory(DbContextOptions<ExplorerDbContext> options) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);
    }

    private sealed class Setup(ServiceProvider services, Clock clock) : IAsyncDisposable
    {
        public Clock Clock { get; } = clock;
        public IGoalService Goals => services.GetRequiredService<IGoalService>();
        public IProfileService Profiles => services.GetRequiredService<IProfileService>();
        public IActivityQueryService Activities => services.GetRequiredService<IActivityQueryService>();
        public IDbContextFactory<ExplorerDbContext> Factory => services.GetRequiredService<IDbContextFactory<ExplorerDbContext>>();

        public static async Task<Setup> CreateAsync(string now)
        {
            var previous = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_DATA");
            try
            {
                Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", TestSupport.NewDirectory());
                var clock = new Clock(now);
                var services = new ServiceCollection().AddLogging().AddActivityExplorer().AddSingleton<TimeProvider>(clock).BuildServiceProvider();
                await services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
                return new(services, clock);
            }
            finally { Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", previous); }
        }

        public GoalService NewGoals(CommandCapture? capture = null)
        {
            IDbContextFactory<ExplorerDbContext> factory = Factory;
            if (capture is not null)
                factory = new Factory(new DbContextOptionsBuilder<ExplorerDbContext>()
                    .UseSqlite($"Data Source={services.GetRequiredService<AppDataPaths>().DatabasePath}").AddInterceptors(capture).Options);
            return new(factory, services.GetRequiredService<IOwnerMutationLock>(), Clock);
        }

        public async Task SeedAsync(params Activity[] activities)
        {
            await using var db = await Factory.CreateDbContextAsync();
            db.Activities.AddRange(activities);
            await db.SaveChangesAsync();
        }

        public async Task ImportAsync(Guid owner, string input)
        {
            var staging = Path.Combine(services.GetRequiredService<AppDataPaths>().StagingPath, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var staged = Path.Combine(staging, Path.GetFileName(input));
            File.Copy(input, staged);
            var id = await services.GetRequiredService<IImportQueue>().EnqueueAsync(new(owner, staged, Path.GetFileName(input), SourceKind.Fit));
            await services.GetRequiredService<IImportProcessor>().ProcessAsync(id);
            await using var db = await Factory.CreateDbContextAsync();
            Assert.Equal(ImportStatus.Completed, (await db.ImportBatches.FindAsync(id))!.Status);
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }
    }
}
