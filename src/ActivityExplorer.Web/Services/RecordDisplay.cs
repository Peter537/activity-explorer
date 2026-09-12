using ActivityExplorer.Core.Domain;
using ActivityExplorer.Web.Components.Shared;

namespace ActivityExplorer.Web.Services;

internal static class RecordDisplay
{
    public static string Value(SportKind sport, RecordKind kind, double value) => kind switch
    {
        RecordKind.Distance or RecordKind.TimedDistanceEffort => Format.Distance(value),
        RecordKind.Duration or RecordKind.DistanceEffort => Format.Duration(value),
        RecordKind.Elevation => $"{value:N0} m",
        RecordKind.AverageSpeed => sport == SportKind.Rowing ? Format.Speed(value, sport) : $"{value * 3.6:N1} km/h",
        RecordKind.PowerCurve => $"{value:N0} W",
        _ => $"{value:N1}"
    };

    public static string ScopeLabel(RecordScope scope) => scope switch
    {
        RecordScope.Outdoor => "Outdoor only",
        RecordScope.Indoor => "Indoor only",
        _ => "All training"
    };

    public static T? Parse<T>(string? text) where T : struct, Enum =>
        Enum.TryParse<T>(text, true, out var value) && Enum.IsDefined(value) &&
        string.Equals(value.ToString(), text, StringComparison.OrdinalIgnoreCase) ? value : null;

    public static string AttemptsUrl(SportKind sport, RecordKind kind, string key, RecordScope scope,
        Guid? owner, bool multiple = false, int page = 1)
    {
        var url = $"/records/attempts?sport={sport.ToString().ToLowerInvariant()}&kind={kind.ToString().ToLowerInvariant()}&key={Uri.EscapeDataString(key)}";
        if (scope != RecordScope.All) url += $"&scope={scope.ToString().ToLowerInvariant()}";
        if (owner.HasValue) url += $"&owner={owner}";
        if (multiple) url += "&mode=multiple";
        if (page > 1) url += $"&page={page}";
        return url;
    }
}
