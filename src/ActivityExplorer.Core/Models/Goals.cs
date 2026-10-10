using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Core.Models;

public enum GoalMetric { Distance, MovingTime, Ascent, ActivityCount, ActiveDays }
public enum GoalRecurrence { Once, Weekly, Monthly, Yearly }
public enum GoalPeriodState { Upcoming, Active, Ended }
public enum GoalListView { CurrentAndUpcoming, All, Archived }
public enum GoalEditScope { ThisEdition, NextEditions }

public sealed record GoalDefinition(string Name, SportKind? Sport, double Target);
public sealed record GoalSchedule(GoalMetric Metric, GoalRecurrence Recurrence, DateOnly Start, DateOnly? End);
public sealed record GoalInfo(Guid Id, Guid OwnerId, string OwnerName, string TimeZoneId,
    GoalSchedule Schedule, GoalDefinition Definition, bool Archived, long MutationVersion);
public sealed record GoalEdition(DateOnly Key, DateOnly Start, DateOnly End, GoalDefinition Definition,
    double Actual, double Percentage, double Remaining, GoalPeriodState State,
    double? PaceTarget, double? RemainingPerDay, int CompletedDays, int RemainingDays)
{
    public bool Achieved => Actual >= Definition.Target;
}
public sealed record GoalSummary(GoalInfo Goal, GoalEdition? Edition, string? Error = null);
public sealed record GoalContribution(Guid ActivityId, string Title, SportKind Sport, DateTimeOffset StartTime, double Amount);
public sealed record GoalContributionDay(DateOnly Date, double Amount, IReadOnlyList<GoalContribution> Activities);
public sealed record GoalDetail(GoalInfo Goal, GoalEdition? Edition,
    IReadOnlyList<GoalEdition> History, int HistoryCount, int HistoryPage,
    IReadOnlyList<GoalContributionDay> Contributions, int ContributionCount, int ContributionPage,
    GoalEdition? EditableEdition, DateOnly? NextEditionStart, GoalDefinition? NextDefinition)
{
    public const int PageSize = 20;
}
public sealed record CreateGoalRequest(Guid OwnerId, GoalSchedule Schedule, GoalDefinition Definition);
public sealed record EditGoalRequest(Guid OwnerId, Guid GoalId, long ExpectedVersion,
    DateOnly ExpectedEdition, GoalEditScope Scope, GoalDefinition Definition, GoalPeriodState ExpectedState);
public sealed record ArchiveGoalRequest(Guid OwnerId, Guid GoalId, long ExpectedVersion, DateOnly ExpectedEdition,
    GoalPeriodState ExpectedState);
