using System.Globalization;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Web.Services;

public sealed class ActivitySearchInput
{
    public string TextMode { get; set; } = "words";
    public bool DeviceExact { get; set; }
    public string Gear { get; set; } = "";
    public bool GearUnassigned { get; set; }
    public string Indoor { get; set; } = "";
    public string Gps { get; set; } = "";
    public string MinDistance { get; set; } = "";
    public string MaxDistance { get; set; } = "";
    public string MinMoving { get; set; } = "";
    public string MaxMoving { get; set; } = "";
    public string MinAscent { get; set; } = "";
    public string MaxAscent { get; set; } = "";
    public string TagMode { get; set; } = "any";
    public string TagPresence { get; set; } = "any";

    public ActivityCriteria Build(SportKind? sport, string? search, bool? power, string? device,
        IReadOnlyList<Guid> tags, IReadOnlyList<Guid> excluded) => ActivityCriteriaValidation.Normalize(new(
        sport, search, power, device, tags, Mode<ActivityTextMode>(TextMode), DeviceExact,
        Gear, GearUnassigned, Boolean(Indoor), Boolean(Gps),
        Number(MinDistance, 1000, "Minimum distance"), Number(MaxDistance, 1000, "Maximum distance"),
        Number(MinMoving, 60, "Minimum moving time"), Number(MaxMoving, 60, "Maximum moving time"),
        Number(MinAscent, 1, "Minimum ascent"), Number(MaxAscent, 1, "Maximum ascent"),
        Mode<ActivityTagMode>(TagMode), excluded, Mode<ActivityTagPresence>(TagPresence)));

    public static ActivitySearchInput FromCriteria(ActivityCriteria criteria) => new()
    {
        TextMode = criteria.TextMode.ToString().ToLowerInvariant(),
        DeviceExact = criteria.DeviceExact,
        Gear = criteria.Gear ?? "",
        GearUnassigned = criteria.GearUnassigned,
        Indoor = BooleanText(criteria.IsIndoor),
        Gps = BooleanText(criteria.HasGps),
        MinDistance = DisplayNumber(criteria.MinDistanceMeters, 1000),
        MaxDistance = DisplayNumber(criteria.MaxDistanceMeters, 1000),
        MinMoving = DisplayNumber(criteria.MinMovingSeconds, 60),
        MaxMoving = DisplayNumber(criteria.MaxMovingSeconds, 60),
        MinAscent = DisplayNumber(criteria.MinAscentMeters, 1),
        MaxAscent = DisplayNumber(criteria.MaxAscentMeters, 1),
        TagMode = criteria.TagMode.ToString().ToLowerInvariant(),
        TagPresence = criteria.TagPresence.ToString().ToLowerInvariant()
    };

    public static string DisplayRaw(string? raw, double divisor) => string.IsNullOrWhiteSpace(raw) ? "" :
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? DisplayNumber(value, divisor) : raw;

    public static IEnumerable<KeyValuePair<string, string>> QueryValues(ActivityCriteria criteria)
    {
        var values = new Dictionary<string, string>();
        Add("q", criteria.Search); Add("sport", criteria.Sport?.ToString().ToLowerInvariant());
        Add("power", BooleanText(criteria.HasPower)); Add("device", criteria.Device);
        if (criteria.TextMode == ActivityTextMode.Words && !string.IsNullOrEmpty(criteria.Search)) Add("textMode", "words");
        if (criteria.DeviceExact) Add("deviceExact", "yes");
        Add("gear", criteria.Gear); if (criteria.GearUnassigned) Add("gearUnassigned", "yes");
        Add("indoor", BooleanText(criteria.IsIndoor)); Add("gps", BooleanText(criteria.HasGps));
        Add("minDistance", DisplayNumber(criteria.MinDistanceMeters, 1)); Add("maxDistance", DisplayNumber(criteria.MaxDistanceMeters, 1));
        Add("minMoving", DisplayNumber(criteria.MinMovingSeconds, 1)); Add("maxMoving", DisplayNumber(criteria.MaxMovingSeconds, 1));
        Add("minAscent", DisplayNumber(criteria.MinAscentMeters, 1)); Add("maxAscent", DisplayNumber(criteria.MaxAscentMeters, 1));
        if (criteria.TagIds is { Count: > 0 }) Add("tags", string.Join(',', criteria.TagIds.Order()));
        if (criteria.ExcludedTagIds is { Count: > 0 }) Add("excludeTags", string.Join(',', criteria.ExcludedTagIds.Order()));
        if (criteria.TagMode != ActivityTagMode.Any) Add("tagMode", "all");
        if (criteria.TagPresence != ActivityTagPresence.Any) Add("tagPresence", criteria.TagPresence.ToString().ToLowerInvariant());
        return values;

        void Add(string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) values[name] = value; }
    }

    private static double? Number(string text, double multiplier, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value * multiplier))
            throw new ArgumentException($"{label} must be a finite number.");
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var precise) &&
            precise >= decimal.MinValue / (decimal)multiplier && precise <= decimal.MaxValue / (decimal)multiplier)
            return double.Parse((precise * (decimal)multiplier).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return value * multiplier;
    }

    private static T Mode<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var mode) && Enum.IsDefined(mode) ? mode : throw new ArgumentException("Choose a valid matching mode.");

    private static bool? Boolean(string value) => value switch
    {
        "" => null,
        "yes" => true,
        "no" => false,
        _ => throw new ArgumentException("Choose a valid recording filter.")
    };

    private static string BooleanText(bool? value) => value switch { true => "yes", false => "no", _ => "" };
    private static string DisplayNumber(double? value, double divisor)
    {
        if (!value.HasValue) return "";
        return decimal.TryParse(value.Value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var precise)
            ? (precise / (decimal)divisor).ToString("G", CultureInfo.InvariantCulture)
            : (value.Value / divisor).ToString("R", CultureInfo.InvariantCulture);
    }
}
