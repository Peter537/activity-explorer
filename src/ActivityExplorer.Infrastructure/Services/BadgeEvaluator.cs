using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Infrastructure.Services;

public static class BadgeEvaluator
{
    private sealed record LocalActivity(BadgeActivity Activity, DateOnly Date, int Hour, double Moving);
    private sealed record Contribution(DateOnly Date, double Value, IReadOnlyList<BadgeEvidence> Evidence);
    private sealed record EvaluationInput(string TimeZoneId, int FirstYear, DateOnly SelectedMonth, DateOnly AsOf, DateOnly Today, LocalActivity[] Activities);
    private readonly record struct EvaluatedEdition(BadgeDefinition Definition, BadgeCalendarPeriod Period,
        double Progress, DateOnly? EarnedOn, IReadOnlyList<BadgeEvidence> Evidence);

    public static BadgeSnapshot Evaluate(Guid ownerId, string ownerName, string? timeZoneId,
        IReadOnlyList<BadgeActivity> activities, DateOnly? month, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var input = Capture(timeZoneId, activities, month, now, cancellationToken);
        var populated = EvaluatePopulated(input.Activities, includeEvidence: true, cancellationToken).Select(result =>
        {
            var ended = result.Period.End < input.AsOf || result.Period.End == input.AsOf && input.AsOf < input.Today;
            var status = result.EarnedOn.HasValue ? BadgeStatus.Completed : ended ? BadgeStatus.Incomplete : BadgeStatus.InProgress;
            var definition = result.Definition;
            return new BadgeEdition(definition, result.Period.Edition(definition.Period), result.Period.Start, result.Period.End,
                result.Progress, status, result.EarnedOn, result.Evidence);
        }).ToArray();
        return new(ownerId, ownerName, input.TimeZoneId, input.FirstYear, input.SelectedMonth, input.AsOf, input.Today,
            BadgeCatalog.Definitions, populated);
    }

    internal static BadgeProfileOverview EvaluateOverview(Guid ownerId, string ownerName, string? timeZoneId,
        IReadOnlyList<BadgeActivity> activities, DateOnly? month, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var input = Capture(timeZoneId, activities, month, now, cancellationToken);
        long points = 0;
        var count = 0;
        foreach (var result in EvaluatePopulated(input.Activities, includeEvidence: false, cancellationToken))
        {
            if (!result.EarnedOn.HasValue) continue;
            points += result.Definition.Points;
            count++;
        }
        return new(ownerId, ownerName, BadgeLevel.FromPoints(points), count, input.AsOf, null);
    }

    private static EvaluationInput Capture(string? timeZoneId, IReadOnlyList<BadgeActivity> activities,
        DateOnly? month, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var zone = ReportingTimeZone.Resolve(timeZoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var local = activities.Where(x =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return x.StartTimeUtc <= now;
            }).DistinctBy(x => x.Id)
            .OrderBy(x => x.StartTimeUtc).ThenBy(x => x.Id).Select(x =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var time = TimeZoneInfo.ConvertTime(x.StartTimeUtc, zone);
                return new LocalActivity(x, DateOnly.FromDateTime(time.DateTime), time.Hour,
                    x.MovingTimeSource == MovingTimeSource.Unavailable ? 0 : Valid(x.MovingSeconds));
            }).ToArray();
        var firstYear = local.Length == 0 ? today.Year : local.Min(x => x.Date.Year);
        var selected = month ?? new DateOnly(today.Year, today.Month, 1);
        selected = new DateOnly(Math.Clamp(selected.Year, firstYear, today.Year), selected.Month, 1);
        var endOfMonth = BadgeCalendar.EndOfMonth(selected);
        var asOf = endOfMonth < today ? endOfMonth : today;
        return new(zone.Id, firstYear, selected, asOf, today, local.Where(x => x.Date <= asOf).ToArray());
    }

    private static IEnumerable<EvaluatedEdition> EvaluatePopulated(LocalActivity[] activities, bool includeEvidence,
        CancellationToken cancellationToken)
    {
        var months = activities.ToLookup(x => BadgeCalendar.PeriodStart(x.Date, BadgePeriod.Monthly));
        var quarters = activities.ToLookup(x => BadgeCalendar.PeriodStart(x.Date, BadgePeriod.Quarterly));
        var years = activities.ToLookup(x => BadgeCalendar.PeriodStart(x.Date, BadgePeriod.Annual));
        var specialDates = activities.ToLookup(x => (x.Date.Month, x.Date.Day));
        var lifetime = activities.ToLookup(_ => DateOnly.MinValue);
        foreach (var family in BadgeCatalog.Definitions.GroupBy(x => (x.Period, x.Sport, x.Measure, x.CalendarMonth, x.CalendarDay)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var representative = family.First();
            var dateGroups = family.Key.Period switch
            {
                BadgePeriod.Monthly => months,
                BadgePeriod.Quarterly => quarters,
                BadgePeriod.Annual => years,
                BadgePeriod.SpecialDate => specialDates[(family.Key.CalendarMonth!.Value, family.Key.CalendarDay!.Value)].ToLookup(x => x.Date),
                _ => lifetime
            };
            foreach (var members in dateGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = Contributions(representative, members.Where(x => representative.Sport is null ||
                    x.Activity.Sport == representative.Sport), includeEvidence, cancellationToken);
                if (values.Count == 0) continue;
                var period = BadgeCalendar.FromStart(members.Key, representative.Period);
                foreach (var definition in family)
                    yield return EvaluateEdition(definition, period, values, includeEvidence, cancellationToken);
            }
        }
    }

    private static EvaluatedEdition EvaluateEdition(BadgeDefinition definition, BadgeCalendarPeriod period,
        IReadOnlyList<Contribution> values, bool includeEvidence, CancellationToken cancellationToken)
    {
        var maximum = definition.Measure is BadgeMeasure.SingleDistance or BadgeMeasure.SingleAscent or BadgeMeasure.ConsistentWeeks;
        var scale = UnitScale(definition.Measure);
        double progress = 0;
        DateOnly? earned = null;
        List<BadgeEvidence>? evidence = includeEvidence ? [] : null;
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (maximum)
            {
                if (value.Value > progress)
                {
                    progress = value.Value;
                    evidence?.Clear();
                    evidence?.AddRange(value.Evidence);
                }
            }
            else
            {
                progress += value.Value;
                evidence?.AddRange(value.Evidence);
            }
            if (progress >= definition.Target * scale)
            {
                earned = value.Date;
                break;
            }
        }
        return new(definition, period, Math.Min(progress / scale, definition.Target), earned,
            evidence is not null ? evidence : Array.Empty<BadgeEvidence>());
    }

    private static double Valid(double value) => double.IsFinite(value) && value > 0 ? value : 0;
    private static double UnitScale(BadgeMeasure measure) => measure switch
    {
        BadgeMeasure.Distance or BadgeMeasure.SingleDistance => 1000,
        BadgeMeasure.MovingTime => 3600,
        _ => 1
    };

    private static IReadOnlyList<Contribution> Contributions(BadgeDefinition definition, IEnumerable<LocalActivity> activities,
        bool includeEvidence, CancellationToken cancellationToken)
    {
        if (definition.Measure is BadgeMeasure.ActiveDays or BadgeMeasure.ConsistentWeeks)
        {
            var days = activities.Where(x =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return x.Moving > 0;
                }).GroupBy(x => x.Date).OrderBy(x => x.Key)
                .Where(x => x.Sum(a => a.Moving) >= 1200)
                .Select(x => new Contribution(x.Key, 1, includeEvidence ? x.Select(a => Evidence(a, a.Moving / 60)).ToArray() : [])).ToArray();
            if (definition.Measure == BadgeMeasure.ActiveDays) return days;
            var weeks = days.GroupBy(x => x.Date.AddDays(-((int)x.Date.DayOfWeek + 6) % 7))
                .Where(x => x.Count() >= 3).OrderBy(x => x.Key).ToArray();
            var result = new List<Contribution>();
            var chain = new List<BadgeEvidence>();
            DateOnly? previous = null;
            var count = 0;
            foreach (var week in weeks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (previous?.AddDays(7) != week.Key) { count = 0; chain.Clear(); }
                var qualifying = week.Take(3).ToArray();
                chain.AddRange(qualifying.SelectMany(x => x.Evidence));
                count++;
                result.Add(new(qualifying[^1].Date, count, chain.ToArray()));
                previous = week.Key;
                if (count >= 26) break;
            }
            return result;
        }
        var sports = new HashSet<SportKind>();
        var values = new List<Contribution>();
        foreach (var item in activities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var eligible = item.Moving >= 600;
            var amount = definition.Measure switch
            {
                BadgeMeasure.Distance or BadgeMeasure.SingleDistance => Valid(item.Activity.DistanceMeters),
                BadgeMeasure.Ascent or BadgeMeasure.SingleAscent => Valid(item.Activity.AscentMeters),
                BadgeMeasure.MovingTime => item.Moving,
                BadgeMeasure.ActivityCount or BadgeMeasure.SpecialDate => eligible ? 1 : 0,
                BadgeMeasure.Morning => eligible && item.Hour is >= 4 and < 7 ? 1 : 0,
                BadgeMeasure.Night => eligible && (item.Hour >= 22 || item.Hour < 4) ? 1 : 0,
                BadgeMeasure.Variety => eligible && sports.Add(item.Activity.Sport) ? 1 : 0,
                _ => 0
            };
            if (amount > 0) values.Add(new(item.Date, amount, includeEvidence ? [Evidence(item, amount / UnitScale(definition.Measure))] : []));
        }
        return values;
    }

    private static BadgeEvidence Evidence(LocalActivity item, double amount) => new(item.Activity.Id, item.Activity.Title, item.Date, amount);
}
