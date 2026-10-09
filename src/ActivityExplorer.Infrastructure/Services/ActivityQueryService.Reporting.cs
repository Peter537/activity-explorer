using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed partial class ActivityQueryService
{
    private sealed record ReportingActivityRow(
        Guid Id, Guid OwnerId, string Title, SportKind Sport, DateTimeOffset StartTime,
        double DistanceMeters, double MovingSeconds, double ElevationMeters,
        double? AveragePowerWatts, string? Device, string? Gear, bool HasGps, bool HasPower);

    private sealed record LocalReportingActivity(ReportingActivityRow Activity, DateOnly Date);

    public async Task<DashboardSummary> GetDashboardAsync(DashboardQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!Enum.IsDefined(query.Comparison)) throw new ArgumentException("Choose a valid comparison period.");
        if (!Enum.IsDefined(query.Granularity)) throw new ArgumentException("Choose a valid trend interval.");
        ValidateReportingSport(query.Sport);
        var selection = query.Period ?? new ReportingDateSelection(ReportingPreset.YearToDate);
        var asOf = query.AsOfUtc ?? (timeProvider ?? TimeProvider.System).GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var periods = await ReportingDateQuery.ResolveAsync(db, query.OwnerId, selection, asOf, cancellationToken);
        var source = ReportingSource(db, query.OwnerId, query.Sport);
        var selected = await ReadReportingActivitiesAsync(source, periods, cancellationToken);
        var comparisonPeriods = new List<ResolvedOwnerPeriod>();
        string? comparisonUnavailable = null;
        if (selection.Preset == ReportingPreset.AllTime || selection.Preset == ReportingPreset.Custom &&
            (!selection.From.HasValue || !selection.To.HasValue))
        {
            comparisonUnavailable = "Choose a period with both a start and end date to compare totals.";
        }
        else
        {
            try
            {
                foreach (var period in periods)
                {
                    var dates = ReportingProgress.ComparisonDates(period.From!.Value, period.To!.Value, query.Comparison);
                    comparisonPeriods.Add(ReportingDates.Resolve(new(ReportingPreset.Custom, dates.From, dates.To),
                        period.OwnerId, period.OwnerName, period.TimeZoneId, asOf));
                }
            }
            catch (ArgumentException)
            {
                comparisonPeriods.Clear();
                comparisonUnavailable = "The comparison dates are outside the supported timezone or calendar range.";
            }
        }
        var comparison = comparisonUnavailable is null
            ? Totals(await ReadReportingActivitiesAsync(source, comparisonPeriods, cancellationToken))
            : null;
        var hasAny = await db.Activities.AsNoTracking().AnyAsync(
            activity => !query.OwnerId.HasValue || activity.OwnerId == query.OwnerId, cancellationToken);
        var records = await db.StatisticSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.Scope == RecordScope.All &&
                (!query.OwnerId.HasValue || snapshot.OwnerId == query.OwnerId) &&
                (!query.Sport.HasValue || snapshot.Sport == query.Sport))
            .Join(db.Owners.AsNoTracking(), snapshot => snapshot.OwnerId, owner => owner.Id, (snapshot, owner) => new { snapshot, owner })
            .Join(db.Activities.AsNoTracking(), item => item.snapshot.ActivityId, activity => activity.Id, (item, activity) =>
                new PersonalRecord(item.snapshot.Id, item.snapshot.OwnerId, item.owner.DisplayName,
                    item.snapshot.Sport, item.snapshot.Kind, item.snapshot.Key, item.snapshot.Value,
                    activity.Id, activity.Title, item.snapshot.CoveragePercent, activity.StartTimeUtc))
            .ToArrayAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var names = periods.ToDictionary(period => period.OwnerId, period => period.OwnerName);
        var recent = selected.OrderByDescending(item => item.Activity.StartTime).ThenBy(item => item.Activity.Id).Take(8)
            .Select(item => new ActivitySummary(item.Activity.Id, item.Activity.OwnerId, names[item.Activity.OwnerId],
                item.Activity.Title, item.Activity.Sport, item.Activity.StartTime, item.Activity.DistanceMeters,
                item.Activity.MovingSeconds, item.Activity.ElevationMeters, item.Activity.AveragePowerWatts,
                item.Activity.Device, item.Activity.HasGps, item.Activity.HasPower)).ToArray();
        return new(Totals(selected), comparison, comparisonUnavailable, periods, comparisonPeriods,
            Buckets(selected, periods, query.Granularity, cancellationToken), Sports(selected), recent,
            records.OrderBy(record => record.Sport).ThenBy(record => RecordCatalog.CategoryOrder(record.Kind))
                .ThenBy(record => RecordCatalog.TargetOrder(record.Sport, record.Kind, record.Key))
                .ThenBy(record => record.Key, StringComparer.Ordinal).ThenBy(record => record.OwnerId).Take(5).ToArray(),
            Names(selected.Select(item => item.Activity.Device)), Names(selected.Select(item => item.Activity.Gear)), hasAny);
    }

    public async Task<TrainingCalendarSummary> GetTrainingCalendarAsync(
        TrainingCalendarQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateReportingSport(query.Sport);
        var asOf = query.AsOfUtc ?? (timeProvider ?? TimeProvider.System).GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var owners = await ReportingDateQuery.ResolveAsync(db, query.OwnerId, new(ReportingPreset.AllTime), asOf, cancellationToken);
        var zone = ReportingTimeZone.Resolve(query.OwnerId.HasValue && owners.Count > 0 ? owners[0].TimeZoneId : null);
        var selected = query.Month ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(asOf, zone).DateTime);
        var month = new DateOnly(selected.Year, selected.Month, 1);
        if (month == new DateOnly(9999, 12, 1)) throw new ArgumentException("Choose a month before December 9999.");
        var end = month.AddMonths(1).AddDays(-1);
        var periods = owners.Select(owner => ReportingDates.Resolve(new(ReportingPreset.Custom, month, end),
            owner.OwnerId, owner.OwnerName, owner.TimeZoneId, asOf)).ToArray();
        var activities = await ReadReportingActivitiesAsync(ReportingSource(db, query.OwnerId, query.Sport), periods, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var groups = activities.ToLookup(activity => activity.Date);
        var days = Enumerable.Range(0, end.DayNumber - month.DayNumber + 1).Select(offset =>
        {
            var date = month.AddDays(offset);
            var rows = groups[date].ToArray();
            return new TrainingCalendarDay(date, Totals(rows), Sports(rows));
        }).ToArray();
        // Even an empty profile collection has a complete, navigable month.
        var calendarRange = periods.Length == 0
            ? [new ResolvedOwnerPeriod(Guid.Empty, "", zone.Id, month, end, null, null)]
            : periods;
        return new(month, Totals(activities), periods, days, Buckets(activities, calendarRange, TrendGranularity.Weekly, cancellationToken));
    }

    private static void ValidateReportingSport(SportKind? sport)
    {
        if (sport.HasValue && !Enum.IsDefined(sport.Value)) throw new ArgumentException("Choose a valid sport.");
    }

    private static IQueryable<Activity> ReportingSource(ExplorerDbContext db, Guid? owner, SportKind? sport) =>
        db.Activities.AsNoTracking().Where(activity => (!owner.HasValue || activity.OwnerId == owner) &&
            (!sport.HasValue || activity.Sport == sport));

    private static async Task<LocalReportingActivity[]> ReadReportingActivitiesAsync(
        IQueryable<Activity> source, IReadOnlyList<ResolvedOwnerPeriod> periods, CancellationToken cancellationToken)
    {
        var rows = await ReportingDateQuery.Apply(source, periods).Select(activity => new ReportingActivityRow(
            activity.Id, activity.OwnerId, activity.Title, activity.Sport, activity.StartTimeUtc,
            activity.DistanceMeters, activity.MovingTimeSeconds, activity.ElevationGainMeters,
            activity.AveragePowerWatts, activity.DeviceName, activity.GearName, activity.HasGps, activity.HasPower))
            .ToArrayAsync(cancellationToken);
        var zones = periods.ToDictionary(period => period.OwnerId, period => ReportingTimeZone.Resolve(period.TimeZoneId));
        var result = new LocalReportingActivity[rows.Length];
        for (var index = 0; index < rows.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[index];
            result[index] = new(row, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(row.StartTime, zones[row.OwnerId]).DateTime));
        }
        return result;
    }

    private static ReportingTotals Totals(IReadOnlyCollection<LocalReportingActivity> activities) => new(
        activities.Count, activities.Sum(item => item.Activity.DistanceMeters), activities.Sum(item => item.Activity.MovingSeconds),
        activities.Sum(item => item.Activity.ElevationMeters), activities.Select(item => item.Date).Distinct().Count());

    private static SportTotal[] Sports(IEnumerable<LocalReportingActivity> activities) => activities.GroupBy(item => item.Activity.Sport)
        .OrderBy(group => group.Key).Select(group => new SportTotal(group.Key, group.Count(),
            group.Sum(item => item.Activity.DistanceMeters), group.Sum(item => item.Activity.MovingSeconds),
            group.Sum(item => item.Activity.ElevationMeters))).ToArray();

    private static NamedTotal[] Names(IEnumerable<string?> values) => values.Where(value => !string.IsNullOrWhiteSpace(value))
        .GroupBy(value => value!, StringComparer.OrdinalIgnoreCase).Select(group => new NamedTotal(group.Key, group.Count()))
        .OrderByDescending(item => item.Count).ThenBy(item => item.Name).Take(5).ToArray();

    private static ReportingBucket[] Buckets(LocalReportingActivity[] activities, IReadOnlyList<ResolvedOwnerPeriod> periods,
        TrendGranularity granularity, CancellationToken cancellationToken)
    {
        var from = periods.Count > 0 && periods.All(period => period.From.HasValue)
            ? periods.Min(period => period.From) : activities.Select(activity => (DateOnly?)activity.Date).Min();
        var to = periods.Count > 0 && periods.All(period => period.To.HasValue)
            ? periods.Max(period => period.To) : activities.Select(activity => (DateOnly?)activity.Date).Max();
        if (!from.HasValue || !to.HasValue || from > to) return [];
        var groups = activities.ToLookup(activity => ReportingProgress.BucketStart(activity.Date, granularity));
        var result = new List<ReportingBucket>();
        var start = ReportingProgress.BucketStart(from.Value, granularity);
        while (start <= to.Value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = ReportingProgress.BucketEnd(start, granularity);
            result.Add(new(start < from ? from.Value : start, end > to ? to.Value : end, Totals(groups[start].ToArray())));
            if (end >= to) break;
            start = end.AddDays(1);
        }
        return result.ToArray();
    }
}
