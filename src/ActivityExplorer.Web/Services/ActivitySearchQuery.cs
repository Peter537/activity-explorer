using System.Globalization;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace ActivityExplorer.Web.Services;

public sealed record ActivitySearchSelection(ActivityCriteria Criteria, Guid? TagOwner);

public static class ActivitySearchQuery
{
    private static readonly HashSet<string> CriteriaKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "q", "sport", "power", "device", "textMode", "deviceExact", "gear", "gearUnassigned", "indoor", "gps",
        "minDistance", "maxDistance", "minMoving", "maxMoving", "minAscent", "maxAscent", "tags", "excludeTags",
        "tagMode", "tagPresence", "tagOwner"
    };

    public static ActivitySearchSelection Read(string uri, SportKind? sport = null)
    {
        var query = Uri.TryCreate(uri, UriKind.Absolute, out var absolute) ? absolute.Query :
            uri.Contains('?', StringComparison.Ordinal) ? uri[uri.IndexOf('?', StringComparison.Ordinal)..].Split('#')[0] : "";
        return Parse(QueryHelpers.ParseQuery(query).Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value.ToString())), sport);
    }

    public static ActivitySearchSelection Parse(IEnumerable<KeyValuePair<string, string?>> values, SportKind? sport = null)
    {
        var query = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        string? Get(string name) => query.GetValueOrDefault(name);
        if (!sport.HasValue && !string.IsNullOrWhiteSpace(Get("sport")))
        {
            if (!Enum.TryParse<SportKind>(Get("sport"), true, out var parsed) || !Enum.IsDefined(parsed))
                throw new ArgumentException("Choose a valid sport.");
            sport = parsed;
        }
        var included = Tags(Get("tags"));
        var excluded = Tags(Get("excludeTags"));
        Guid? tagOwner = null;
        if (included.Count + excluded.Count > 0)
        {
            if (!Guid.TryParse(Get("tagOwner"), out var owner) || owner == Guid.Empty)
                throw new ArgumentException("The tag filter link is invalid. Reopen a saved search or clear the activity search.");
            tagOwner = owner;
        }
        var input = new ActivitySearchInput
        {
            TextMode = Get("textMode") ?? (string.IsNullOrWhiteSpace(Get("q")) ? "words" : "literal"),
            DeviceExact = Flag(Get("deviceExact")),
            GearUnassigned = Flag(Get("gearUnassigned")),
            Gear = Get("gear") ?? "",
            Indoor = Get("indoor") ?? "",
            Gps = Get("gps") ?? "",
            MinDistance = ActivitySearchInput.DisplayRaw(Get("minDistance"), 1000),
            MaxDistance = ActivitySearchInput.DisplayRaw(Get("maxDistance"), 1000),
            MinMoving = ActivitySearchInput.DisplayRaw(Get("minMoving"), 60),
            MaxMoving = ActivitySearchInput.DisplayRaw(Get("maxMoving"), 60),
            MinAscent = Get("minAscent") ?? "",
            MaxAscent = Get("maxAscent") ?? "",
            TagMode = Get("tagMode") ?? "any",
            TagPresence = Get("tagPresence") ?? "any"
        };
        var power = Get("power") switch
        {
            null or "" => (bool?)null,
            "yes" => true,
            "no" => false,
            _ => throw new ArgumentException("Choose a valid power recording filter.")
        };
        return new(input.Build(sport, Get("q"), power, Get("device"), included, excluded), tagOwner);
    }

    public static string Build(string path, ActivityCriteria criteria, Guid? ownerId,
        IReadOnlyDictionary<string, string?>? extraQueryValues = null)
    {
        if (HasTags(criteria) && (!ownerId.HasValue || ownerId == Guid.Empty))
            throw new ArgumentException("Select one profile for an activity search containing tags.", nameof(ownerId));
        var values = extraQueryValues is null ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) :
            new Dictionary<string, string?>(extraQueryValues, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in ActivitySearchInput.QueryValues(ActivityCriteriaValidation.Normalize(criteria))) values[pair.Key] = pair.Value;
        if (HasTags(criteria)) values["tagOwner"] = ownerId?.ToString();
        return QueryHelpers.AddQueryString(path, values);
    }

    public static string WithCriteria(string uri, ActivityCriteria criteria, Guid? ownerId)
    {
        var fragmentIndex = uri.IndexOf('#', StringComparison.Ordinal);
        var fragment = fragmentIndex < 0 ? "" : uri[fragmentIndex..];
        var unfragmented = fragmentIndex < 0 ? uri : uri[..fragmentIndex];
        var queryIndex = unfragmented.IndexOf('?', StringComparison.Ordinal);
        var path = queryIndex < 0 ? unfragmented : unfragmented[..queryIndex];
        var values = QueryHelpers.ParseQuery(queryIndex < 0 ? "" : unfragmented[queryIndex..])
            .Where(pair => !CriteriaKeys.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        return Build(path, criteria, ownerId, values) + fragment;
    }

    public static bool HasNonSportCriteria(ActivityCriteria criteria) =>
        !string.IsNullOrWhiteSpace(criteria.Search) || criteria.HasPower.HasValue || !string.IsNullOrWhiteSpace(criteria.Device) ||
        !string.IsNullOrWhiteSpace(criteria.Gear) || criteria.GearUnassigned || criteria.IsIndoor.HasValue || criteria.HasGps.HasValue ||
        criteria.MinDistanceMeters.HasValue || criteria.MaxDistanceMeters.HasValue || criteria.MinMovingSeconds.HasValue ||
        criteria.MaxMovingSeconds.HasValue || criteria.MinAscentMeters.HasValue || criteria.MaxAscentMeters.HasValue ||
        HasTags(criteria) || criteria.TagPresence != ActivityTagPresence.Any;

    public static string Describe(ActivityCriteria criteria)
    {
        var parts = new List<string>();
        if (criteria.Search is { } search) parts.Add($"Text: {search} ({(criteria.TextMode == ActivityTextMode.Words ? "all words / phrases" : "literal")})");
        if (criteria.HasPower is { } power) parts.Add(power ? "Has power" : "No power");
        if (criteria.Device is { } device) parts.Add($"Device: {device}{(criteria.DeviceExact ? " (exact)" : "")}");
        if (criteria.Gear is { } gear) parts.Add($"Gear: {gear}");
        if (criteria.GearUnassigned) parts.Add("Unassigned gear");
        if (criteria.IsIndoor is { } indoor) parts.Add(indoor ? "Indoor" : "Outdoor");
        if (criteria.HasGps is { } gps) parts.Add(gps ? "Has GPS" : "No GPS");
        Range("Distance", criteria.MinDistanceMeters, criteria.MaxDistanceMeters, 1000, "km");
        Range("Moving time", criteria.MinMovingSeconds, criteria.MaxMovingSeconds, 60, "min");
        Range("Ascent", criteria.MinAscentMeters, criteria.MaxAscentMeters, 1, "m");
        if (criteria.TagIds is { Count: > 0 } tags) parts.Add($"{tags.Count} included tags ({criteria.TagMode.ToString().ToLowerInvariant()})");
        if (criteria.ExcludedTagIds is { Count: > 0 } excluded) parts.Add($"{excluded.Count} excluded tags");
        if (criteria.TagPresence != ActivityTagPresence.Any) parts.Add(criteria.TagPresence.ToString());
        return string.Join(" · ", parts);

        void Range(string label, double? minimum, double? maximum, double divisor, string unit)
        {
            if (!minimum.HasValue && !maximum.HasValue) return;
            string Number(double? value) => value.HasValue ? (value.Value / divisor).ToString("0.###", CultureInfo.CurrentCulture) : "any";
            parts.Add($"{label}: {Number(minimum)}–{Number(maximum)} {unit}");
        }
    }

    private static bool HasTags(ActivityCriteria criteria) => (criteria.TagIds?.Count ?? 0) + (criteria.ExcludedTagIds?.Count ?? 0) > 0;
    private static bool Flag(string? value) => value switch
    {
        null or "" or "no" => false,
        "yes" => true,
        _ => throw new ArgumentException("Choose a valid equipment filter.")
    };
    private static IReadOnlyList<Guid> Tags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Any(part => !Guid.TryParse(part, out var id) || id == Guid.Empty))
            throw new ArgumentException("The activity search contains invalid tag references.");
        return parts.Select(Guid.Parse).Distinct().Order().ToArray();
    }
}
