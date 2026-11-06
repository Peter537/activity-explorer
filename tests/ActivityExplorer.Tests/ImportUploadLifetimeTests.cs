using System.Reflection;
using System.Text.Json;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Components.Pages;
using ActivityExplorer.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace ActivityExplorer.Tests;

public sealed class ImportUploadLifetimeTests
{
    [Fact]
    public async Task Settled_partial_upload_polls_every_known_id_and_retains_unresolved_file_outcomes()
    {
        var js = new DeferredUploadRuntime();
        js.Completion.SetResult("""
            {"items":[{"index":0,"name":"accepted.gpx","outcome":"Queued","id":"00000000-0000-0000-0000-000000000001","error":null},
            {"index":1,"name":"rejected.gpx","outcome":"Rejected","id":null,"error":"Synthetic rejection"},
            {"index":2,"name":"delayed.gpx","outcome":"Queued","id":"00000000-0000-0000-0000-000000000002","error":null},
            {"index":3,"name":"lost.gpx","outcome":"Unknown","id":null,"error":"Check import history"}]}
            """);
        var data = new PageData { ReturnCompletedReports = true };
        var activator = new ImportPageActivator();
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IJSRuntime>(js).AddSingleton<IProfileService>(data)
            .AddSingleton<IImportHistoryService>(data).AddSingleton<IWatchedFolderService>(data)
            .AddSingleton(new ProfileState(data)).AddSingleton<IComponentActivator>(activator)
            .AddSingleton<NavigationManager>(new ImportNavigationManager()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<ImportsPage>();
            var page = activator.Page!;
            await ((IHandleEvent)page).HandleEventAsync(new EventCallbackWorkItem(
                (Func<Task>)(() => InvokeAsync(page, "UploadMany", "synthetic-file-input", null))), null);
            var polling = (Task)typeof(ImportsPage).GetField("_pollingTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
            await polling.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, data.ReportIds.Count);
            Assert.Equal(2, data.ReportIds.Distinct().Count());
            var html = root.ToHtmlString();
            Assert.Contains("Queued files finished processing successfully", html, StringComparison.Ordinal);
            Assert.Contains("Upload rejected", html, StringComparison.Ordinal);
            Assert.Contains("Upload outcome unknown", html, StringComparison.Ordinal);
            Assert.DoesNotContain("All selected files finished processing successfully", html, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Upload_completion_after_disposal_does_not_start_polling_or_refresh_the_page()
    {
        var js = new DeferredUploadRuntime();
        var data = new PageData();
        var page = new ImportsPage();
        Inject(page, "JS", js);
        Inject(page, "History", data);
        Inject(page, "WatchedFolders", data);
        Inject(page, "ProfileState", new ProfileState(data));
        await InvokeAsync(page, "OnInitializedAsync");
        var upload = InvokeAsync(page, "UploadMany", "synthetic-file-input", null);
        await js.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.DisposeAsync();
        var folderReads = data.FolderReads;
        var historyReads = data.HistoryReads;
        js.Completion.SetResult("""
            {"ids":["00000000-0000-0000-0000-000000000001"],"items":[{"index":0,"name":"synthetic.gpx","outcome":"Queued","id":"00000000-0000-0000-0000-000000000001","error":null}]}
            """);
        try
        {
            await upload;
            Assert.Equal(folderReads, data.FolderReads);
            Assert.Equal(historyReads, data.HistoryReads);
            Assert.Null(typeof(ImportsPage).GetField("_polling", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page));
        }
        finally { await page.DisposeAsync(); }
    }

    private static void Inject(ImportsPage page, string name, object value) =>
        typeof(ImportsPage).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static Task InvokeAsync(ImportsPage page, string name, params object?[] args) =>
        (Task)typeof(ImportsPage).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args)!;

    private sealed class DeferredUploadRuntime : IJSRuntime
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("activityExplorerImports.uploadMany", identifier);
            Started.TrySetResult();
            return JsonSerializer.Deserialize<TValue>(await Completion.Task.WaitAsync(cancellationToken), JsonOptions)!;
        }
    }

    private sealed class PageData : IProfileService, IImportHistoryService, IWatchedFolderService
    {
        public bool ReturnCompletedReports { get; init; }
        public List<Guid> ReportIds { get; } = [];
        public int FolderReads { get; private set; }
        public int HistoryReads { get; private set; }
        public Task<IReadOnlyList<ProfileSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProfileSummary>>([new(Guid.NewGuid(), "Synthetic profile", 0, 0, DateTimeOffset.UnixEpoch)]);
        public Task<Guid> CreateAsync(string displayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateTimeZoneAsync(Guid ownerId, string timeZoneId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProfileDeletionResult> DeleteAsync(Guid ownerId, string confirmation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProfileExport> ExportAsync(Guid ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImportHistoryPage> ListAsync(Guid? ownerId, int limit = 10, CancellationToken cancellationToken = default) => SearchAsync(new(), limit, cancellationToken);
        public Task<ImportHistoryPage> SearchAsync(ImportHistoryFilter filter, int limit = 10, CancellationToken cancellationToken = default)
        {
            HistoryReads++;
            return Task.FromResult(new ImportHistoryPage([], 0));
        }
        public Task<ImportReport?> GetReportAsync(Guid importId, CancellationToken cancellationToken = default)
        {
            if (!ReturnCompletedReports) throw new InvalidOperationException("Disposed upload started polling.");
            ReportIds.Add(importId);
            return Task.FromResult<ImportReport?>(new(importId, ImportStatus.Completed, 1, 0, 0, 0, 0, "Synthetic completion"));
        }
        public Task<IReadOnlyList<WatchedFolderSummary>> ListAsync(Guid? ownerId, CancellationToken cancellationToken = default)
        {
            FolderReads++;
            return Task.FromResult<IReadOnlyList<WatchedFolderSummary>>([]);
        }
        public Task<Guid> AddAsync(Guid ownerId, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReconcileAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ImportPageActivator : IComponentActivator
    {
        public ImportsPage? Page { get; private set; }
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is ImportsPage page) Page = page;
            return component;
        }
    }

    private sealed class ImportNavigationManager : NavigationManager
    {
        public ImportNavigationManager() => Initialize("http://localhost/", "http://localhost/imports");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
