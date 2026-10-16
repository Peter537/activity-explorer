using Microsoft.AspNetCore.WebUtilities;

namespace ActivityExplorer.Web.Services;

public sealed record ImportLinkScope(Guid BatchId, Guid OwnerId)
{
    public static ImportLinkScope? Parse(string? batch, string? owner)
    {
        if (batch is null && owner is null) return null;
        if (!Guid.TryParse(batch, out var batchId) || batchId == Guid.Empty ||
            !Guid.TryParse(owner, out var ownerId) || ownerId == Guid.Empty)
            throw new ArgumentException("The linked import filter is invalid. Remove the import filter and reopen import history.");
        return new(batchId, ownerId);
    }

    public void ValidateOwner(Guid? selectedOwner)
    {
        if (OwnerId != selectedOwner)
            throw new InvalidOperationException("These linked import results belong to another profile. Switch to that profile or remove the import filter.");
    }

    public static string Remove(string uri)
    {
        var address = new Uri(uri);
        var values = QueryHelpers.ParseQuery(address.Query).Where(pair => pair.Key is not ("importBatch" or "importOwner" or "page"))
            .ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ToString());
        return QueryHelpers.AddQueryString(address.AbsolutePath, values) + address.Fragment;
    }
}
