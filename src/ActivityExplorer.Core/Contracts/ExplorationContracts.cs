using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Core.Contracts;

public interface IExplorationIndexService
{
    Task<ExplorationIndexStatus> GetStatusAsync(ExplorationScope scope, CancellationToken cancellationToken = default);
    Task<ExplorationIndexStatus> BuildAsync(ExplorationScope scope, IProgress<ExplorationBuildProgress>? progress = null,
        bool recheckLimited = false, CancellationToken cancellationToken = default);
}

public interface IExplorationQueryService
{
    Task<ExplorationResult> GetAsync(ExplorationQuery query, CancellationToken cancellationToken = default);
    Task<ExplorationViewport> GetViewportAsync(ExplorationQuery query, MapQuery viewport, CancellationToken cancellationToken = default);
    Task<ExplorationCellDetail?> GetCellAsync(ExplorationQuery query, int cellId, int page = 1,
        CancellationToken cancellationToken = default);
}
