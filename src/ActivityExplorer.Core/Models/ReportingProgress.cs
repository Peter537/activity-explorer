using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Core.Models;

public enum PeriodComparison { PreviousPeriod, PriorYear }
public enum TrendGranularity { Weekly, Monthly }

public sealed record DashboardQuery(
    Guid? OwnerId = null, SportKind? Sport = null, ReportingDateSelection? Period = null,
    PeriodComparison Comparison = PeriodComparison.PriorYear,
    TrendGranularity Granularity = TrendGranularity.Monthly, DateTimeOffset? AsOfUtc = null);

public sealed record ReportingTotals(
    int ActivityCount, double DistanceMeters, double MovingSeconds, double ElevationMeters, int ActiveDays)
{
    public static ReportingTotals Empty { get; } = new(0, 0, 0, 0, 0);
}

public sealed record ReportingBucket(DateOnly Start, DateOnly End, ReportingTotals Totals);

public sealed record DashboardSummary(
    ReportingTotals Totals, ReportingTotals? ComparisonTotals, string? ComparisonUnavailableReason,
    IReadOnlyList<ResolvedOwnerPeriod> Periods, IReadOnlyList<ResolvedOwnerPeriod> ComparisonPeriods,
    IReadOnlyList<ReportingBucket> Trend, IReadOnlyList<SportTotal> Sports,
    IReadOnlyList<ActivitySummary> Recent, IReadOnlyList<PersonalRecord> Highlights,
    IReadOnlyList<NamedTotal> Devices, IReadOnlyList<NamedTotal> Gear, bool HasAnyActivities);

public sealed record TrainingCalendarQuery(
    Guid? OwnerId = null, SportKind? Sport = null, DateOnly? Month = null, DateTimeOffset? AsOfUtc = null);

public sealed record TrainingCalendarDay(DateOnly Date, ReportingTotals Totals, IReadOnlyList<SportTotal> Sports);

public sealed record TrainingCalendarSummary(
    DateOnly Month, ReportingTotals Totals, IReadOnlyList<ResolvedOwnerPeriod> Periods,
    IReadOnlyList<TrainingCalendarDay> Days, IReadOnlyList<ReportingBucket> Weeks);

public static class ReportingProgress
{
    public static (DateOnly From, DateOnly To) ComparisonDates(DateOnly from, DateOnly to, PeriodComparison comparison)
    {
        if (from > to) throw new ArgumentException("The From date cannot be after the To date.");
        if (!Enum.IsDefined(comparison)) throw new ArgumentException("Choose a valid comparison period.");
        try
        {
            return comparison == PeriodComparison.PreviousPeriod
                ? (from.AddDays(-(to.DayNumber - from.DayNumber + 1)), from.AddDays(-1))
                : (from.AddYears(-1), to.AddYears(-1));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentException("The comparison dates are outside the supported calendar range.", exception);
        }
    }

    public static double? PercentageChange(double current, double baseline) =>
        baseline == 0 ? null : (current - baseline) / baseline * 100;

    public static DateOnly BucketStart(DateOnly date, TrendGranularity granularity) => granularity switch
    {
        TrendGranularity.Weekly => date.AddDays(-Math.Min(date.DayNumber, ((int)date.DayOfWeek + 6) % 7)),
        TrendGranularity.Monthly => new(date.Year, date.Month, 1),
        _ => throw new ArgumentException("Choose a valid trend interval.")
    };

    public static DateOnly BucketEnd(DateOnly start, TrendGranularity granularity) => granularity switch
    {
        TrendGranularity.Weekly => start.AddDays(Math.Min(6, DateOnly.MaxValue.DayNumber - start.DayNumber)),
        TrendGranularity.Monthly => new(start.Year, start.Month, DateTime.DaysInMonth(start.Year, start.Month)),
        _ => throw new ArgumentException("Choose a valid trend interval.")
    };
}
