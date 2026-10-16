namespace ActivityExplorer.Core.Models;

public sealed record SegmentLibraryMatches(IReadOnlySet<Guid> MatchingIds, IReadOnlySet<Guid> VisibleIds);

public static class SegmentLibraryFinding
{
    public static SegmentLibraryMatches Find(IReadOnlyList<SegmentSummary> segments,
        IReadOnlyList<SegmentRelationship> relationships, string? name, Domain.SportKind? sport)
    {
        var term = name?.Trim();
        var matching = segments.Where(segment => (!sport.HasValue || segment.Sport == sport) &&
            (string.IsNullOrEmpty(term) || segment.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(segment => segment.Id).ToHashSet();
        var known = segments.Select(segment => segment.Id).ToHashSet();
        var parents = relationships.Where(link => known.Contains(link.ParentSegmentId) && known.Contains(link.ChildSegmentId))
            .ToLookup(link => link.ChildSegmentId, link => link.ParentSegmentId);
        var visible = new HashSet<Guid>(matching);
        var pending = new Stack<Guid>(matching);
        while (pending.TryPop(out var child))
            foreach (var parent in parents[child])
                if (visible.Add(parent)) pending.Push(parent);
        return new(matching, visible);
    }
}

public sealed record SegmentEffortFilter(DateOnly? From = null, DateOnly? To = null,
    double? MinimumElapsedSeconds = null, double? MaximumElapsedSeconds = null, string Sort = "rank");

public static class SegmentEffortFinding
{
    public static void Validate(SegmentEffortFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.From.HasValue && filter.To.HasValue && filter.From > filter.To)
            throw new ArgumentException("Efforts from cannot be later than Efforts to.", nameof(filter));
        if ((filter.MinimumElapsedSeconds is { } minimum && (!double.IsFinite(minimum) || minimum < 0)) ||
            (filter.MaximumElapsedSeconds is { } maximum && (!double.IsFinite(maximum) || maximum < 0)))
            throw new ArgumentException("Elapsed bounds must be finite, non-negative seconds.", nameof(filter));
        if (filter.MinimumElapsedSeconds.HasValue && filter.MaximumElapsedSeconds.HasValue &&
            filter.MinimumElapsedSeconds > filter.MaximumElapsedSeconds)
            throw new ArgumentException("Minimum elapsed time cannot exceed maximum elapsed time.", nameof(filter));
        if (filter.Sort is not ("rank" or "date" or "elapsed" or "speed" or "heart-rate" or "power"))
            throw new ArgumentException("Choose a valid effort sort.", nameof(filter));
    }

    public static IReadOnlyList<SegmentEffortSummary> Apply(IReadOnlyList<SegmentEffortSummary> efforts,
        SegmentEffortFilter filter, string? ownerTimeZoneId)
    {
        Validate(filter);
        var zone = ReportingTimeZone.Resolve(ownerTimeZoneId);
        var source = efforts.Where(effort =>
        {
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(effort.StartTime, zone).DateTime);
            return (!filter.From.HasValue || localDate >= filter.From) && (!filter.To.HasValue || localDate <= filter.To) &&
                (!filter.MinimumElapsedSeconds.HasValue || effort.ElapsedSeconds >= filter.MinimumElapsedSeconds) &&
                (!filter.MaximumElapsedSeconds.HasValue || effort.ElapsedSeconds <= filter.MaximumElapsedSeconds);
        });
        var ordered = filter.Sort switch
        {
            "date" => source.OrderByDescending(effort => effort.StartTime),
            "elapsed" => source.OrderBy(effort => effort.ElapsedSeconds),
            "speed" => source.OrderByDescending(effort => effort.AverageSpeed ?? double.NegativeInfinity),
            "heart-rate" => source.OrderByDescending(effort => effort.AverageHeartRate ?? double.NegativeInfinity),
            "power" => source.OrderByDescending(effort => effort.AveragePower ?? double.NegativeInfinity),
            _ => source.OrderBy(effort => effort.Rank)
        };
        return ordered.ThenBy(effort => effort.StartTime).ThenBy(effort => effort.ActivityId)
            .ThenBy(effort => effort.StartPointIndex).ThenBy(effort => effort.Id).ToArray();
    }
}
