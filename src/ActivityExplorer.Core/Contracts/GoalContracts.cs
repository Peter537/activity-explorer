using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Core.Contracts;

public interface IGoalService
{
    Task<IReadOnlyList<GoalSummary>> ListAsync(Guid? ownerId = null,
        GoalListView view = GoalListView.CurrentAndUpcoming, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GoalSummary>> SearchAsync(GoalListFilter filter, CancellationToken cancellationToken = default);
    Task<GoalEditionLocation?> FindEditionAsync(Guid goalId, DateOnly editionDate, CancellationToken cancellationToken = default);
    Task<GoalDetail?> GetDetailAsync(Guid goalId, DateOnly? edition = null,
        int historyPage = 1, int contributionPage = 1, CancellationToken cancellationToken = default);
    Task ValidateCreateAsync(CreateGoalRequest request, CancellationToken cancellationToken = default);
    Task ValidateEditAsync(EditGoalRequest request, CancellationToken cancellationToken = default);
    Task ValidateArchiveAsync(ArchiveGoalRequest request, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(CreateGoalRequest request, CancellationToken cancellationToken = default);
    Task EditAsync(EditGoalRequest request, CancellationToken cancellationToken = default);
    Task ArchiveAsync(ArchiveGoalRequest request, CancellationToken cancellationToken = default);
}
