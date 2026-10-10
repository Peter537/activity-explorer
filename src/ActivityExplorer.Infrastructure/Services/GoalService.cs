using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class GoalService(
    IDbContextFactory<ExplorerDbContext> contextFactory,
    IOwnerMutationLock ownerMutationLock,
    TimeProvider timeProvider) : IGoalService
{
    private sealed record ActivityRow(Guid Id, Guid OwnerId, string Title, SportKind Sport, DateTimeOffset StartTime,
        double Distance, double MovingTime, double Ascent);
    private sealed record LocalActivity(ActivityRow Activity, DateOnly Date);
    private sealed record ReadGoal(PersonalGoal Goal, GoalInfo Info, DateOnly Today,
        TimeZoneInfo? Zone, GoalCalendar.Period? Period, string? Error);

    public async Task<IReadOnlyList<GoalSummary>> ListAsync(Guid? ownerId = null,
        GoalListView view = GoalListView.CurrentAndUpcoming, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(view)) throw new ArgumentException("Choose a valid goal view.");
        var now = timeProvider.GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (ownerId.HasValue) await RequireOwnerAsync(db, ownerId.Value, cancellationToken);
        var goals = await ReadGoals(db).Where(x => !ownerId.HasValue || x.OwnerId == ownerId)
            .ToArrayAsync(cancellationToken);
        var selections = new List<ReadGoal>();
        foreach (var goal in goals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = PrepareRead(goal, now);
            var archived = goal.ArchiveFromEdition.HasValue;
            if (view == GoalListView.Archived && !archived) continue;
            if (view == GoalListView.CurrentAndUpcoming && read.Error is null &&
                (read.Period is null || read.Period.End < read.Today)) continue;
            selections.Add(read);
        }
        var periods = selections.Where(x => x.Error is null && x.Period is not null)
            .GroupBy(x => x.Goal.OwnerId).Select(group =>
            {
                var first = group.First();
                return ResolvePeriod(first.Goal, group.Min(x => x.Period!.Start), group.Max(x => x.Period!.End), now);
            }).ToArray();
        var activities = await ReadActivitiesAsync(db, periods, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var local = Localize(activities, selections.Where(x => x.Zone is not null)
            .GroupBy(x => x.Goal.OwnerId).ToDictionary(x => x.Key, x => x.First().Zone!), cancellationToken);
        var result = new List<GoalSummary>();
        foreach (var read in selections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var edition = read.Period is null ? null : Evaluate(read.Goal, read.Period, read.Today,
                    local.GetValueOrDefault(read.Goal.OwnerId, []), cancellationToken);
                result.Add(new(read.Info, edition, read.Error));
            }
            catch (InvalidOperationException exception)
            {
                result.Add(new(read.Info, null, exception.Message));
            }
        }
        return result.OrderBy(x => x.Goal.OwnerName, StringComparer.CurrentCulture)
            .ThenBy(x => x.Edition?.End).ThenBy(x => x.Goal.Definition.Name, StringComparer.CurrentCulture)
            .ThenBy(x => x.Goal.Id).ToArray();
    }

    public async Task<GoalDetail?> GetDetailAsync(Guid goalId, DateOnly? edition = null,
        int historyPage = 1, int contributionPage = 1, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var goal = await ReadGoals(db).SingleOrDefaultAsync(x => x.Id == goalId, cancellationToken);
        if (goal is null) return null;
        var zone = ReportingTimeZone.Resolve(goal.Owner!.TimeZoneId);
        var today = GoalCalendar.Today(now, zone);
        var schedule = GoalCalendar.Schedule(goal);
        var count = GoalCalendar.Count(goal, today);
        historyPage = Page(historyPage, count);
        var primary = GoalCalendar.Selected(goal, today);
        var selected = GoalCalendar.Selected(goal, today, edition);
        var historyPeriods = Enumerable.Range(0, Math.Min(GoalDetail.PageSize, Math.Max(0, count - (historyPage - 1) * GoalDetail.PageSize)))
            .Select(offset => GoalCalendar.At(schedule, GoalCalendar.KeyAt(schedule, count - 1 - (historyPage - 1) * GoalDetail.PageSize - offset)))
            .ToArray();
        var periods = historyPeriods.Concat(new[] { selected, primary }.OfType<GoalCalendar.Period>()).Distinct().ToArray();
        var rows = periods.Length == 0 ? [] : await ReadActivitiesAsync(db,
            periods.Select(period => ResolvePeriod(goal, period.Start, period.End, now)).ToArray(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var activities = Localize(rows, new Dictionary<Guid, TimeZoneInfo> { [goal.OwnerId] = zone }, cancellationToken)
            .GetValueOrDefault(goal.OwnerId, []);
        var history = historyPeriods.Select(period => Evaluate(goal, period, today, activities, cancellationToken)).ToArray();
        var selectedEdition = selected is null ? null : Evaluate(goal, selected, today, activities, cancellationToken);
        var contributions = selected is null ? [] : Contribute(goal, selected, activities, cancellationToken);
        contributionPage = Page(contributionPage, contributions.Length);
        var editable = primary is not null && primary.End >= today ? Evaluate(goal, primary, today, activities, cancellationToken) : null;
        var next = NextEditable(goal, primary, today);
        return new(Info(goal, primary?.Key ?? GoalCalendar.Key(schedule, schedule.Start)), selectedEdition,
            history, count, historyPage, contributions.Skip((contributionPage - 1) * GoalDetail.PageSize).Take(GoalDetail.PageSize).ToArray(),
            contributions.Length, contributionPage, editable, next, next.HasValue ? GoalCalendar.Definition(goal, next.Value) : null);
    }

    public async Task<Guid> CreateAsync(CreateGoalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSchedule(request.Schedule);
        var definition = ValidateDefinition(request.Definition, request.Schedule.Metric);
        await using var ownerLock = await ownerMutationLock.AcquireAsync([request.OwnerId], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var owner = await RequireOwnerAsync(db, request.OwnerId, cancellationToken);
        var firstKey = GoalCalendar.Key(request.Schedule, request.Schedule.Start);
        var first = GoalCalendar.At(request.Schedule, firstKey);
        _ = ReportingDates.Resolve(new(ReportingPreset.Custom, first.Start, first.End), owner.Id, owner.DisplayName,
            owner.TimeZoneId, timeProvider.GetUtcNow());
        var goal = new PersonalGoal
        {
            OwnerId = owner.Id,
            Metric = request.Schedule.Metric,
            Recurrence = request.Schedule.Recurrence,
            Start = request.Schedule.Start,
            End = request.Schedule.End
        };
        SetDefinition(goal, firstKey, definition);
        db.Goals.Add(goal);
        await db.SaveChangesAsync(cancellationToken);
        return goal.Id;
    }

    public async Task EditAsync(EditGoalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Scope)) throw new ArgumentException("Choose which goal editions to edit.");
        await using var ownerLock = await ownerMutationLock.AcquireAsync([request.OwnerId], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var goal = await RequireGoalAsync(db, request.OwnerId, request.GoalId, cancellationToken);
        var definition = ValidateDefinition(request.Definition, goal.Metric);
        var today = GoalCalendar.Today(timeProvider.GetUtcNow(), ReportingTimeZone.Resolve(goal.Owner!.TimeZoneId));
        var primary = RequireExpected(goal, request.ExpectedVersion, request.ExpectedEdition, request.ExpectedState, today);
        if (primary.End < today) throw new InvalidOperationException("Ended goal editions cannot be edited.");
        var schedule = GoalCalendar.Schedule(goal);
        if (request.Scope == GoalEditScope.NextEditions)
        {
            var next = NextEditable(goal, primary, today)
                ?? throw new InvalidOperationException("This goal has no future template to edit.");
            SetDefinition(goal, next, definition);
        }
        else
        {
            if (goal.Recurrence != GoalRecurrence.Once && !goal.ArchiveFromEdition.HasValue)
            {
                var next = GoalCalendar.Next(schedule, primary.Key);
                if (goal.Definitions.All(x => x.EffectiveFromEdition != next))
                    SetDefinition(goal, next, GoalCalendar.Definition(goal, primary.Key));
            }
            SetDefinition(goal, primary.Key, definition);
        }
        goal.MutationVersion++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ArchiveAsync(ArchiveGoalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var ownerLock = await ownerMutationLock.AcquireAsync([request.OwnerId], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var goal = await RequireGoalAsync(db, request.OwnerId, request.GoalId, cancellationToken);
        var today = GoalCalendar.Today(timeProvider.GetUtcNow(), ReportingTimeZone.Resolve(goal.Owner!.TimeZoneId));
        var primary = RequireExpected(goal, request.ExpectedVersion, request.ExpectedEdition, request.ExpectedState, today);
        if (goal.ArchiveFromEdition.HasValue) return;
        var schedule = GoalCalendar.Schedule(goal);
        goal.ArchiveFromEdition = today < goal.Start ? GoalCalendar.Key(schedule, goal.Start) :
            GoalCalendar.Next(schedule, primary.Key);
        goal.MutationVersion++;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static IQueryable<PersonalGoal> ReadGoals(ExplorerDbContext db) => db.Goals.AsNoTracking()
        .Include(x => x.Owner).Include(x => x.Definitions).AsSplitQuery();

    private static ReadGoal PrepareRead(PersonalGoal goal, DateTimeOffset now)
    {
        var key = GoalCalendar.Key(GoalCalendar.Schedule(goal), goal.Start);
        try
        {
            var zone = ReportingTimeZone.Resolve(goal.Owner!.TimeZoneId);
            var today = GoalCalendar.Today(now, zone);
            var selected = GoalCalendar.Selected(goal, today);
            return new(goal, Info(goal, selected?.Key ?? key), today, zone, selected, null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return new(goal, Info(goal, key), default, null, null, exception.Message);
        }
    }

    private static GoalInfo Info(PersonalGoal goal, DateOnly key) => new(goal.Id, goal.OwnerId, goal.Owner!.DisplayName,
        goal.Owner.TimeZoneId ?? ReportingTimeZone.DefaultId, GoalCalendar.Schedule(goal), GoalCalendar.Definition(goal, key),
        goal.ArchiveFromEdition.HasValue, goal.MutationVersion);

    private static ResolvedOwnerPeriod ResolvePeriod(PersonalGoal goal, DateOnly start, DateOnly end, DateTimeOffset now) =>
        ReportingDates.Resolve(new(ReportingPreset.Custom, start, end), goal.OwnerId, goal.Owner!.DisplayName, goal.Owner.TimeZoneId, now);

    private static Task<ActivityRow[]> ReadActivitiesAsync(ExplorerDbContext db,
        IReadOnlyList<ResolvedOwnerPeriod> periods, CancellationToken cancellationToken) =>
        ReportingDateQuery.Apply(db.Activities.AsNoTracking(), periods).OrderBy(x => x.StartTimeUtc).ThenBy(x => x.Id)
            .Select(x => new ActivityRow(x.Id, x.OwnerId, x.Title, x.Sport, x.StartTimeUtc,
                x.DistanceMeters, x.MovingTimeSeconds, x.ElevationGainMeters)).ToArrayAsync(cancellationToken);

    private static Dictionary<Guid, LocalActivity[]> Localize(ActivityRow[] rows,
        IReadOnlyDictionary<Guid, TimeZoneInfo> zones, CancellationToken cancellationToken)
    {
        var local = new List<LocalActivity>(rows.Length);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            local.Add(new(row, GoalCalendar.Today(row.StartTime, zones[row.OwnerId])));
        }
        return local.GroupBy(x => x.Activity.OwnerId).ToDictionary(x => x.Key, x => x.ToArray());
    }

    private static GoalEdition Evaluate(PersonalGoal goal, GoalCalendar.Period period, DateOnly today,
        LocalActivity[] activities, CancellationToken cancellationToken)
    {
        var definition = GoalCalendar.Definition(goal, period.Key);
        if (!double.IsFinite(definition.Target) || definition.Target <= 0 ||
            (goal.Metric is GoalMetric.ActivityCount or GoalMetric.ActiveDays && definition.Target != Math.Truncate(definition.Target)) ||
            (definition.Sport.HasValue && !Enum.IsDefined(definition.Sport.Value)))
            throw new InvalidOperationException("This goal has an invalid stored definition. Its progress is unavailable.");
        var contributions = Contribute(goal, period, activities, cancellationToken);
        var actual = Sum(contributions.Select(x => x.Amount));
        var percentage = actual / definition.Target * 100;
        if (!double.IsFinite(percentage)) throw new InvalidOperationException("This goal's progress exceeds the supported numeric range.");
        var remaining = Math.Max(0, definition.Target - actual);
        var days = period.End.DayNumber - period.Start.DayNumber + 1;
        var completed = Math.Clamp(today.DayNumber - period.Start.DayNumber, 0, days);
        var remainingDays = days - completed;
        var state = State(period, today);
        return new(period.Key, period.Start, period.End, definition, actual, percentage, remaining, state,
            state == GoalPeriodState.Active ? definition.Target * (completed / (double)days) : null,
            state == GoalPeriodState.Active && remainingDays > 0 ? remaining / remainingDays : null, completed, remainingDays);
    }

    private static GoalContributionDay[] Contribute(PersonalGoal goal, GoalCalendar.Period period,
        LocalActivity[] activities, CancellationToken cancellationToken)
    {
        var definition = GoalCalendar.Definition(goal, period.Key);
        var days = new Dictionary<DateOnly, List<GoalContribution>>();
        foreach (var item in activities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Date < period.Start || item.Date > period.End ||
                (definition.Sport.HasValue && item.Activity.Sport != definition.Sport)) continue;
            if (!days.TryGetValue(item.Date, out var members)) days[item.Date] = members = [];
            members.Add(new(item.Activity.Id, item.Activity.Title, item.Activity.Sport,
                item.Activity.StartTime, Amount(item.Activity, goal.Metric)));
        }
        var result = new List<GoalContributionDay>();
        foreach (var day in days.OrderByDescending(x => x.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var members = day.Value.ToArray();
            result.Add(new(day.Key, goal.Metric == GoalMetric.ActiveDays ? 1 : Sum(members.Select(x => x.Amount)), members));
        }
        return result.ToArray();
    }

    private static double Amount(ActivityRow row, GoalMetric metric)
    {
        var value = metric switch
        {
            GoalMetric.Distance => row.Distance,
            GoalMetric.MovingTime => row.MovingTime,
            GoalMetric.Ascent => row.Ascent,
            GoalMetric.ActivityCount or GoalMetric.ActiveDays => 1,
            _ => throw new InvalidOperationException("This goal has an unsupported metric.")
        };
        if (!double.IsFinite(value) || value < 0) throw new InvalidOperationException("An activity summary has an invalid goal contribution.");
        return value;
    }

    private static double Sum(IEnumerable<double> values)
    {
        var total = values.Sum();
        if (!double.IsFinite(total)) throw new InvalidOperationException("The activity total exceeds the supported numeric range.");
        return total;
    }

    private static DateOnly? NextEditable(PersonalGoal goal, GoalCalendar.Period? primary, DateOnly today)
    {
        if (goal.ArchiveFromEdition.HasValue || goal.Recurrence == GoalRecurrence.Once || primary is null) return null;
        // Before the first edition starts, the initial definition is still the upcoming template.
        return today < goal.Start ? primary.Key : GoalCalendar.Next(GoalCalendar.Schedule(goal), primary.Key);
    }

    private static GoalPeriodState State(GoalCalendar.Period period, DateOnly today) =>
        today < period.Start ? GoalPeriodState.Upcoming : today > period.End ? GoalPeriodState.Ended : GoalPeriodState.Active;

    private static GoalCalendar.Period RequireExpected(PersonalGoal goal, long version, DateOnly expected,
        GoalPeriodState expectedState, DateOnly today)
    {
        var primary = GoalCalendar.Selected(goal, today);
        if (primary is null || version != goal.MutationVersion || expected != primary.Key || expectedState != State(primary, today))
            throw new InvalidOperationException("This goal or its current edition changed. Reload the goal and try again.");
        return primary;
    }

    private static void SetDefinition(PersonalGoal goal, DateOnly key, GoalDefinition definition)
    {
        var revision = goal.Definitions.SingleOrDefault(x => x.EffectiveFromEdition == key);
        if (revision is null)
        {
            revision = new GoalDefinitionRevision { GoalId = goal.Id, EffectiveFromEdition = key };
            goal.Definitions.Add(revision);
        }
        revision.Name = definition.Name;
        revision.Sport = definition.Sport;
        revision.Target = definition.Target;
    }

    private static GoalDefinition ValidateDefinition(GoalDefinition definition, GoalMetric metric)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var name = definition.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 120) throw new ArgumentException("Goal name must contain 1 to 120 characters.");
        if (definition.Sport.HasValue && !Enum.IsDefined(definition.Sport.Value)) throw new ArgumentException("Choose a valid goal sport.");
        if (!double.IsFinite(definition.Target) || definition.Target <= 0) throw new ArgumentException("Goal target must be finite and positive.");
        if (metric is GoalMetric.ActivityCount or GoalMetric.ActiveDays && definition.Target != Math.Truncate(definition.Target))
            throw new ArgumentException("Activity and active-day targets must be whole numbers.");
        return definition with { Name = name };
    }

    private static void ValidateSchedule(GoalSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (!Enum.IsDefined(schedule.Metric)) throw new ArgumentException("Choose a valid goal metric.");
        if (!Enum.IsDefined(schedule.Recurrence)) throw new ArgumentException("Choose a valid goal recurrence.");
        if (schedule.Recurrence == GoalRecurrence.Once)
        {
            if (!schedule.End.HasValue || schedule.End < schedule.Start) throw new ArgumentException("Choose an end date on or after the goal's start date.");
            ReportingDates.Validate(new(ReportingPreset.Custom, schedule.Start, schedule.End));
        }
        else if (schedule.End.HasValue) throw new ArgumentException("Recurring goals use calendar periods, without a fixed end date.");
    }

    private static int Page(int page, int count) => Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(count / (double)GoalDetail.PageSize)));

    private static async Task<OwnerProfile> RequireOwnerAsync(ExplorerDbContext db, Guid ownerId, CancellationToken cancellationToken) =>
        await db.Owners.SingleOrDefaultAsync(x => x.Id == ownerId, cancellationToken)
        ?? throw new InvalidOperationException("This profile is unavailable. Choose another profile.");

    private static async Task<PersonalGoal> RequireGoalAsync(ExplorerDbContext db, Guid ownerId, Guid goalId, CancellationToken cancellationToken) =>
        await db.Goals.Include(x => x.Owner).Include(x => x.Definitions).AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == goalId && x.OwnerId == ownerId, cancellationToken)
        ?? throw new InvalidOperationException("This goal is unavailable for the selected profile.");
}
