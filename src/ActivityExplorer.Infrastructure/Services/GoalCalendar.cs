using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Infrastructure.Services;

internal static class GoalCalendar
{
    internal sealed record Period(DateOnly Key, DateOnly Start, DateOnly End);

    public static GoalSchedule Schedule(PersonalGoal goal) => new(goal.Metric, goal.Recurrence, goal.Start, goal.End);

    public static DateOnly Key(GoalSchedule schedule, DateOnly date) => schedule.Recurrence switch
    {
        GoalRecurrence.Once => schedule.Start,
        GoalRecurrence.Weekly => date.AddDays(-((int)date.DayOfWeek + 6) % 7),
        GoalRecurrence.Monthly => new(date.Year, date.Month, 1),
        GoalRecurrence.Yearly => new(date.Year, 1, 1),
        _ => throw new ArgumentException("Choose a valid goal recurrence.")
    };

    public static DateOnly Next(GoalSchedule schedule, DateOnly key)
    {
        try
        {
            return schedule.Recurrence switch
            {
                GoalRecurrence.Once => schedule.End!.Value.AddDays(1),
                GoalRecurrence.Weekly => key.AddDays(7),
                GoalRecurrence.Monthly => key.AddMonths(1),
                GoalRecurrence.Yearly => key.AddYears(1),
                _ => throw new ArgumentException("Choose a valid goal recurrence.")
            };
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentException("The goal period exceeds the supported calendar. Choose earlier dates.", exception);
        }
    }

    public static Period At(GoalSchedule schedule, DateOnly key) =>
        new(key, key < schedule.Start ? schedule.Start : key,
            schedule.Recurrence == GoalRecurrence.Once ? schedule.End!.Value : Next(schedule, key).AddDays(-1));

    public static int Count(PersonalGoal goal, DateOnly today)
    {
        var schedule = Schedule(goal);
        var first = Key(schedule, schedule.Start);
        if (goal.ArchiveFromEdition <= first) return 0;
        if (schedule.Recurrence == GoalRecurrence.Once) return 1;
        var latest = Key(schedule, today < schedule.Start ? schedule.Start : today);
        if (goal.ArchiveFromEdition is { } cutoff && latest >= cutoff)
            latest = Key(schedule, cutoff.AddDays(-1));
        return schedule.Recurrence switch
        {
            GoalRecurrence.Weekly => (latest.DayNumber - first.DayNumber) / 7 + 1,
            GoalRecurrence.Monthly => (latest.Year - first.Year) * 12 + latest.Month - first.Month + 1,
            GoalRecurrence.Yearly => latest.Year - first.Year + 1,
            _ => 1
        };
    }

    public static DateOnly KeyAt(GoalSchedule schedule, int index)
    {
        var first = Key(schedule, schedule.Start);
        return schedule.Recurrence switch
        {
            GoalRecurrence.Weekly => first.AddDays(index * 7),
            GoalRecurrence.Monthly => first.AddMonths(index),
            GoalRecurrence.Yearly => first.AddYears(index),
            _ => first
        };
    }

    public static Period? Selected(PersonalGoal goal, DateOnly today, DateOnly? requested = null)
    {
        var count = Count(goal, today);
        if (count == 0) return null;
        var schedule = Schedule(goal);
        var last = KeyAt(schedule, count - 1);
        var key = requested ?? last;
        if (key < KeyAt(schedule, 0) || key > last || Key(schedule, key) != key) return null;
        return At(schedule, key);
    }

    public static GoalDefinition Definition(PersonalGoal goal, DateOnly key)
    {
        var revision = goal.Definitions.Where(x => x.EffectiveFromEdition <= key)
            .MaxBy(x => x.EffectiveFromEdition)
            ?? throw new InvalidOperationException("This goal has no readable definition.");
        return new(revision.Name, revision.Sport, revision.Target);
    }

    public static DateOnly Today(DateTimeOffset now, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
}
