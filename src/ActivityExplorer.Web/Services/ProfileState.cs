using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Web.Services;

public sealed class ProfileState(IProfileService profiles)
{
    private IReadOnlyList<ProfileSummary> _profiles = [];
    public IReadOnlyList<ProfileSummary> Profiles => _profiles;
    public Guid? SelectedOwnerId { get; private set; }
    public event Action? Changed;

    public async Task InitializeAsync()
    {
        _profiles = await profiles.ListAsync();
        if (SelectedOwnerId.HasValue && _profiles.All(x => x.Id != SelectedOwnerId)) SelectedOwnerId = null;
    }

    public async Task RefreshAsync()
    {
        await InitializeAsync();
        Changed?.Invoke();
    }

    public void Select(Guid? ownerId)
    {
        SelectedOwnerId = ownerId;
        Changed?.Invoke();
    }
}
