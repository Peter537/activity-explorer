using System.Globalization;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Tests;

public sealed class ReportingDatesTests
{
    [Theory]
    [InlineData(ReportingPreset.Today, "2024-03-04", "2024-03-04")]
    [InlineData(ReportingPreset.ThisWeek, "2024-03-04", "2024-03-10")]
    [InlineData(ReportingPreset.LastWeek, "2024-02-26", "2024-03-03")]
    [InlineData(ReportingPreset.ThisMonth, "2024-03-01", "2024-03-31")]
    [InlineData(ReportingPreset.LastMonth, "2024-02-01", "2024-02-29")]
    [InlineData(ReportingPreset.YearToDate, "2024-01-01", "2024-03-04")]
    [InlineData(ReportingPreset.LastYear, "2023-01-01", "2023-12-31")]
    public void Presets_resolve_complete_calendar_periods_and_include_leap_day(
        ReportingPreset preset, string from, string to)
    {
        var period = Resolve(preset, "2024-03-04T12:00:00Z");

        Assert.Equal(Date(from), period.From);
        Assert.Equal(Date(to), period.To);
        Assert.Equal(Instant(from + "T00:00:00Z"), period.FromUtc);
        Assert.Equal(Instant(to + "T00:00:00Z").AddDays(1), period.ToUtc);
    }

    [Theory]
    [InlineData(ReportingPreset.ThisWeek, "2021-01-01T12:00:00Z", "2020-12-28", "2021-01-03")]
    [InlineData(ReportingPreset.LastWeek, "2021-01-04T12:00:00Z", "2020-12-28", "2021-01-03")]
    [InlineData(ReportingPreset.LastMonth, "2021-01-01T12:00:00Z", "2020-12-01", "2020-12-31")]
    [InlineData(ReportingPreset.LastYear, "2021-01-01T12:00:00Z", "2020-01-01", "2020-12-31")]
    public void Week_and_year_transitions_use_Monday_calendar_boundaries(
        ReportingPreset preset, string now, string from, string to)
    {
        var period = Resolve(preset, now);

        Assert.Equal(Date(from), period.From);
        Assert.Equal(Date(to), period.To);
    }

    [Fact]
    public void Same_reference_instant_can_resolve_different_owner_local_dates()
    {
        var east = Resolve(ReportingPreset.Today, "2026-10-08T22:30:00Z", "Europe/Copenhagen");
        var west = Resolve(ReportingPreset.Today, "2026-10-08T22:30:00Z", "America/Los_Angeles");

        Assert.Equal(new DateOnly(2026, 10, 9), east.From);
        Assert.Equal(new DateOnly(2026, 10, 8), west.From);
        Assert.Equal(Instant("2026-10-08T22:00:00Z"), east.FromUtc);
        Assert.Equal(Instant("2026-10-08T07:00:00Z"), west.FromUtc);
    }

    [Theory]
    [InlineData("Europe/Copenhagen", "2026-03-29", "2026-03-28T23:00:00Z", "2026-03-29T22:00:00Z")]
    [InlineData("Europe/Copenhagen", "2026-10-25", "2026-10-24T22:00:00Z", "2026-10-25T23:00:00Z")]
    public void Local_days_have_timezone_aware_half_open_boundaries(
        string zone, string day, string fromUtc, string toUtc)
    {
        var period = ReportingDates.Resolve(
            new ReportingDateSelection(ReportingPreset.Custom, Date(day), Date(day)),
            Guid.NewGuid(), "Synthetic owner", zone, Instant("2026-10-09T12:00:00Z"));

        Assert.Equal(Instant(fromUtc), period.FromUtc);
        Assert.Equal(Instant(toUtc), period.ToUtc);
    }

    [Theory]
    [InlineData("America/Sao_Paulo", "2018-11-04")]
    [InlineData("America/Havana", "2020-11-01")]
    public void Exceptional_midnight_boundaries_match_native_local_dates_at_the_exact_tick(string zoneId, string day)
    {
        var date = Date(day);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var period = ReportingDates.Resolve(new(ReportingPreset.Custom, date, date),
            Guid.NewGuid(), "Synthetic owner", zoneId, Instant("2026-10-09T12:00:00Z"));
        var from = period.FromUtc!.Value;
        var to = period.ToUtc!.Value;

        // Native timezone datasets can put historical transitions at the last millisecond of a second.
        Assert.Equal(date, LocalDate(from));
        Assert.Equal(date.AddDays(-1), LocalDate(from.AddTicks(-1)));
        Assert.Equal(date.AddDays(1), LocalDate(to));
        Assert.Equal(date, LocalDate(to.AddTicks(-1)));

        DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
    }

    [Fact]
    public void Custom_ranges_support_either_open_endpoint_and_missing_timezone_uses_Copenhagen()
    {
        var owner = Guid.NewGuid();
        var now = Instant("2026-10-09T12:00:00Z");
        var lower = ReportingDates.Resolve(new(ReportingPreset.Custom, Date("2026-10-09")), owner, "Owner", null, now);
        var upper = ReportingDates.Resolve(new(ReportingPreset.Custom, To: Date("2026-10-09")), owner, "Owner", null, now);
        var all = ReportingDates.Resolve(new(ReportingPreset.AllTime), owner, "Owner", null, now);

        Assert.Equal(owner, lower.OwnerId);
        Assert.Equal("Owner", lower.OwnerName);
        Assert.Equal("Europe/Copenhagen", lower.TimeZoneId);
        Assert.Equal(Instant("2026-10-08T22:00:00Z"), lower.FromUtc);
        Assert.Null(lower.ToUtc);
        Assert.Null(upper.FromUtc);
        Assert.Equal(Instant("2026-10-09T22:00:00Z"), upper.ToUtc);
        Assert.Null(all.From);
        Assert.Null(all.To);
        Assert.Null(all.FromUtc);
        Assert.Null(all.ToUtc);
    }

    [Fact]
    public void Parsing_recognized_presets_ignores_explicit_dates_but_custom_preserves_them()
    {
        var preset = ReportingDates.Parse("today", "invalid", "9999-99-99");
        var custom = ReportingDates.Parse(null, "2024-02-29", "2024-03-01");
        var open = ReportingDates.Parse("custom", null, "2024-03-01");

        Assert.Equal(new ReportingDateSelection(ReportingPreset.Today), preset);
        Assert.Equal(new ReportingDateSelection(ReportingPreset.Custom, Date("2024-02-29"), Date("2024-03-01")), custom);
        Assert.Equal(new ReportingDateSelection(ReportingPreset.Custom, To: Date("2024-03-01")), open);
        Assert.Equal(ReportingPreset.AllTime, ReportingDates.Parse(null, null, null).Preset);
    }

    [Theory]
    [InlineData("not-a-period", null, null)]
    [InlineData("custom", "2023-02-29", null)]
    [InlineData(null, "2026-10-10", "2026-10-09")]
    [InlineData(null, "09/10/2026", null)]
    [InlineData(null, null, "not-a-date")]
    public void Invalid_URL_values_are_rejected_instead_of_becoming_unfiltered(
        string? preset, string? from, string? to) =>
        Assert.ThrowsAny<ArgumentException>(() => ReportingDates.Parse(preset, from, to));

    [Theory]
    [InlineData(ReportingPreset.Custom, "9999-12-31", "9999-12-31", "UTC", "2026-10-09T12:00:00Z")]
    [InlineData(ReportingPreset.Custom, "0001-01-01", null, "Europe/Copenhagen", "2026-10-09T12:00:00Z")]
    [InlineData(ReportingPreset.LastYear, null, null, "UTC", "0001-01-01T12:00:00Z")]
    [InlineData(ReportingPreset.LastMonth, null, null, "UTC", "0001-01-01T12:00:00Z")]
    [InlineData(ReportingPreset.ThisMonth, null, null, "UTC", "9999-12-01T12:00:00Z")]
    public void Unrepresentable_boundaries_are_rejected_without_overflow_or_clamping(
        ReportingPreset preset, string? from, string? to, string zone, string now) =>
        Assert.ThrowsAny<ArgumentException>(() => ReportingDates.Resolve(
            new(preset, from is null ? null : Date(from), to is null ? null : Date(to)),
            Guid.NewGuid(), "Synthetic owner", zone, Instant(now)));

    [Fact]
    public void Unavailable_timezone_is_actionable_even_for_all_time()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(ReportingPreset.AllTime, "2026-10-09T12:00:00Z", "Missing/Zone"));

        Assert.Contains("Profiles", exception.Message, StringComparison.Ordinal);
    }

    private static ResolvedOwnerPeriod Resolve(ReportingPreset preset, string now, string zone = "UTC") =>
        ReportingDates.Resolve(new(preset), Guid.NewGuid(), "Synthetic owner", zone, Instant(now));

    private static DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateTimeOffset Instant(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
