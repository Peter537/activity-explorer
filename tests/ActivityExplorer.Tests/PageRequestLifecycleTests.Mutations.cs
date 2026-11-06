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
                "UpdateAsync" => Updated(),
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
        await renderer.Dispatcher.InvokeAsync(() => { SetField(page, "_editing", true); SetField(page, "_ownerId", destination); });
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
            "UpdateAsync" => Submit(),
            _ => Unexpected(method)
        });
        await using var renderer = fixture.Renderer();
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<ActivityDetailPage>(Identity(first)));
        var page = (ActivityDetailPage)fixture.Components.Get(typeof(ActivityDetailPage));
        Task mutation = Task.CompletedTask;
        await renderer.Dispatcher.InvokeAsync(() => { SetField(page, "_editing", true); mutation = InvokeTask(page, "Save"); });
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
        var updates = 0;
        var id = Guid.NewGuid();
        using var fixture = new PageFixture(activities: (method, args) => method.Name switch
        {
            "GetAsync" => Task.FromResult<ActivityDetail?>(Activity((Guid)args[0]!, "Current activity")),
            "UpdateAsync" => Unconfirmed(),
            _ => Unexpected(method)
        });
        await using var renderer = fixture.Renderer();
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<ActivityDetailPage>(Identity(id)));
        var page = (ActivityDetailPage)fixture.Components.Get(typeof(ActivityDetailPage));
        await renderer.Dispatcher.InvokeAsync(() => SetField(page, "_editing", true));
        await renderer.Dispatcher.InvokeAsync(() => InvokeTask(page, "Save"));
        await renderer.Dispatcher.InvokeAsync(() => InvokeTask(page, "Save"));
        Assert.Equal(1, updates);
        Assert.Contains("Reload activity before saving again", await renderer.Dispatcher.InvokeAsync(root.ToHtmlString), StringComparison.Ordinal);
        await renderer.Dispatcher.InvokeAsync(() => InvokeTask(page, "LoadAsync"));
        await renderer.Dispatcher.InvokeAsync(() => InvokeTask(page, "Save"));
        Assert.Equal(2, updates);

        Task<ActivityUpdateResult> Unconfirmed() { updates++; return Task.FromException<ActivityUpdateResult>(new IOException("Synthetic unknown outcome")); }
    }

    private static Task InvokeTask(IComponent page, string name) =>
        EventCallback.Factory.Create(page, (Func<Task>)(() =>
            (Task)page.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!)).InvokeAsync();
    private static void SetField(IComponent page, string name, object value) =>
        page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
}
