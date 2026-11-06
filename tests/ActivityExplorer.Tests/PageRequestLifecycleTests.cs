using System.Reflection;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Web.Components.Pages;
using ActivityExplorer.Web.Components.Shared;
using ActivityExplorer.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace ActivityExplorer.Tests;

public sealed partial class PageRequestLifecycleTests
{
    private static readonly Guid Owner = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset Started = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TrackPoint[] Points =
    [
        new(Started, 55, 12, 0, 1, 5, null, null, null, null),
        new(Started.AddSeconds(10), 55.001, 12.001, 100, 2, 5, null, null, null, null)
    ];

    [Theory]
    [InlineData(typeof(RouteDetailPage))]
    [InlineData(typeof(ActivitySegmentCreatorPage))]
    public async Task Late_resource_response_cannot_replace_the_new_route(Type pageType)
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var firstRoute = Pending<RouteDetail?>();
        var secondRoute = Pending<RouteDetail?>();
        var firstActivity = Pending<ActivityDetail?>();
        var secondActivity = Pending<ActivityDetail?>();
        using var fixture = new PageFixture(
            routes: (method, args) => method.Name == "GetAsync" ? (Guid)args[0]! == first ? firstRoute.Task : secondRoute.Task : Unexpected(method),
            activities: (method, args) => method.Name == "GetAsync" ? (Guid)args[0]! == first ? firstActivity.Task : secondActivity.Task : Unexpected(method));
        await using var renderer = fixture.Renderer();
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent(pageType, Identity(first)));
        var page = fixture.Components.Get(pageType);
        Task secondLoad = Task.CompletedTask;
        await renderer.Dispatcher.InvokeAsync(() => { secondLoad = page.SetParametersAsync(Identity(second)); });
        secondRoute.SetResult(Route(second, "Second resource"));
        secondActivity.SetResult(Activity(second, "Second resource"));
        await secondLoad;
        firstRoute.SetResult(Route(first, "First resource"));
        firstActivity.SetResult(Activity(first, "First resource"));
        await root.QuiescenceTask;
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("Second resource", html, StringComparison.Ordinal);
        Assert.DoesNotContain("First resource", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(RouteDetailPage))]
    [InlineData(typeof(ActivitySegmentCreatorPage))]
    [InlineData(typeof(SegmentSubsegmentCreatorPage))]
    public async Task Disposing_a_pending_resource_load_cancels_its_service_request(Type pageType)
    {
        var route = Pending<RouteDetail?>();
        var activity = Pending<ActivityDetail?>();
        var segment = Pending<SegmentDetail?>();
        var request = CancellationToken.None;
        object? Read(MethodInfo method, object?[] args)
        {
            if (method.Name == "GetAsync")
            {
                request = (CancellationToken)args[^1]!;
                return pageType == typeof(RouteDetailPage) ? route.Task : pageType == typeof(ActivitySegmentCreatorPage) ? activity.Task : segment.Task;
            }
            if (method.Name == "ListAsync") return Task.FromResult<IReadOnlyList<SegmentSummary>>([]);
            return Unexpected(method);
        }
        using var fixture = new PageFixture(Read, Read, Read);
        var renderer = fixture.Renderer();
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent(pageType, Identity(Guid.NewGuid())));
        await renderer.DisposeAsync();
        var cancelled = request.IsCancellationRequested;
        route.SetResult(null); activity.SetResult(null); segment.SetResult(null);
        await root.QuiescenceTask;
        Assert.True(cancelled, "The disposed page left its owned resource request running.");
    }

    [Theory]
    [InlineData(typeof(RouteDetailPage), false, true)]
    [InlineData(typeof(ActivitySegmentCreatorPage), false, true)]
    [InlineData(typeof(SegmentSubsegmentCreatorPage), false, true)]
    [InlineData(typeof(RouteDetailPage), true, false)]
    [InlineData(typeof(ActivitySegmentCreatorPage), true, false)]
    [InlineData(typeof(SegmentSubsegmentCreatorPage), true, false)]
    [InlineData(typeof(RouteDetailPage), false, false)]
    [InlineData(typeof(ActivitySegmentCreatorPage), false, false)]
    [InlineData(typeof(SegmentSubsegmentCreatorPage), false, false)]
    public async Task Creator_completion_only_navigates_for_its_current_source(Type pageType, bool dispose, bool replace)
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var created = Guid.NewGuid();
        var create = Pending<Guid>();
        Guid? submittedSource = null;
        object? SegmentsCall(MethodInfo method, object?[] args)
        {
            switch (method.Name)
            {
                case "GetAsync": return Task.FromResult<SegmentDetail?>(Segment((Guid)args[0]!));
                case "ListAsync": return Task.FromResult<IReadOnlyList<SegmentSummary>>([]);
                case "CreateAsync": return create.Task;
                case "CreateFromActivityAsync": submittedSource = ((CreateSegmentRequest)args[0]!).ActivityId; return create.Task;
                case "CreateSubsegmentAsync": submittedSource = ((CreateSubsegmentRequest)args[0]!).ParentSegmentId; return create.Task;
                default: return Unexpected(method);
            }
        }
        using var fixture = new PageFixture(
            routes: (method, args) => method.Name == "GetAsync" ? Task.FromResult<RouteDetail?>(Route((Guid)args[0]!, "Source route")) : Unexpected(method),
            activities: (method, args) => method.Name == "GetAsync" ? Task.FromResult<ActivityDetail?>(Activity((Guid)args[0]!, "Source activity")) : Unexpected(method),
            segments: SegmentsCall);
        var renderer = fixture.Renderer();
        try
        {
            await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync(pageType, Identity(first)));
            var creator = (SegmentCreatorWorkspace)fixture.Components.Get(typeof(SegmentCreatorWorkspace));
            Task mutation = Task.CompletedTask;
            await renderer.Dispatcher.InvokeAsync(() => { mutation = creator.OnCreate.InvokeAsync(new SegmentCreatorSubmission("New section", 30, 0, 1, false)); });
            if (pageType != typeof(RouteDetailPage)) Assert.Equal(first, submittedSource);
            if (dispose) await renderer.DisposeAsync();
            else if (replace) await renderer.Dispatcher.InvokeAsync(() => fixture.Components.Get(pageType).SetParametersAsync(Identity(second)));
            create.SetResult(created);
            await mutation;
            if (dispose || replace) Assert.Empty(fixture.Navigation.Destinations);
            else Assert.Equal(pageType == typeof(SegmentSubsegmentCreatorPage) ? $"/segments/{first}?section={created}" : $"/segments/{created}", Assert.Single(fixture.Navigation.Destinations));
        }
        finally { create.TrySetResult(created); await renderer.DisposeAsync(); }
    }

    [Fact]
    public async Task Disposed_segment_library_does_not_continue_loading_relationships()
    {
        var list = Pending<IReadOnlyList<SegmentSummary>>();
        var request = CancellationToken.None;
        var relationshipReads = 0;
        using var fixture = new PageFixture(segments: (method, args) =>
        {
            if (method.Name == "ListAsync") { request = (CancellationToken)args[^1]!; return list.Task; }
            if (method.Name == "ListRelationshipsAsync") { relationshipReads++; return Task.FromResult<IReadOnlyList<SegmentRelationship>>([]); }
            return Unexpected(method);
        });
        var renderer = fixture.Renderer();
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<Segments>());
        await renderer.DisposeAsync();
        var cancelled = request.IsCancellationRequested;
        list.SetResult([]);
        await root.QuiescenceTask;
        Assert.True(cancelled, "The disposed library left its catalogue request running.");
        Assert.Equal(0, relationshipReads);
    }

    [Fact]
    public async Task Activity_route_reuse_closes_previous_editors_and_delete_confirmation()
    {
        using var fixture = new PageFixture(activities: (method, args) => method.Name == "GetAsync"
            ? Task.FromResult<ActivityDetail?>(Activity((Guid)args[0]!, "Current activity")) : Unexpected(method));
        await using var renderer = fixture.Renderer();
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<ActivityDetailPage>(Identity(Guid.NewGuid())));
        var page = fixture.Components.Get(typeof(ActivityDetailPage));
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            Invoke(page, "BeginDelete");
            Invoke(page, "EditMetric", new ActivityMetricSummary(Guid.NewGuid(), "custom.previous", "Previous metric", 1, null, null, ActivityMetricOrigin.Manual, Started));
            typeof(ActivityDetailPage).GetField("_editing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, true);
        });
        await renderer.Dispatcher.InvokeAsync(() => page.SetParametersAsync(Identity(Guid.NewGuid())));
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.DoesNotContain("delete-confirmation", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Edit local metadata", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Previous metric", html, StringComparison.Ordinal);
        Assert.Contains("Add metric", html, StringComparison.Ordinal);
    }

    private static ParameterView Identity(Guid id) => ParameterView.FromDictionary(new Dictionary<string, object?> { ["Id"] = id });
    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static object? Unexpected(MethodInfo method) => throw new InvalidOperationException("Unexpected service call: " + method.Name);
    private static void Invoke(IComponent page, string name, params object?[] args) => page.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args);
    private static RouteDetail Route(Guid id, string name) => new(new(id, Owner, "Synthetic owner", name, SportKind.Cycling, 100, 1), null, Points);
    private static SegmentDetail Segment(Guid id) => new(new(id, Owner, "Synthetic owner", "Parent segment", SportKind.Cycling, 100, 30, 0, null, null, null, null), Points, [], null, [], [], []);
    private static ActivityDetail Activity(Guid id, string title) => new(new(id, Owner, "Synthetic owner", title, SportKind.Cycling, Started, 100, 10, 1, null, null, true, false),
        null, null, 10, null, 10, 10, null, null, null, null, null, Points, [], [], [],
        new(null, MovingTimeSource.Unavailable, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null), [], "synthetic-stream");

    private sealed class PageFixture : IDisposable
    {
        private readonly ServiceProvider _services;
        public CapturingActivator Components { get; } = new();
        public TestNavigation Navigation { get; } = new();
        public PageFixture(Func<MethodInfo, object?[], object?>? routes = null, Func<MethodInfo, object?[], object?>? activities = null,
            Func<MethodInfo, object?[], object?>? segments = null, Func<MethodInfo, object?[], object?>? statistics = null)
        {
            _services = new ServiceCollection().AddLogging()
                .AddSingleton<IComponentActivator>(Components).AddSingleton<NavigationManager>(Navigation)
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()).AddSingleton<IJSRuntime, StaticJs>()
                .AddSingleton(Proxy<IRouteService>(routes ?? ((method, _) => Unexpected(method))))
                .AddSingleton(Proxy<IActivityQueryService>(activities ?? ((method, _) => Unexpected(method))))
                .AddSingleton(Proxy<ISegmentService>(segments ?? ((method, _) => Unexpected(method))))
                .AddSingleton(Proxy<IStatisticsService>(statistics ?? ((method, _) => Unexpected(method))))
                .AddSingleton(Proxy<IProfileService>((method, _) => method.Name == "ListAsync"
                    ? Task.FromResult<IReadOnlyList<ProfileSummary>>([new(Owner, "Synthetic owner", 0, 0, Started)]) : Unexpected(method)))
                .AddSingleton(Proxy<IMapSettingsService>((method, _) => method.Name == "GetModeAsync" ? Task.FromResult(MapPrivacyMode.Blank) : Unexpected(method)))
                .AddSingleton(Proxy<IActivityOrganizationService>((method, _) => Unexpected(method)))
                .AddSingleton<ProfileState>().AddSingleton<IActivityRangeAnalyzer, ActivityRangeAnalyzer>().BuildServiceProvider();
        }
        public HtmlRenderer Renderer() => new(_services, _services.GetRequiredService<ILoggerFactory>());
        public void Dispose() => _services.Dispose();
    }

    public class ServiceProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }
    private static T Proxy<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, ServiceProxy>();
        ((ServiceProxy)(object)proxy).Handler = handler;
        return proxy;
    }
    private sealed class CapturingActivator : IComponentActivator
    {
        private readonly Dictionary<Type, IComponent> _components = [];
        public IComponent CreateInstance(Type componentType) => _components[componentType] = (IComponent)Activator.CreateInstance(componentType)!;
        public IComponent Get(Type type) => _components[type];
    }
    private sealed class TestNavigation : NavigationManager
    {
        public List<string> Destinations { get; } = [];
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { Destinations.Add(uri); Uri = ToAbsoluteUri(uri).ToString(); }
    }
    private sealed class StaticJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("Static rendering does not run JS.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
