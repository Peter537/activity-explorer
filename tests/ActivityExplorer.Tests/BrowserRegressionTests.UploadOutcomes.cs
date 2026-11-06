using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    private static readonly string[] WorkerUploadNames = ["accepted", "rejected", "delayed", "lost", "server-error"];
    private static readonly string[] WorkerUploadOutcomes = ["Queued", "Rejected", "Queued", "Unknown", "Unknown"];
    private static readonly int[] WorkerUploadIndices = [0, 1, 2, 3, 4];
    private static readonly string[] WorkerUploadFileNames = ["accepted.gpx", "delayed.gpx", "lost.gpx", "rejected.gpx", "server-error.gpx"];
    private static readonly string[] PageUploadNames = ["accepted", "rejected", "delayed", "lost"];
    [Fact]
    public async Task Upload_workers_settle_delayed_rejected_and_unknown_responses_without_retrying()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("<article><input id='upload' type='file' multiple></article>");
        await page.AddScriptTagAsync(new() { Content = await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "src/ActivityExplorer.Web/wwwroot/js/imports.js")) });
        await page.Locator("input").SetInputFilesAsync(WorkerUploadNames
            .Select(name => new FilePayload { Name = name + ".gpx", MimeType = "application/gpx+xml", Buffer = Encoding.UTF8.GetBytes("synthetic") }).ToArray());
        await page.EvaluateAsync("""
            () => {
                window.uploadCalls = [];
                const delayed = new Promise(resolve => window.releaseUpload = resolve);
                window.fetch = async (url, options) => {
                    if (url === '/internal/antiforgery/token') return new Response(JSON.stringify({ token: 'synthetic' }));
                    const name = options.body.get('file').name;
                    window.uploadCalls.push(name);
                    if (name === 'delayed.gpx') { window.delayedStarted = true; await delayed; }
                    if (name === 'rejected.gpx') return new Response(JSON.stringify({ error: 'Synthetic rejection' }), { status: 422 });
                    if (name === 'server-error.gpx') return new Response(JSON.stringify({ error: 'Synthetic failure after acceptance' }), { status: 500 });
                    if (name === 'lost.gpx') return new Response('{');
                    return new Response(JSON.stringify({ id: name === 'accepted.gpx' ? '00000000-0000-0000-0000-000000000001' : '00000000-0000-0000-0000-000000000002' }));
                };
                window.uploadSettled = false;
                window.uploadResultPromise = activityExplorerImports.uploadMany('upload', 'synthetic-owner', null, 2)
                    .then(result => { window.uploadResult = result; window.uploadSettled = true; })
                    .catch(error => { window.uploadError = error.message; window.uploadSettled = true; });
            }
            """);
        await page.WaitForFunctionAsync("() => window.delayedStarted && window.uploadCalls.length === 5");
        Assert.False(await page.EvaluateAsync<bool>("window.uploadSettled"));
        await page.EvaluateAsync("window.releaseUpload()");
        var response = await page.EvaluateAsync<JsonElement>("window.uploadResultPromise.then(() => window.uploadResult)");
        var items = response.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(WorkerUploadOutcomes, items.Select(item => item.GetProperty("outcome").GetString()));
        Assert.Equal(WorkerUploadIndices, items.Select(item => item.GetProperty("index").GetInt32()));
        Assert.Equal(2, items.Count(item => item.GetProperty("id").ValueKind == JsonValueKind.String));
        Assert.Contains("Check import history", items[3].GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Contains("Check import history", items[4].GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, await page.Locator("input").EvaluateAsync<int>("input => input.files.length"));
        Assert.Equal(WorkerUploadFileNames, (await page.EvaluateAsync<string[]>("window.uploadCalls")).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Partial_uploads_poll_accepted_files_and_keep_unknown_outcomes_visible()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedOrganizationBrowserDataAsync(dataRoot);
        var origin = $"http://127.0.0.1:{ReservePort()}";
        var output = new StringBuilder();
        using var process = StartApplication(FindWebAssembly(FindRepositoryRoot()), dataRoot, origin, output);
        var delayedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync();
            var requests = 0;
            await page.RouteAsync("**/internal/imports?**", async route =>
            {
                Interlocked.Increment(ref requests);
                var body = Encoding.UTF8.GetString(route.Request.PostDataBuffer!);
                if (body.Contains("rejected.gpx", StringComparison.Ordinal))
                {
                    await route.FulfillAsync(new() { Status = 422, ContentType = "application/json", Body = "{\"error\":\"Synthetic rejection\"}" });
                    return;
                }
                var response = await route.FetchAsync();
                if (body.Contains("delayed.gpx", StringComparison.Ordinal))
                {
                    delayedStarted.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
                }
                if (body.Contains("lost.gpx", StringComparison.Ordinal))
                    await route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = "{" });
                else await route.FulfillAsync(new() { Response = response });
            });
            await page.GotoAsync(origin + "/imports", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.EastOwner.ToString());
            var input = page.GetByLabel("Choose one or more files", new() { Exact = true });
            await input.SetInputFilesAsync(PageUploadNames.Select(name => new FilePayload
            {
                Name = name + ".gpx",
                MimeType = "application/gpx+xml",
                Buffer = Encoding.UTF8.GetBytes(TestSupport.Gpx(name: "Synthetic " + name))
            }).ToArray());
            var queue = page.GetByRole(AriaRole.Button, new() { Name = "Queue selected files", Exact = true });
            await queue.ClickAsync();
            await delayedStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assertions.Expect(input).ToBeDisabledAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Uploading...", Exact = true })).ToBeDisabledAsync();
            release.TrySetResult();
            await Assertions.Expect(page.GetByText("Queued files finished processing successfully. Other upload outcomes still need review below.", new() { Exact = true }))
                .ToBeVisibleAsync(new() { Timeout = 30000 });
            var outcomes = page.GetByRole(AriaRole.Region, new() { Name = "Upload results", Exact = true });
            await Assertions.Expect(outcomes.Locator("li")).ToHaveCountAsync(4);
            await Assertions.Expect(outcomes).ToContainTextAsync("Upload rejected");
            await Assertions.Expect(outcomes).ToContainTextAsync("Upload outcome unknown");
            await Assertions.Expect(outcomes).ToContainTextAsync("Check import history");
            Assert.Equal(0, await input.EvaluateAsync<int>("input => input.files.length"));
            Assert.Equal(4, Volatile.Read(ref requests));
        }
        catch
        {
            TestContext.Current.TestOutputHelper!.WriteLine(output.ToString());
            await using var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot));
            var batches = await db.ImportBatches.AsNoTracking().Select(batch => new { batch.DisplayName, batch.Status, batch.ErrorMessage }).ToArrayAsync();
            foreach (var batch in batches)
                TestContext.Current.TestOutputHelper.WriteLine($"Synthetic import {batch.DisplayName}: {batch.Status}; {batch.ErrorMessage}");
            throw;
        }
        finally
        {
            release.TrySetResult();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    [Fact]
    public async Task Route_upload_preserves_committed_id_and_cleanup_notice_through_browser_interop()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        const string routeName = "Synthetic route upload contract";
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedOrganizationBrowserDataAsync(dataRoot);
        var origin = $"http://127.0.0.1:{ReservePort()}";
        var output = new StringBuilder();
        using var process = StartApplication(FindWebAssembly(FindRepositoryRoot()), dataRoot, origin, output);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync();
            var imported = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            await page.RouteAsync("**/internal/routes/import?**", async route =>
            {
                Interlocked.Increment(ref requests);
                var response = await route.FetchAsync();
                Assert.True(response.Ok, $"The real route upload returned HTTP {response.Status}.");
                var result = JsonNode.Parse(await response.TextAsync())!.AsObject();
                var id = Guid.Parse(result["id"]!.GetValue<string>());
                result["fileCleanupPending"] = true;
                await route.FulfillAsync(new() { Response = response, ContentType = "application/json", Body = result.ToJsonString() });
                imported.TrySetResult(id);
            });
            await page.GotoAsync(origin + "/routes", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.EastOwner.ToString());
            await page.GetByLabel("Name", new() { Exact = true }).FillAsync(routeName);
            await page.GetByLabel("Choose a GPX route file", new() { Exact = true }).SetInputFilesAsync(new FilePayload
            {
                Name = "synthetic-route-contract.gpx",
                MimeType = "application/gpx+xml",
                Buffer = Encoding.UTF8.GetBytes(TestSupport.Gpx(sport: "cycling", name: "Synthetic GPX path"))
            });
            await page.GetByRole(AriaRole.Button, new() { Name = "Import GPX", Exact = true }).ClickAsync();
            var id = await imported.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assertions.Expect(page).ToHaveURLAsync($"{origin}/routes/{id}?cleanupPending=true");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = routeName, Exact = true, Level = 1 })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("The route was imported. File cleanup is pending and will be retried at startup; you can use this route without importing it again.", new() { Exact = true })).ToBeVisibleAsync();
            await using var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot));
            var saved = await db.Routes.AsNoTracking().SingleAsync(route => route.Id == id);
            Assert.Equal(routeName, saved.Name);
            Assert.Equal(seed.EastOwner, saved.OwnerId);
            Assert.Equal(1, await db.Routes.CountAsync(route => route.Name == routeName));
            Assert.Equal(1, Volatile.Read(ref requests));
        }
        catch
        {
            TestContext.Current.TestOutputHelper!.WriteLine(output.ToString());
            throw;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }
}
