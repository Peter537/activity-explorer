using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Core.Models;

public sealed record ProfileSummary(Guid Id, string DisplayName, int ActivityCount, double DistanceMeters, DateTimeOffset CreatedAt);
public sealed record ProfileExport(string FileName, string Json);
public sealed record ImportBatchSummary(
    Guid Id, Guid OwnerId, string OwnerName, string DisplayName, SourceKind SourceKind, ImportBatchKind Kind, ImportStatus Status,
    int FilesDiscovered, int Created, int Updated, int Skipped, int Warnings, DateTimeOffset CreatedAt, string? Summary);
public sealed record ImportHistoryPage(IReadOnlyList<ImportBatchSummary> Items, int Total);
public sealed record WatchedFolderSummary(Guid Id, Guid OwnerId, string OwnerName, string Path, bool Enabled, DateTimeOffset? LastScanAt);
