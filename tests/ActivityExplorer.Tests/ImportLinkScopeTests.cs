using ActivityExplorer.Web.Services;

namespace ActivityExplorer.Tests;

public sealed class ImportLinkScopeTests
{
    [Fact]
    public void Linked_results_require_the_bookmarked_profile_and_remove_only_import_context()
    {
        var batch = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var scope = ImportLinkScope.Parse(batch.ToString(), owner.ToString())!;
        Assert.Equal(batch, scope.BatchId);
        scope.ValidateOwner(owner);
        Assert.Throws<InvalidOperationException>(() => scope.ValidateOwner(null));
        Assert.Throws<InvalidOperationException>(() => scope.ValidateOwner(Guid.NewGuid()));
        Assert.Null(ImportLinkScope.Parse(null, null));
        Assert.Throws<ArgumentException>(() => ImportLinkScope.Parse("invalid", owner.ToString()));
        Assert.Throws<ArgumentException>(() => ImportLinkScope.Parse(batch.ToString(), null));
        Assert.Equal("/activities?q=Hill&from=2026-01-01#results", ImportLinkScope.Remove(
            $"http://localhost/activities?q=Hill&importBatch={batch}&importOwner={owner}&from=2026-01-01&page=3#results"));
    }
}
