using System.Text.Json;
using System.Text.Json.Serialization;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Infrastructure.Services;

internal static class SavedSearchCriteriaJson
{
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static SavedSearchCriteria? TryRead(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("Version", out var version) || !version.TryGetInt32(out var number)) return null;
            var criteria = number switch
            {
                1 or 2 => JsonSerializer.Deserialize<SavedSearchCriteria>(json, Options),
                _ => null
            };
            return criteria is null ? null : Validate(criteria);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    public static string Write(SavedSearchCriteria criteria) => criteria.Version == 1
        ? JsonSerializer.Serialize(new LegacyCriteria(criteria.Version, criteria.Sport, criteria.Search, criteria.HasPower,
            criteria.Device, criteria.Tags, criteria.Sort, criteria.Dates), Options)
        : JsonSerializer.Serialize(criteria, Options);

    public static IEnumerable<SavedTagReference> References(SavedSearchCriteria criteria) =>
        criteria.Tags.Concat(criteria.ExcludedTags ?? []);

    public static SavedSearchCriteria Validate(SavedSearchCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Version is not (1 or 2)) throw new ArgumentException("This saved search version is unsupported.", nameof(criteria));
        if (criteria.Version == 1 && (criteria.TextMode != ActivityTextMode.Literal || criteria.DeviceExact ||
            criteria.Gear is not null || criteria.GearUnassigned || criteria.IsIndoor.HasValue || criteria.HasGps.HasValue ||
            criteria.MinDistanceMeters.HasValue || criteria.MaxDistanceMeters.HasValue ||
            criteria.MinMovingSeconds.HasValue || criteria.MaxMovingSeconds.HasValue ||
            criteria.MinAscentMeters.HasValue || criteria.MaxAscentMeters.HasValue || criteria.TagMode != ActivityTagMode.Any ||
            criteria.ExcludedTags is { Count: > 0 } || criteria.TagPresence != ActivityTagPresence.Any))
            throw new ArgumentException("Use saved search version 2 for the extended filters.", nameof(criteria));
        if (criteria.Sort is not ("start-desc" or "start-asc" or "distance-desc" or "distance-asc" or "duration-desc" or "elevation-desc"))
            throw new ArgumentException("Choose a valid activity sort.", nameof(criteria));
        ArgumentNullException.ThrowIfNull(criteria.Dates);
        ReportingDates.Validate(criteria.Dates);
        ArgumentNullException.ThrowIfNull(criteria.Tags);
        if (References(criteria).Any(x => x is null || x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.Name) || x.Name.Trim().Length > 80))
            throw new ArgumentException("The saved tags are invalid.", nameof(criteria));
        ActivityCriteriaValidation.Normalize(criteria.ToActivityCriteria());
        return criteria with
        {
            Search = Clean(criteria.Search),
            Device = Clean(criteria.Device),
            Gear = Clean(criteria.Gear),
            Tags = criteria.Tags.DistinctBy(x => x.Id).Select(x => x with { Name = x.Name.Trim() }).ToArray(),
            ExcludedTags = (criteria.ExcludedTags ?? []).DistinctBy(x => x.Id).Select(x => x with { Name = x.Name.Trim() }).ToArray(),
            Dates = criteria.Dates.Preset == ReportingPreset.Custom ? criteria.Dates : new ReportingDateSelection(criteria.Dates.Preset)
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record LegacyCriteria(
        int Version, SportKind? Sport, string? Search, bool? HasPower, string? Device,
        IReadOnlyList<SavedTagReference> Tags, string Sort, ReportingDateSelection Dates);
}
