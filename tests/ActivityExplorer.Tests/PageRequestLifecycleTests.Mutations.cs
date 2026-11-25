using System.Reflection;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ActivityExplorer.Tests;

public sealed partial class PageRequestLifecycleTests
{
    [Fact]
    public async Task Committed_activity_update_retries_only_pending_maintenance()
    {
        var id = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var updates = 0;
        var recordOwners = new List<Guid>();
        var segmentOwners = new List<Guid>();
        var segmentRefreshes = 0;
        using var fixture = new PageFixture(
            activities: (method, args) => method.Name switch
            {
                "GetAsync" => Task.FromResult<ActivityDetail?>(Activity((Guid)args[0]!, "Current activity")),
                "UpdateReviewedAsync" => Updated(),
                _ => Unexpected(method)
            },
            statistics: (method, args) =>
            {
                Assert.Equal("RecomputeAsync", method.Name);
                recordOwners.Add((Guid)args[0]!);
                return Task.CompletedTask;
            },
            segments: (method, args) =>
            {
                if (method.Name == "ListAsync")
                {
                    segmentOwners.Add((Guid)args[0]!);
                    return Task.FromResult<IReadOnlyList<SegmentSummary>>([Segment(Guid.NewGuid()).Summary]);
                }
                Assert.Equal("RecomputeAsync", method.Name);
                segmentRefreshes++;
                return Task.CompletedTask;
            });
        await using var renderer = fixture.Renderer();
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<ActivityDetailPage>(Identity(id)));
        var page = (ActivityDetailPage)fixture.Components.Get(typeof(ActivityDetailPage));
        await renderer.Dispatcher.InvokeAsync(() => { Invoke(page, "ToggleMetadataEditor"); SetField(page, "_ownerId", destination); });
        await renderer.Dispatcher.InvokeAsync(() => InvokeTask(page, "Save"));
        Assert.Contains("Records or segment results still need refreshing", await renderer.Dispatcher.InvokeAsync(root.ToHtmlString), StringComparison.Ordinal);
        await renderer.Dispatcher.InvokeAsync(() => InvokeTask(page, "RetryMaintenance"));
        await renderer.Dispatcher.InvokeAsync(() => InvokeTask(page, "Save"));
        Assert.Equal(1, updates);
        Assert.Equal(new[] { Owner, destination }.Order(), recordOwners.Order());
        Assert.Equal(new[] { Owner, destination }.Order(), segmentOwners.Order());
        Assert.Equal(2, segmentRefreshes);
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.DoesNotContain("Retry records and segment refresh", html, StringComparison.Ordinal);
        Assert.Contains("File cleanup is pending", html, StringComparison.Ordinal);

        Task<ActivityUpdateResult> Updated()
        {
            updates++;
            return Task.FromResult(new ActivityUpdateResult(id, destination, true, true, true));
        }
    }

    [Fact]
    public async Task A_late_metadata_save_cannot_change_a_different_activity_view()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var saved = Pending<ActivityUpdateResult>();
        var updates = 0;
        using var fixture = new PageFixture(activities: (method, args) => method.Name switch
        {
            "GetAsync" => Task.FromResult<ActivityDetail?>(Activity((Guid)args[0]!, (Guid)args[0]! == first ? "First activity" : "Second activity")),
            "UpdateReviewedAsync" => Submit(),
            _ => Unexpected(method)
        });
        await using var renderer = fixture.Renderer();
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<ActivityDetailPage>(Identity(first)));
        var page = (ActivityDetailPage)fixture.Components.Get(typeof(ActivityDetailPage));
        Task mutation = Task.CompletedTask;
        await renderer.Dispatcher.InvokeAsync(() => { Invoke(page, "ToggleMetadataEditor"); mutation = InvokeTask(page, "Save"); });
        await renderer.Dispatcher.InvokeAsync(() => InvokeTask(page, "Save"));
        await renderer.Dispatcher.InvokeAsync(() => page.SetParametersAsync(Identity(second)));
        saved.SetResult(new ActivityUpdateResult(first, Owner, true, true, true));
        await mutation;
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Equal(1, updates);
        Assert.Contains("Second activity", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Activity metadata saved locally", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry records and segment refresh", html, StringComparison.Ordinal);

        Task<ActivityUpdateResult> Submit() { updates++; return saved.Task; }
    }

    [Fact]
    public async Task An_unknown_save_outcome_requires_an_authoritative_reload_before_retry()
    {
        var id = Guid.NewGuid();
        var reads = new List<Guid>();
        var updates = new List<UpdateActivityRequest>();
        var reread = Pending<ActivityDetail?>();
        using var fixture = new PageFixture(activities: (method, args) => method.Name switch
        {
            "GetAsync" => Read((Guid)args[0]!),
            "UpdateReviewedAsync" => Unconfirmed((UpdateActivityRequest)args[1]!),
            _ => Unexpected(method)
        });
        await using var renderer = fixture.Renderer();
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<ActivityDetailPage>(Identity(id)));
        var page = (ActivityDetailPage)fixture.Components.Get(typeof(ActivityDetailPage));
        await renderer.Dispatcher.InvokeAsync(() => Invoke(page, "ToggleMetadataEditor"));
        await renderer.Dispatcher.InvokeAsync(() => InvokeTask(page, "Save"));
        await renderer.Dispatcher.InvokeAsync(() => InvokeTask(page, "Save"));
        Assert.Equal("Current activity", Assert.Single(updates).Title);
        Assert.Equal(id, Assert.Single(reads));
        Assert.Contains("Reload activity before saving again", await renderer.Dispatcher.InvokeAsync(root.ToHtmlString), StringComparison.Ordinal);

        Task reload = Task.CompletedTask;
        await renderer.Dispatcher.InvokeAsync(() => { reload = InvokeTask(page, "ReloadMetadataAsync"); });
        try
        {
            Assert.Equal(new[] { id, id }, reads);
            Assert.False(reload.IsCompleted);
            await renderer.Dispatcher.InvokeAsync(() => InvokeTask(page, "Save"));
            Assert.Single(updates);
        }
        finally
        {
            reread.SetResult(Activity(id, "Reloaded activity"));
            await reload.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("Reloaded activity", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Reload activity before saving again", html, StringComparison.Ordinal);
        await renderer.Dispatcher.InvokeAsync(() => InvokeTask(page, "Save"));
        Assert.Collection(updates,
            update => Assert.Equal("Current activity", update.Title),
            update => Assert.Equal("Reloaded activity", update.Title));

        Task<ActivityDetail?> Read(Guid activityId)
        {
            reads.Add(activityId);
            return reads.Count == 2 ? reread.Task : Task.FromResult<ActivityDetail?>(Activity(activityId, "Current activity"));
        }

        Task<ActivityUpdateResult> Unconfirmed(UpdateActivityRequest request)
        {
            updates.Add(request);
            return Task.FromException<ActivityUpdateResult>(new IOException("Synthetic unknown outcome"));
        }
    }

    [Fact]
    public async Task Activity_confirmation_and_metadata_draft_keep_the_version_displayed_when_opened()
    {
        var id = Guid.NewGuid();
        var current = Activity(id, "First version");
        ActivityDeletionReview? deletion = null;
        var edits = new List<(ActivityMutationTarget Target, UpdateActivityRequest Request)>();
        using var fixture = new PageFixture(activities: (method, args) => method.Name switch
        {
            "GetAsync" => Task.FromResult<ActivityDetail?>(current),
            "DeleteReviewedAsync" => RejectDelete((ActivityDeletionReview)args[0]!),
            "UpdateReviewedAsync" => RejectEdit((ActivityMutationTarget)args[0]!, (UpdateActivityRequest)args[1]!),
            _ => Unexpected(method)
        });
        await using var renderer = fixture.Renderer();
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<ActivityDetailPage>(Identity(id)));
        var page = (ActivityDetailPage)fixture.Components.Get(typeof(ActivityDetailPage));
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            Invoke(page, "ToggleMetadataEditor");
            SetField(page, "_title", "Pending draft");
            Invoke(page, "BeginDelete");
        });
        current = current with { Summary = current.Summary with { Title = "Second version", MutationVersion = 1 } };
        await renderer.Dispatcher.InvokeAsync(() => InvokeTask(page, "LoadAsync"));

        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("Delete First version?", html, StringComparison.Ordinal);
        await renderer.Dispatcher.InvokeAsync(() => InvokeTask(page, "ConfirmDelete"));
        Assert.Equal(new ActivityMutationTarget(id, Owner, 0), Assert.Single(deletion!.Members));

        await renderer.Dispatcher.InvokeAsync(() => Invoke(page, "CancelDelete"));
        await renderer.Dispatcher.InvokeAsync(() => InvokeTask(page, "Save"));
        var (target, request) = Assert.Single(edits);
        Assert.Equal(new ActivityMutationTarget(id, Owner, 0), target);
        Assert.Equal("Pending draft", request.Title);

        Task<ActivityDeletionResult> RejectDelete(ActivityDeletionReview review)
        {
            deletion = review;
            return Task.FromException<ActivityDeletionResult>(new InvalidOperationException("The reviewed activity changed."));
        }

        Task<ActivityUpdateResult> RejectEdit(ActivityMutationTarget target, UpdateActivityRequest request)
        {
            edits.Add((target, request));
            return Task.FromException<ActivityUpdateResult>(new InvalidOperationException("The reviewed activity changed."));
        }
    }

    private static Task InvokeTask(IComponent page, string name) =>
        EventCallback.Factory.Create(page, (Func<Task>)(() =>
            (Task)page.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!)).InvokeAsync();
    private static void SetField(IComponent page, string name, object value) =>
        page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
}
