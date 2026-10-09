using System.Globalization;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Tests;

public sealed class ReportingProgressTests
{
    [Theory]
    [InlineData("2025-03-01", "2025-03-31", PeriodComparison.PreviousPeriod, "2025-01-29", "2025-02-28")]
    [InlineData("2024-03-01", "2024-03-31", PeriodComparison.PreviousPeriod, "2024-01-30", "2024-02-29")]
    [InlineData("2024-02-29", "2024-02-29", PeriodComparison.PreviousPeriod, "2024-02-28", "2024-02-28")]
    [InlineData("2024-01-01", "2024-02-29", PeriodComparison.PriorYear, "2023-01-01", "2023-02-28")]
    [InlineData("2025-01-01", "2025-03-04", PeriodComparison.PriorYear, "2024-01-01", "2024-03-04")]
    [InlineData("2024-02-29", "2024-03-01", PeriodComparison.PriorYear, "2023-02-28", "2023-03-01")]
    public void Comparisons_use_inclusive_calendar_lengths_or_clamped_corresponding_dates(
        string from, string to, PeriodComparison mode, string expectedFrom, string expectedTo)
    {
        var result = ReportingProgress.ComparisonDates(Date(from), Date(to), mode);

        Assert.Equal((Date(expectedFrom), Date(expectedTo)), result);
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(50, 0, null)]
    [InlineData(0, 20, -100d)]
    [InlineData(30, 20, 50d)]
    [InlineData(10, 20, -50d)]
    public void Zero_baselines_have_no_percentage_and_other_changes_keep_their_sign(double current, double baseline, double? expected) =>
        Assert.Equal(expected, ReportingProgress.PercentageChange(current, baseline));

    [Fact]
    public void Comparison_boundaries_reject_unsupported_ranges() =>
        Assert.Throws<ArgumentException>(() => ReportingProgress.ComparisonDates(new(1, 1, 1), new(1, 1, 10), PeriodComparison.PreviousPeriod));

    private static DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
