using System.Text;
using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Core.Models;

public enum ActivityTextMode { Literal, Words }
public enum ActivityTagMode { Any, All }
public enum ActivityTagPresence { Any, Tagged, Untagged }

/// <summary>Activity membership criteria, independent of profile, dates, ordering, and pagination.</summary>
public sealed record ActivityCriteria(
    SportKind? Sport = null, string? Search = null, bool? HasPower = null, string? Device = null,
    IReadOnlyList<Guid>? TagIds = null, ActivityTextMode TextMode = ActivityTextMode.Literal,
    bool DeviceExact = false, string? Gear = null, bool GearUnassigned = false,
    bool? IsIndoor = null, bool? HasGps = null,
    double? MinDistanceMeters = null, double? MaxDistanceMeters = null,
    double? MinMovingSeconds = null, double? MaxMovingSeconds = null,
    double? MinAscentMeters = null, double? MaxAscentMeters = null,
    ActivityTagMode TagMode = ActivityTagMode.Any, IReadOnlyList<Guid>? ExcludedTagIds = null,
    ActivityTagPresence TagPresence = ActivityTagPresence.Any);

public static class ActivityCriteriaValidation
{
    public static ActivityCriteria Normalize(ActivityCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Sport.HasValue && !Enum.IsDefined(criteria.Sport.Value))
            throw new ArgumentException("Choose a valid sport.", nameof(criteria));
        if (!Enum.IsDefined(criteria.TextMode) || !Enum.IsDefined(criteria.TagMode) || !Enum.IsDefined(criteria.TagPresence))
            throw new ArgumentException("Choose valid text and tag matching modes.", nameof(criteria));
        ValidateRange(criteria.MinDistanceMeters, criteria.MaxDistanceMeters, "Distance");
        ValidateRange(criteria.MinMovingSeconds, criteria.MaxMovingSeconds, "Moving time");
        ValidateRange(criteria.MinAscentMeters, criteria.MaxAscentMeters, "Ascent");
        var included = NormalizeIds(criteria.TagIds);
        var excluded = NormalizeIds(criteria.ExcludedTagIds);
        if (included.Intersect(excluded).Any())
            throw new ArgumentException("A tag cannot be both included and excluded.", nameof(criteria));
        if (criteria.TagPresence == ActivityTagPresence.Untagged && included.Length > 0)
            throw new ArgumentException("Untagged activities cannot also require included tags.", nameof(criteria));
        var gear = Clean(criteria.Gear);
        if (criteria.GearUnassigned && gear is not null)
            throw new ArgumentException("Choose either a named gear item or unassigned gear.", nameof(criteria));
        var search = Clean(criteria.Search);
        if (criteria.TextMode == ActivityTextMode.Words && search is not null) ParseTextTerms(search);
        return criteria with
        {
            Search = search,
            Device = Clean(criteria.Device),
            Gear = gear,
            TagIds = included,
            ExcludedTagIds = excluded
        };
    }

    /// <summary>All terms must match; quoted phrases retain spaces and match within a single field.</summary>
    public static IReadOnlyList<string> ParseTextTerms(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return [];
        var terms = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in search.Trim())
        {
            if (character == '"')
            {
                AddTerm();
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(character) && !quoted) AddTerm();
            else current.Append(character);
        }
        if (quoted) throw new ArgumentException("Close the quoted phrase in the activity search.", nameof(search));
        AddTerm();
        if (terms.Count == 0) throw new ArgumentException("Enter at least one search word or phrase.", nameof(search));
        return terms.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        void AddTerm()
        {
            var term = current.ToString().Trim();
            if (term.Length > 0) terms.Add(term);
            current.Clear();
        }
    }

    private static Guid[] NormalizeIds(IReadOnlyList<Guid>? ids)
    {
        if (ids?.Contains(Guid.Empty) == true)
            throw new ArgumentException("Choose valid tags.", nameof(ids));
        return ids?.Distinct().Order().ToArray() ?? [];
    }

    private static void ValidateRange(double? minimum, double? maximum, string label)
    {
        if ((minimum.HasValue && (!double.IsFinite(minimum.Value) || minimum < 0)) ||
            (maximum.HasValue && (!double.IsFinite(maximum.Value) || maximum < 0)))
            throw new ArgumentException($"{label} bounds must be finite, non-negative numbers.");
        if (minimum.HasValue && maximum.HasValue && minimum > maximum)
            throw new ArgumentException($"{label} minimum cannot exceed its maximum.");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
