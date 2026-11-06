using System.Reflection;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ActivityExplorer.Tests;

public sealed class SharedMutationOutcomeTests
{
    [Fact]
    public async Task Committed_batch_with_failed_notification_cannot_be_applied_again()
    {
        var organization = new OrganizationStub();
        await WithComponentAsync<ActivityBatchEditor>(organization, new()
        {
            [nameof(ActivityBatchEditor.OwnerId)] = Guid.NewGuid(),
            [nameof(ActivityBatchEditor.OnApplied)] = EventCallback.Factory.Create<BatchEditResult>(this,
                _ => Task.FromException(new InvalidOperationException("Synthetic refresh failure")))
        }, async (component, html) =>
        {
            await InvokeEventAsync(component, "Prepare");
            await InvokeEventAsync(component, "Apply");
            Assert.Contains("Changes were applied", html(), StringComparison.Ordinal);
            Assert.DoesNotContain("Nothing changed", html(), StringComparison.Ordinal);
            Assert.DoesNotContain(">Apply changes</button>", html(), StringComparison.Ordinal);
            await InvokeEventAsync(component, "Apply");
            Assert.Equal(1, organization.ApplyCalls);
        });
    }

    [Fact]
    public async Task Rejected_batch_requires_a_fresh_review_before_retry()
    {
        var organization = new OrganizationStub { RejectApply = true };
        await WithComponentAsync<ActivityBatchEditor>(organization, [], async (component, html) =>
        {
            await InvokeEventAsync(component, "Prepare");
            await InvokeEventAsync(component, "Apply");
            Assert.Contains("Nothing changed", html(), StringComparison.Ordinal);
            await InvokeEventAsync(component, "Apply");
            Assert.Equal(1, organization.ApplyCalls);
            await InvokeEventAsync(component, "ChangeEdits");
            await InvokeEventAsync(component, "Prepare");
            organization.RejectApply = false;
            await InvokeEventAsync(component, "Apply");
            Assert.Equal(2, organization.ApplyCalls);
            Assert.Equal(2, organization.PrepareCalls);
            Assert.Contains("Activity changes applied", html(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Unconfirmed_batch_result_requires_refresh_before_any_new_review()
    {
        var organization = new OrganizationStub { UnknownApply = true };
        await WithComponentAsync<ActivityBatchEditor>(organization, [], async (component, html) =>
        {
            await InvokeEventAsync(component, "Prepare");
            await InvokeEventAsync(component, "Apply");
            Assert.Contains("The result could not be confirmed", html(), StringComparison.Ordinal);
            Assert.DoesNotContain("Nothing changed", html(), StringComparison.Ordinal);
            await InvokeEventAsync(component, "ChangeEdits");
            await InvokeEventAsync(component, "Prepare");
            await InvokeEventAsync(component, "Apply");
            Assert.Equal(1, organization.PrepareCalls);
            Assert.Equal(1, organization.ApplyCalls);
        });
    }

    [Fact]
    public async Task Saved_activity_tags_remain_saved_when_parent_refresh_fails()
    {
        var organization = new OrganizationStub();
        await WithComponentAsync<ActivityTagEditor>(organization, new()
        {
            [nameof(ActivityTagEditor.OwnerId)] = Guid.NewGuid(),
            [nameof(ActivityTagEditor.ActivityId)] = Guid.NewGuid(),
            [nameof(ActivityTagEditor.Changed)] = EventCallback.Factory.Create(this,
                () => Task.FromException(new InvalidOperationException("Synthetic refresh failure")))
        }, async (component, html) =>
        {
            await InvokeEventAsync(component, "ToggleEditorAsync");
            await InvokeEventAsync(component, "SaveAsync");
            Assert.Equal(1, organization.SetTagCalls);
            Assert.Contains("Activity tags were saved", html(), StringComparison.Ordinal);
            Assert.Contains("Reload activity", html(), StringComparison.Ordinal);
            Assert.DoesNotContain("Nothing changed", html(), StringComparison.Ordinal);
            Assert.DoesNotContain(">Save tags</button>", html(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Tag_manager_retries_only_refresh_after_a_committed_create()
    {
        var organization = new OrganizationStub();
        var notifications = 0;
        await WithComponentAsync<TagManager>(organization, new()
        {
            [nameof(TagManager.OwnerId)] = Guid.NewGuid(),
            [nameof(TagManager.Changed)] = EventCallback.Factory.Create(this, () =>
                ++notifications == 1 ? Task.FromException(new InvalidOperationException("Synthetic refresh failure")) : Task.CompletedTask)
        }, async (component, html) =>
        {
            await InvokeEventAsync(component, "CreateAsync");
            Assert.Equal(1, organization.CreateTagCalls);
            Assert.Contains("Tag created.", html(), StringComparison.Ordinal);
            Assert.Contains(">Refresh tags</button>", html(), StringComparison.Ordinal);
            Assert.DoesNotContain(">Create tag</button>", html(), StringComparison.Ordinal);
            await InvokeEventAsync(component, "RefreshMutationAsync");
            Assert.Equal(1, organization.CreateTagCalls);
            Assert.Equal(2, notifications);
            Assert.DoesNotContain("could not be refreshed", html(), StringComparison.Ordinal);
            Assert.Contains(">Create tag</button>", html(), StringComparison.Ordinal);
        });
    }

    private static async Task WithComponentAsync<T>(OrganizationStub organization, Dictionary<string, object?> parameters,
        Func<T, Func<string>, Task> verify) where T : ComponentBase
    {
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IActivityOrganizationService>(organization)
            .AddSingleton<IComponentActivator>(activator)
            .AddSingleton<NavigationManager>(new TestNavigationManager()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            await verify((T)activator.Components.Single(component => component is T), root.ToHtmlString);
        });
    }

    private static Task InvokeEventAsync(ComponentBase component, string method)
    {
        var handler = component.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var callback = handler.ReturnType == typeof(void)
            ? (MulticastDelegate)handler.CreateDelegate<Action>(component)
            : handler.CreateDelegate<Func<Task>>(component);
        return ((IHandleEvent)component).HandleEventAsync(new EventCallbackWorkItem(callback), null);
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public List<IComponent> Components { get; } = [];
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            Components.Add(component);
            return component;
        }
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/activities");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class OrganizationStub : IActivityOrganizationService
    {
        public int ApplyCalls { get; private set; }
        public int PrepareCalls { get; private set; }
        public int SetTagCalls { get; private set; }
        public int CreateTagCalls { get; private set; }
        public bool RejectApply { get; set; }
        public bool UnknownApply { get; set; }
        public Task<IReadOnlyList<TagSummary>> ListTagsAsync(Guid ownerId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TagSummary>>([]);
        public Task<Guid> CreateTagAsync(Guid ownerId, string name, CancellationToken cancellationToken = default) { CreateTagCalls++; return Task.FromResult(Guid.NewGuid()); }
        public Task RenameTagAsync(Guid ownerId, Guid tagId, string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteTagAsync(Guid ownerId, Guid tagId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetActivityTagsAsync(Guid ownerId, Guid activityId, IReadOnlyList<Guid> tagIds, long expectedVersion, CancellationToken cancellationToken = default) { SetTagCalls++; return Task.CompletedTask; }
        public Task<IReadOnlyList<SavedSearchSummary>> ListSavedSearchesAsync(Guid ownerId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SavedSearchSummary>>([]);
        public Task ValidateSavedSearchAsync(Guid ownerId, string name, SavedSearchCriteria criteria, Guid? id = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ValidateSavedSearchRenameAsync(Guid ownerId, Guid id, string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Guid> SaveSavedSearchAsync(Guid ownerId, string name, SavedSearchCriteria criteria, Guid? id = null, CancellationToken cancellationToken = default) => Task.FromResult(id ?? Guid.NewGuid());
        public Task RenameSavedSearchAsync(Guid ownerId, Guid id, string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteSavedSearchAsync(Guid ownerId, Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BatchReview> PrepareBatchAsync(Guid ownerId, IReadOnlyCollection<Guid>? activityIds, ActivityFilter? filter, BatchChangeRequest changes, CancellationToken cancellationToken = default)
        {
            PrepareCalls++;
            return Task.FromResult(new BatchReview(ownerId, [], changes, [], []));
        }
        public Task<BatchEditResult> ApplyBatchAsync(BatchReview review, CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            if (UnknownApply) return Task.FromException<BatchEditResult>(new IOException("Synthetic connection failure"));
            return RejectApply ? Task.FromException<BatchEditResult>(new InvalidOperationException("Synthetic stale review")) : Task.FromResult(new BatchEditResult(3, 3));
        }
    }
}
