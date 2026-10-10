using System.Globalization;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Web.Services;

internal static class ExplorationQueryInput
{
    public static string View(string? value) => value switch
    {
        null or "" or "lines" => "lines",
        "frequency" or "exploration" => value,
        _ => throw new ArgumentException("Choose Lines, Frequency, or Exploration.")
    };

    public static bool Cumulative(string? value) => value switch
    {
        null or "" or "range" => false,
        "cumulative" => true,
        _ => throw new ArgumentException("Choose Range or Cumulative coverage.")
    };

    public static DateOnly? Date(string? value, string label, bool month = false)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (!DateOnly.TryParseExact(month ? value + "-01" : value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)) throw new ArgumentException($"{label} must be a valid {(month ? "year and month" : "date")}.");
        return date;
    }

    public static int Page(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 1;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page is > 0 and <= 1_000_000
            ? page : throw new ArgumentException("The requested page is invalid.");
    }

    public static int? Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var cell) && cell is >= 0 and < 268435456
            ? cell : throw new ArgumentException("The selected exploration cell is invalid.");
    }

    public static ExplorationQuery FromRequest(HttpRequest request, MapQuery map)
    {
        var view = View(request.Query["view"]);
        var cumulative = Cumulative(request.Query["coverage"]);
        var through = Date(request.Query["through"], "Through");
        return new(new(map.OwnerId, map.Sport),
            new(map.Period ?? (map.From.HasValue || map.To.HasValue ? ReportingPreset.Custom : ReportingPreset.AllTime), map.From, map.To), map.AsOfUtc ?? DateTimeOffset.UtcNow,
            view == "exploration" && cumulative, through);
    }
}
