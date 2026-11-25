using System.Globalization;

namespace ActivityExplorer.Core.Models;

public readonly record struct BadgeCalendarPeriod(DateOnly? Start, DateOnly? End)
{
    public string Edition(BadgePeriod period) => period switch
    {
        BadgePeriod.Lifetime => "lifetime",
        BadgePeriod.Monthly => Start!.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        BadgePeriod.Quarterly => string.Create(CultureInfo.InvariantCulture, $"{Start!.Value.Year}-Q{(Start!.Value.Month - 1) / 3 + 1}"),
        _ => Start!.Value.Year.ToString(CultureInfo.InvariantCulture)
    };
}

public static class BadgeCalendar
{
    public static DateOnly EndOfMonth(DateOnly date) => new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));

    public static DateOnly PeriodStart(DateOnly date, BadgePeriod period) => period switch
    {
        BadgePeriod.Monthly => new(date.Year, date.Month, 1),
        BadgePeriod.Quarterly => new(date.Year, (date.Month - 1) / 3 * 3 + 1, 1),
        BadgePeriod.Annual => new(date.Year, 1, 1),
        _ => date
    };

    public static BadgeCalendarPeriod FromStart(DateOnly start, BadgePeriod period) => period switch
    {
        BadgePeriod.Lifetime => new(null, null),
        BadgePeriod.Monthly => new(start, EndOfMonth(start)),
        BadgePeriod.Quarterly => new(start, EndOfMonth(new(start.Year, start.Month + 2, 1))),
        BadgePeriod.Annual => new(start, new(start.Year, 12, 31)),
        _ => new(start, start)
    };

    internal static IEnumerable<BadgeCalendarPeriod> ForYear(BadgeDefinition definition, int year, bool descending = false)
    {
        var period = definition.Period;
        if (period == BadgePeriod.Lifetime)
        {
            yield return new(null, null);
            yield break;
        }
        if (period == BadgePeriod.SpecialDate)
        {
            if (SpecialDate(definition, year) is { } date) yield return new(date, date);
            yield break;
        }

        var step = period == BadgePeriod.Monthly ? 1 : period == BadgePeriod.Quarterly ? 3 : 12;
        for (var month = descending ? 13 - step : 1; month is >= 1 and <= 12; month += descending ? -step : step)
            yield return FromStart(new(year, month, 1), period);
    }

    internal static DateOnly? SpecialDate(BadgeDefinition definition, int year)
    {
        var month = definition.CalendarMonth!.Value;
        var day = definition.CalendarDay!.Value;
        return day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;
    }

    internal static BadgeCalendarPeriod? FallbackLeapDay(BadgeDefinition definition, int firstYear, int lastYear)
    {
        if (definition.CalendarMonth != 2 || definition.CalendarDay != 29) return null;
        for (var year = lastYear; year >= Math.Max(firstYear, lastYear - 7); year--)
            if (DateTime.IsLeapYear(year)) return null;
        for (var year = lastYear + 1; year <= Math.Min(9999, lastYear + 8); year++)
        {
            if (!DateTime.IsLeapYear(year)) continue;
            var leapDay = new DateOnly(year, 2, 29);
            return new(leapDay, leapDay);
        }
        return null;
    }
}
