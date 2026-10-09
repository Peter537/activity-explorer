using System.Globalization;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Web.Services;

internal static class ActivityRangeUrl
{
    public static string Build(Guid activityId, ActivityRange? range, string? fingerprint,
        ChartAxisKind axis = ChartAxisKind.ElapsedTime, int? section = null, string? fragment = null)
    {
        var parameters = new List<string>();
        if (range is not null && fingerprint is not null)
        {
            parameters.Add("range=" + Uri.EscapeDataString(Serialize(range)));
            parameters.Add("stream=" + Uri.EscapeDataString(fingerprint));
        }
        if (axis == ChartAxisKind.Distance) parameters.Add("axis=distance");
        if (section.HasValue) parameters.Add("section=" + section.Value.ToString(CultureInfo.InvariantCulture));
        return $"/activities/{activityId}" + (parameters.Count > 0 ? "?" + string.Join('&', parameters) : "") +
            (fragment is null ? "" : "#" + fragment);
    }

    public static string ForAttempt(RecordAttempt attempt) => Build(attempt.ActivityId, attempt.Range,
        attempt.StreamFingerprint, fragment: attempt.Range is null ? null : "activity-streams");

    public static string Serialize(ActivityRange range) => "1," + Boundary(range.Start) + "," + Boundary(range.End);

    public static bool TryParse(string? text, out ActivityRange? range)
    {
        range = null;
        if (string.IsNullOrEmpty(text) || text.Length > 512) return false;
        var fields = text.Split(',');
        if (fields.Length != 9 || fields[0] != "1" ||
            !TryBoundary(fields.AsSpan(1, 4), out var start) || !TryBoundary(fields.AsSpan(5, 4), out var end)) return false;
        range = new ActivityRange(start!, end!);
        return true;
    }

    private static string Boundary(SourceBoundary boundary) => string.Join(',',
        boundary.LowerIndex.ToString(CultureInfo.InvariantCulture),
        boundary.UpperIndex.ToString(CultureInfo.InvariantCulture),
        boundary.Fraction.ToString("R", CultureInfo.InvariantCulture),
        boundary.Timestamp?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "-");

    private static bool TryBoundary(ReadOnlySpan<string> fields, out SourceBoundary? boundary)
    {
        boundary = null;
        if (!int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var lower) ||
            !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var upper) ||
            !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) ||
            !double.IsFinite(fraction) || fraction is < 0 or > 1) return false;
        DateTimeOffset? timestamp = null;
        if (fields[3] != "-")
        {
            if (!long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks) return false;
            timestamp = new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        boundary = new SourceBoundary(lower, upper, fraction, timestamp);
        return true;
    }
}
