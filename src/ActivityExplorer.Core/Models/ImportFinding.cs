using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Core.Models;

public sealed record ImportHistoryFilter(Guid? OwnerId = null, string? Search = null,
    IReadOnlyList<ImportStatus>? Statuses = null, bool? HasWarnings = null, SourceKind? Source = null,
    ImportBatchKind? Kind = null, DateOnly? From = null, DateOnly? To = null);

public static class ImportHistoryFiltering
{
    public static ImportHistoryFilter Normalize(ImportHistoryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var statuses = filter.Statuses?.Distinct().Order().ToArray() ?? [];
        if (statuses.Any(status => !Enum.IsDefined(status)))
            throw new ArgumentException("Choose valid import statuses.", nameof(filter));
        if (filter.Source.HasValue && !Enum.IsDefined(filter.Source.Value))
            throw new ArgumentException("Choose a valid import source.", nameof(filter));
        if (filter.Kind.HasValue && !Enum.IsDefined(filter.Kind.Value))
            throw new ArgumentException("Choose a valid import type.", nameof(filter));
        ReportingDates.Validate(new(ReportingPreset.Custom, filter.From, filter.To));
        return filter with { Search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim(), Statuses = statuses };
    }
}
