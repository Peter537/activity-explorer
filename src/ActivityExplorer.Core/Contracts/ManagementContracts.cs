using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Core.Contracts;

public interface IProfileService
{
    Task<IReadOnlyList<ProfileSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(string displayName, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid ownerId, string confirmation, CancellationToken cancellationToken = default);
    Task<ProfileExport> ExportAsync(Guid ownerId, CancellationToken cancellationToken = default);
}

public interface IImportHistoryService
{
    Task<ImportHistoryPage> ListAsync(Guid? ownerId, int limit = 10, CancellationToken cancellationToken = default);
    Task<ImportReport?> GetReportAsync(Guid importId, CancellationToken cancellationToken = default);
}

public interface IWatchedFolderService
{
    Task<IReadOnlyList<WatchedFolderSummary>> ListAsync(Guid? ownerId, CancellationToken cancellationToken = default);
    Task<Guid> AddAsync(Guid ownerId, string path, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}
