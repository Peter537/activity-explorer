using System.Globalization;

namespace ActivityExplorer.Core.Models;

public sealed class BadgeSnapshot(
    Guid ownerId, string ownerName, string timeZoneId, int firstYear, DateOnly month, DateOnly asOf, DateOnly today,
    IReadOnlyList<BadgeDefinition> definitions, IReadOnlyList<BadgeEdition> populatedEditions)
{
    private readonly IReadOnlyList<BadgeDefinition> _orderedDefinitions = definitions;
    private readonly Dictionary<string, BadgeDefinition> _definitions = definitions.ToDictionary(x => x.Id, StringComparer.Ordinal);
    private readonly Dictionary<(string Id, DateOnly? Start), BadgeEdition> _populated = populatedEditions.ToDictionary(x => (x.Definition.Id, x.StartsOn));
    private readonly ILookup<string, BadgeEdition> _byDefinition = populatedEditions.ToLookup(x => x.Definition.Id, StringComparer.Ordinal);
    private readonly Dictionary<string, int> _earnedByFamily = populatedEditions.Where(x => x.Status == BadgeStatus.Completed)
        .GroupBy(x => x.Definition.FamilyId, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);

    public Guid OwnerId { get; } = ownerId;
    public string OwnerName { get; } = ownerName;
    public string TimeZoneId { get; } = timeZoneId;
    public int FirstYear { get; } = firstYear;
    public DateOnly Month { get; } = month;
    public DateOnly AsOf { get; } = asOf;
    public DateOnly Today { get; } = today;
    public BadgeLevel Level { get; } = BadgeLevel.FromPoints(populatedEditions.Where(x => x.Status == BadgeStatus.Completed).Sum(x => (long)x.Definition.Points));
    public int EarnedCount { get; } = populatedEditions.Count(x => x.Status == BadgeStatus.Completed);
    public IEnumerable<BadgeEdition> Editions => Enumerate(_orderedDefinitions);
    public IReadOnlyList<BadgeEdition> ForMonth => _orderedDefinitions.Select(ForSelectedMonth).OfType<BadgeEdition>().ToArray();

    public BadgeEdition? FindEdition(string id, string? edition)
    {
        if (!_definitions.TryGetValue(id, out var definition)) return null;
        if (edition is null) return ForSelectedMonth(definition);
        if (definition.Period == BadgePeriod.Lifetime)
            return edition == "lifetime" ? Get(definition, new(null, null)) : null;

        var separator = edition.IndexOf('-');
        var yearText = separator < 0 ? edition.AsSpan() : edition.AsSpan(0, separator);
        if (!int.TryParse(yearText, NumberStyles.None, CultureInfo.InvariantCulture, out var year)) return null;
        if (year >= FirstYear && year <= Today.Year)
        {
            foreach (var period in BadgeCalendar.ForYear(definition, year))
                if (period.Edition(definition.Period) == edition) return Get(definition, period);
        }
        else if (BadgeCalendar.FallbackLeapDay(definition, FirstYear, Today.Year) is { } leapDay &&
                 leapDay.Edition(definition.Period) == edition)
        {
            return Get(definition, leapDay);
        }
        return null;
    }

    public IEnumerable<BadgeEdition> Collection(Func<BadgeDefinition, bool> filter, BadgeStatus? status, CancellationToken cancellationToken)
    {
        foreach (var definition in _orderedDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filter(definition) && Latest(definition, status, cancellationToken) is { } edition) yield return edition;
        }
    }

    public int FamilyEarnedCount(string familyId) => _earnedByFamily.GetValueOrDefault(familyId);

    public IEnumerable<int?> FamilyYears(string familyId)
    {
        var definition = _orderedDefinitions.FirstOrDefault(x => x.FamilyId == familyId);
        if (definition is null) yield break;
        if (definition.Period == BadgePeriod.Lifetime)
        {
            yield return null;
            yield break;
        }
        if (BadgeCalendar.FallbackLeapDay(definition, FirstYear, Today.Year) is { Start: { } leapDay })
            yield return leapDay.Year;
        for (var year = Today.Year; year >= FirstYear; year--)
            if (definition.Period != BadgePeriod.SpecialDate || BadgeCalendar.SpecialDate(definition, year).HasValue)
                yield return year;
    }

    public IReadOnlyList<BadgeEdition> FamilyEditions(string familyId, int? year)
    {
        var family = _orderedDefinitions.Where(x => x.FamilyId == familyId);
        var editions = new List<BadgeEdition>();
        foreach (var definition in family)
        {
            if (definition.Period == BadgePeriod.Lifetime)
            {
                if (year is null) editions.Add(Get(definition, new(null, null)));
            }
            else if (year is int calendarYear && calendarYear >= FirstYear && calendarYear <= Today.Year)
            {
                editions.AddRange(BadgeCalendar.ForYear(definition, calendarYear).Select(period => Get(definition, period)));
            }
            else if (BadgeCalendar.FallbackLeapDay(definition, FirstYear, Today.Year) is { } leapDay && leapDay.Start?.Year == year)
            {
                editions.Add(Get(definition, leapDay));
            }
        }
        return editions;
    }

    internal IEnumerable<BadgeEdition> EnumerateFamily(string familyId) => Enumerate(_orderedDefinitions.Where(x => x.FamilyId == familyId));

    private BadgeEdition? ForSelectedMonth(BadgeDefinition definition)
    {
        if (definition.Period != BadgePeriod.SpecialDate)
        {
            var start = BadgeCalendar.PeriodStart(Month, definition.Period);
            return Get(definition, BadgeCalendar.FromStart(start, definition.Period));
        }
        return BadgeCalendar.SpecialDate(definition, Month.Year) is { } date && date.Month == Month.Month
            ? Get(definition, new(date, date))
            : null;
    }

    private BadgeEdition? Latest(BadgeDefinition definition, BadgeStatus? status, CancellationToken cancellationToken)
    {
        if (status is BadgeStatus.Completed or BadgeStatus.InProgress or BadgeStatus.Incomplete)
            return _byDefinition[definition.Id].Where(x => x.Status == status).MaxBy(x => x.StartsOn);

        if (status != BadgeStatus.Upcoming)
        {
            for (var year = AsOf.Year; year >= FirstYear; year--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var period in BadgeCalendar.ForYear(definition, year, descending: true))
                {
                    if (period.Start > AsOf) continue;
                    if (_populated.TryGetValue((definition.Id, period.Start), out var recorded))
                    {
                        if (status is null) return recorded;
                    }
                    else
                    {
                        return Get(definition, period);
                    }
                }
                if (definition.Period == BadgePeriod.Lifetime) return null;
            }
        }

        if (status == BadgeStatus.NotStarted) return null;
        for (var year = AsOf.Year; year <= Today.Year; year++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var period in BadgeCalendar.ForYear(definition, year))
                if (period.Start > AsOf) return Get(definition, period);
            if (definition.Period == BadgePeriod.Lifetime) return null;
        }
        return BadgeCalendar.FallbackLeapDay(definition, FirstYear, Today.Year) is { } leapDay ? Get(definition, leapDay) : null;
    }

    private IEnumerable<BadgeEdition> Enumerate(IEnumerable<BadgeDefinition> source)
    {
        foreach (var definition in source)
        {
            if (definition.Period == BadgePeriod.Lifetime)
            {
                yield return Get(definition, new(null, null));
                continue;
            }
            if (BadgeCalendar.FallbackLeapDay(definition, FirstYear, Today.Year) is { } leapDay)
                yield return Get(definition, leapDay);
            for (var year = FirstYear; year <= Today.Year; year++)
                foreach (var period in BadgeCalendar.ForYear(definition, year))
                    yield return Get(definition, period);
        }
    }

    private BadgeEdition Get(BadgeDefinition definition, BadgeCalendarPeriod period) =>
        _populated.TryGetValue((definition.Id, period.Start), out var stored) ? stored :
            new(definition, period.Edition(definition.Period), period.Start, period.End, 0,
                period.Start > AsOf ? BadgeStatus.Upcoming : BadgeStatus.NotStarted, null, []);
}
