using System.Globalization;

namespace ActivityExplorer.Core.Models;

public enum ReportingPreset { AllTime, Custom, Today, ThisWeek, LastWeek, ThisMonth, LastMonth, YearToDate, LastYear }

public sealed record ReportingDateSelection(ReportingPreset Preset, DateOnly? From = null, DateOnly? To = null);

public sealed record ResolvedOwnerPeriod(
    Guid OwnerId, string OwnerName, string TimeZoneId, DateOnly? From, DateOnly? To,
    DateTimeOffset? FromUtc, DateTimeOffset? ToUtc);

public static class ReportingTimeZone
{
    public const string DefaultId = "Europe/Copenhagen";

    public static TimeZoneInfo Resolve(string? id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(id) ? DefaultId : id); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException("The profile's reporting timezone is unavailable. Choose a valid timezone in Profiles.", exception);
        }
    }
}

public static class ReportingDates
{
    public static ReportingDateSelection Parse(string? period, string? from, string? to)
    {
        var preset = string.IsNullOrWhiteSpace(period)
            ? string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to) ? ReportingPreset.AllTime : ReportingPreset.Custom
            : FromSlug(period);
        if (preset != ReportingPreset.Custom) return new(preset);
        var selection = new ReportingDateSelection(preset, ParseDate(from, "From"), ParseDate(to, "To"));
        Validate(selection);
        return selection;
    }

    public static ReportingPreset FromSlug(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all-time" => ReportingPreset.AllTime,
        "custom" => ReportingPreset.Custom,
        "today" => ReportingPreset.Today,
        "this-week" => ReportingPreset.ThisWeek,
        "last-week" => ReportingPreset.LastWeek,
        "this-month" => ReportingPreset.ThisMonth,
        "last-month" => ReportingPreset.LastMonth,
        "year-to-date" => ReportingPreset.YearToDate,
        "last-year" => ReportingPreset.LastYear,
        _ => throw new ArgumentException("Choose a valid reporting period.")
    };

    public static string ToSlug(ReportingPreset preset) => preset switch
    {
        ReportingPreset.AllTime => "all-time",
        ReportingPreset.Custom => "custom",
        ReportingPreset.Today => "today",
        ReportingPreset.ThisWeek => "this-week",
        ReportingPreset.LastWeek => "last-week",
        ReportingPreset.ThisMonth => "this-month",
        ReportingPreset.LastMonth => "last-month",
        ReportingPreset.YearToDate => "year-to-date",
        ReportingPreset.LastYear => "last-year",
        _ => throw new ArgumentException("Choose a valid reporting period.")
    };

    public static void Validate(ReportingDateSelection selection)
    {
        if (!Enum.IsDefined(selection.Preset)) throw new ArgumentException("Choose a valid reporting period.");
        if (selection.Preset != ReportingPreset.Custom) return;
        if (selection.From > selection.To) throw new ArgumentException("The From date cannot be after the To date.");
        if (selection.To == DateOnly.MaxValue) throw new ArgumentException("Choose a To date before December 31, 9999.");
    }

    public static ResolvedOwnerPeriod Resolve(
        ReportingDateSelection selection, Guid ownerId, string ownerName, string? timeZoneId, DateTimeOffset asOfUtc)
    {
        Validate(selection);
        var zone = ReportingTimeZone.Resolve(timeZoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(asOfUtc, zone).DateTime);
        try
        {
            var monday = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
            var month = new DateOnly(today.Year, today.Month, 1);
            (DateOnly? from, DateOnly? to) = selection.Preset switch
            {
                ReportingPreset.AllTime => (null, null),
                ReportingPreset.Custom => (selection.From, selection.To),
                ReportingPreset.Today => (today, today),
                ReportingPreset.ThisWeek => (monday, monday.AddDays(6)),
                ReportingPreset.LastWeek => (monday.AddDays(-7), monday.AddDays(-1)),
                ReportingPreset.ThisMonth => (month, month.AddMonths(1).AddDays(-1)),
                ReportingPreset.LastMonth => (month.AddMonths(-1), month.AddDays(-1)),
                ReportingPreset.YearToDate => (new(today.Year, 1, 1), today),
                ReportingPreset.LastYear => (new(today.Year - 1, 1, 1), new(today.Year - 1, 12, 31)),
                _ => throw new ArgumentException("Choose a valid reporting period.")
            };
            return new(ownerId, ownerName, zone.Id, from, to,
                from.HasValue ? StartOfDayUtc(from.Value, zone) : null,
                to.HasValue ? StartOfDayUtc(to.Value.AddDays(1), zone) : null);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentException("The reporting dates are outside the supported timezone range. Choose dates further from the calendar limits.", exception);
        }
    }

    private static DateOnly? ParseDate(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new ArgumentException($"The {label} date must be a valid date in yyyy-MM-dd format.");
    }

    private static DateTimeOffset StartOfDayUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // A timezone may skip midnight or an entire date. The next valid wall time is the boundary.
        if (zone.IsInvalidTime(local))
        {
            var invalid = local;
            do { local = local.AddHours(1); } while (zone.IsInvalidTime(local));
            var low = invalid.Ticks;
            var high = local.Ticks;
            while (high - low > 1)
            {
                var middle = low + (high - low) / 2;
                if (zone.IsInvalidTime(new DateTime(middle, DateTimeKind.Unspecified))) low = middle;
                else high = middle;
            }
            local = new DateTime(high, DateTimeKind.Unspecified);
        }
        // Use the first occurrence of a repeated midnight so both occurrences belong to the date.
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        var boundary = new DateTimeOffset(local, offset).ToUniversalTime();
        if (TimeZoneInfo.ConvertTime(boundary, zone).DateTime == local) return boundary;

        // Base-offset changes can skip wall times without IsInvalidTime reporting a daylight-saving gap.
        var midnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified).Ticks;
        var lowUtc = Math.Max(DateTime.MinValue.Ticks, midnight - TimeSpan.TicksPerHour * 14);
        var highUtc = Math.Min(DateTime.MaxValue.Ticks, midnight + TimeSpan.TicksPerHour * 14);
        while (highUtc - lowUtc > 1)
        {
            var middle = lowUtc + (highUtc - lowUtc) / 2;
            if (TimeZoneInfo.ConvertTime(new DateTimeOffset(middle, TimeSpan.Zero), zone).DateTime.Ticks < midnight) lowUtc = middle;
            else highUtc = middle;
        }
        return new DateTimeOffset(highUtc, TimeSpan.Zero);
    }
}
