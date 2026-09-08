using System.Globalization;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Infrastructure.Services;

public static class BadgeEvaluator
{
    private sealed record LocalActivity(BadgeActivity Activity, DateOnly Date, int Hour, double Moving);
    private sealed record Contribution(DateOnly Date, double Value, IReadOnlyList<BadgeEvidence> Evidence);

    public static BadgeSnapshot Evaluate(Guid ownerId, string ownerName, string? timeZoneId,
        IReadOnlyList<BadgeActivity> activities, DateOnly? month, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var zone = BadgeTimeZone.Resolve(timeZoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var local = activities.Where(x => x.StartTimeUtc <= now).DistinctBy(x => x.Id)
            .OrderBy(x => x.StartTimeUtc).ThenBy(x => x.Id).Select(x =>
            {
                var time = TimeZoneInfo.ConvertTime(x.StartTimeUtc, zone);
                return new LocalActivity(x, DateOnly.FromDateTime(time.DateTime), time.Hour,
                    x.MovingTimeSource == MovingTimeSource.Unavailable ? 0 : Valid(x.MovingSeconds));
            }).ToArray();
        var firstYear = local.Length == 0 ? today.Year : local.Min(x => x.Date.Year);
        var selected = month ?? new DateOnly(today.Year, today.Month, 1);
        selected = new DateOnly(Math.Clamp(selected.Year, firstYear, today.Year), selected.Month, 1);
        var endOfMonth = selected.AddMonths(1).AddDays(-1);
        var asOf = endOfMonth < today ? endOfMonth : today;
        local = local.Where(x => x.Date <= asOf).ToArray();
        var periods = new Dictionary<(DateOnly?, DateOnly?), LocalActivity[]>();
        var contributions = new Dictionary<(SportKind?, BadgeMeasure, DateOnly?, DateOnly?), IReadOnlyList<Contribution>>();
        var editions = new List<BadgeEdition>();
        foreach (var definition in BadgeCatalog.Definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var period in Periods(definition, firstYear, today.Year))
            {
                var key = (period.Start, period.End);
                if (!periods.TryGetValue(key, out var members))
                {
                    members = local.Where(x => period.Start is null || x.Date >= period.Start && x.Date <= period.End).ToArray();
                    periods.Add(key, members);
                }
                var contributionKey = (definition.Sport, definition.Measure, period.Start, period.End);
                if (!contributions.TryGetValue(contributionKey, out var values))
                {
                    values = Contributions(definition, members.Where(x => definition.Sport is null || x.Activity.Sport == definition.Sport).ToArray());
                    contributions.Add(contributionKey, values);
                }
                var maximum = definition.Measure is BadgeMeasure.SingleDistance or BadgeMeasure.SingleAscent or BadgeMeasure.ConsistentWeeks;
                var scale = UnitScale(definition.Measure);
                double progress = 0;
                DateOnly? earned = null;
                var evidence = new List<BadgeEvidence>();
                foreach (var value in values)
                {
                    if (maximum)
                    {
                        if (value.Value > progress)
                        {
                            progress = value.Value;
                            evidence.Clear();
                            evidence.AddRange(value.Evidence);
                        }
                    }
                    else
                    {
                        progress += value.Value;
                        evidence.AddRange(value.Evidence);
                    }
                    if (progress >= definition.Target * scale)
                    {
                        earned = value.Date;
                        break;
                    }
                }
                var ended = period.End < asOf || period.End == asOf && asOf < today;
                var status = earned.HasValue ? BadgeStatus.Completed : period.Start > asOf ? BadgeStatus.Upcoming :
                    progress == 0 ? BadgeStatus.NotStarted : ended ? BadgeStatus.Incomplete : BadgeStatus.InProgress;
                editions.Add(new(definition, period.Key, period.Start, period.End, Math.Min(progress / scale, definition.Target), status, earned, evidence));
            }
        }
        return new(ownerId, ownerName, zone.Id, firstYear, selected, asOf, today, editions);
    }

    private static double Valid(double value) => double.IsFinite(value) && value > 0 ? value : 0;
    private static double UnitScale(BadgeMeasure measure) => measure switch
    {
        BadgeMeasure.Distance or BadgeMeasure.SingleDistance => 1000,
        BadgeMeasure.MovingTime => 3600,
        _ => 1
    };

    private static IReadOnlyList<Contribution> Contributions(BadgeDefinition definition, LocalActivity[] activities)
    {
        if (definition.Measure is BadgeMeasure.ActiveDays or BadgeMeasure.ConsistentWeeks)
        {
            var days = activities.Where(x => x.Moving > 0).GroupBy(x => x.Date).OrderBy(x => x.Key)
                .Where(x => x.Sum(a => a.Moving) >= 1200)
                .Select(x => new Contribution(x.Key, 1, x.Select(a => Evidence(a, a.Moving / 60)).ToArray())).ToArray();
            if (definition.Measure == BadgeMeasure.ActiveDays) return days;
            var weeks = days.GroupBy(x => x.Date.AddDays(-((int)x.Date.DayOfWeek + 6) % 7))
                .Where(x => x.Count() >= 3).OrderBy(x => x.Key).ToArray();
            var result = new List<Contribution>();
            var chain = new List<BadgeEvidence>();
            DateOnly? previous = null;
            var count = 0;
            foreach (var week in weeks)
            {
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
            if (amount > 0) values.Add(new(item.Date, amount, [Evidence(item, amount / UnitScale(definition.Measure))]));
        }
        return values;
    }

    private static BadgeEvidence Evidence(LocalActivity item, double amount) => new(item.Activity.Id, item.Activity.Title, item.Date, amount);

    private static IEnumerable<(string Key, DateOnly? Start, DateOnly? End)> Periods(BadgeDefinition definition, int firstYear, int lastYear)
    {
        if (definition.Period == BadgePeriod.Lifetime) { yield return ("lifetime", null, null); yield break; }
        if (definition.CalendarMonth == 2 && definition.CalendarDay == 29 &&
            !Enumerable.Range(firstYear, lastYear - firstYear + 1).Any(DateTime.IsLeapYear))
        {
            var nextLeapYear = lastYear + 1;
            while (!DateTime.IsLeapYear(nextLeapYear)) nextLeapYear++;
            var leapDay = new DateOnly(nextLeapYear, 2, 29);
            yield return (nextLeapYear.ToString(CultureInfo.InvariantCulture), leapDay, leapDay);
        }
        for (var year = firstYear; year <= lastYear; year++)
        {
            if (definition.Period == BadgePeriod.SpecialDate)
            {
                if (definition.CalendarMonth == 2 && definition.CalendarDay == 29 && !DateTime.IsLeapYear(year)) continue;
                var date = new DateOnly(year, definition.CalendarMonth!.Value, definition.CalendarDay!.Value);
                yield return (year.ToString(CultureInfo.InvariantCulture), date, date);
                continue;
            }
            var step = definition.Period == BadgePeriod.Monthly ? 1 : definition.Period == BadgePeriod.Quarterly ? 3 : 12;
            for (var month = 1; month <= 12; month += step)
            {
                var start = new DateOnly(year, month, 1);
                var key = definition.Period == BadgePeriod.Monthly ? start.ToString("yyyy-MM", CultureInfo.InvariantCulture) :
                    definition.Period == BadgePeriod.Quarterly ? $"{year}-Q{(month - 1) / 3 + 1}" : year.ToString(CultureInfo.InvariantCulture);
                yield return (key, start, start.AddMonths(step).AddDays(-1));
            }
        }
    }
}
