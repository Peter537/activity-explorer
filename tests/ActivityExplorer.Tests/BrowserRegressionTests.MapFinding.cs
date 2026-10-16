using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    [Fact]
    public async Task Map_finding_preview_fixture_is_created_only_when_explicitly_requested()
    {
        var requestedPath = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_MAP_FINDING_PREVIEW_DATA");
        if (string.IsNullOrWhiteSpace(requestedPath)) return;
        var path = Path.GetFullPath(requestedPath);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException("The map finding preview fixture requires an empty directory.");
        Directory.CreateDirectory(path);
        var seed = await SeedMapFindingBrowserDataAsync(path);
        await File.WriteAllTextAsync(Path.Combine(path, "synthetic-fixture.json"), JsonSerializer.Serialize(seed));
        TestContext.Current.TestOutputHelper!.WriteLine($"Synthetic map finding preview data: {path}");
    }

    [Fact]
    public async Task Map_finding_reports_limits_skips_disabled_layers_and_discards_stale_results()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        await SeedMapFindingBrowserDataAsync(dataRoot);
        var origin = $"http://127.0.0.1:{ReservePort()}";
        var output = new StringBuilder();
        using var process = StartApplication(FindWebAssembly(FindRepositoryRoot()), dataRoot, origin, output);
        var releasePending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 1000 } });
            var requests = new List<string>();
            var errors = new List<string>();
            var external = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            page.Request += (_, request) =>
            {
                if (request.Url.Contains("/internal/map/", StringComparison.Ordinal)) requests.Add(request.Url);
                if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
                    uri.GetLeftPart(UriPartial.Authority) != origin) external.Add(request.Url);
            };
            var mode = "capped";
            var pendingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await page.RouteAsync("**/internal/map/activities?**", async route =>
            {
                if (mode == "pending")
                {
                    pendingStarted.TrySetResult();
                    await releasePending.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    try { await route.FulfillAsync(new() { ContentType = "application/json", Body = CappedMapLines("activity", 2000) }); }
                    catch (PlaywrightException) when (route.Request.Failure is not null) { }
                }
                else if (mode == "failed") await route.FulfillAsync(new() { Status = 503, Body = "{}" });
                else if (mode == "capped") await route.FulfillAsync(new() { ContentType = "application/json", Body = CappedMapLines("activity", 2000) });
                else await route.ContinueAsync();
            });
            await page.RouteAsync("**/internal/map/routes?**", route => mode == "capped"
                ? route.FulfillAsync(new() { ContentType = "application/json", Body = CappedMapLines("route", 1000) }) : route.ContinueAsync());
            await page.RouteAsync("**/internal/map/segments?**", route => mode == "capped"
                ? route.FulfillAsync(new() { ContentType = "application/json", Body = CappedMapLines("segment", 1000) }) : route.ContinueAsync());
            await page.GotoAsync(origin + "/map", new() { WaitUntil = WaitUntilState.NetworkIdle });
            var layers = page.GetByRole(AriaRole.Group, new() { Name = "Lines layers", Exact = true });
            var activities = layers.GetByRole(AriaRole.Checkbox, new() { Name = "Activities", Exact = true });
            var routes = layers.GetByRole(AriaRole.Checkbox, new() { Name = "Routes", Exact = true });
            var segments = layers.GetByRole(AriaRole.Checkbox, new() { Name = "Segments", Exact = true });
            var canvas = page.Locator(".world-map .maplibregl-canvas");
            await Assertions.Expect(canvas).ToHaveAttributeAsync("data-activities-features", "2000");
            await Assertions.Expect(canvas).ToHaveAttributeAsync("data-routes-features", "1000");
            await Assertions.Expect(canvas).ToHaveAttributeAsync("data-segments-features", "1000");
            await Assertions.Expect(page.Locator("[data-map-line-layer='activities']")).ToContainTextAsync($"{2000:N0} of {2001:N0}");
            await Assertions.Expect(page.Locator("[data-map-line-layer='activities']")).ToContainTextAsync("Narrow the activity dates or zoom in");
            await Assertions.Expect(page.Locator("[data-map-line-layer='routes']")).ToContainTextAsync($"{1000:N0} of {1001:N0}");
            await Assertions.Expect(page.Locator("[data-map-line-layer='segments']")).ToContainTextAsync($"{1000:N0} of {1001:N0}");
            await AssertMapFindingResponsiveAsync(page, "line-layer-limits");

            var routeRequests = requests.Count(url => url.Contains("/internal/map/routes?", StringComparison.Ordinal));
            await routes.UncheckAsync();
            await Assertions.Expect(canvas).ToHaveAttributeAsync("data-routes-features", "0");
            await Assertions.Expect(page.Locator("[data-map-line-layer='routes']")).ToHaveCountAsync(0);
            Assert.Equal(routeRequests, requests.Count(url => url.Contains("/internal/map/routes?", StringComparison.Ordinal)));
            await segments.UncheckAsync();
            await activities.UncheckAsync();
            await Assertions.Expect(page.GetByText("All line layers are hidden.", new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]layers=none(?:&|$)"));
            await Assertions.Expect(canvas).ToHaveAttributeAsync("data-activities-features", "0");
            await Assertions.Expect(canvas).ToHaveAttributeAsync("data-segments-features", "0");
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(activities).Not.ToBeCheckedAsync();
            await Assertions.Expect(routes).Not.ToBeCheckedAsync();
            await Assertions.Expect(segments).Not.ToBeCheckedAsync();
            var allOffRequestCount = requests.Count;
            await canvas.FocusAsync();
            await canvas.PressAsync("ArrowRight");
            await Assertions.Expect(page.GetByText("All line layers are hidden.", new() { Exact = true })).ToBeVisibleAsync();
            Assert.Equal(allOffRequestCount, requests.Count);

            mode = "pending";
            await activities.CheckAsync();
            await pendingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await activities.UncheckAsync();
            releasePending.TrySetResult();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Assertions.Expect(canvas).ToHaveAttributeAsync("data-activities-features", "0");
            await Assertions.Expect(page.GetByText("All line layers are hidden.", new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("[data-map-line-layer='activities']")).ToHaveCountAsync(0);

            mode = "failed";
            await activities.CheckAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Retry map lines", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(canvas).ToHaveAttributeAsync("data-activities-features", "0");
            mode = "normal";
            await page.GetByRole(AriaRole.Button, new() { Name = "Retry map lines", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Retry map lines", Exact = true })).ToHaveCountAsync(0);
            await Assertions.Expect(canvas).ToHaveAttributeAsync("data-activities-features", new Regex("^[1-9][0-9]*$"));
            await Assertions.Expect(routes).Not.ToBeCheckedAsync();
            await Assertions.Expect(segments).Not.ToBeCheckedAsync();
            Assert.Empty(external);
            Assert.Empty(errors);
        }
        catch
        {
            TestContext.Current.TestOutputHelper!.WriteLine(output.ToString());
            throw;
        }
        finally
        {
            releasePending.TrySetResult();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    [Fact]
    public async Task Map_finding_filters_new_cells_without_changing_discovery_totals_and_restores_world_camera()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        await SeedMapFindingBrowserDataAsync(dataRoot);
        var origin = $"http://127.0.0.1:{ReservePort()}";
        var output = new StringBuilder();
        using var process = StartApplication(FindWebAssembly(FindRepositoryRoot()), dataRoot, origin, output);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 1000 } });
            var errors = new List<string>();
            var external = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            page.Request += (_, request) =>
            {
                if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
                    uri.GetLeftPart(UriPartial.Authority) != origin) external.Add(request.Url);
            };
            await page.GotoAsync(origin + "/map?view=exploration&from=2026-04-01&to=2026-04-30", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await AssertExplorationCompleteAsync(page);
            var totals = await page.Locator(".exploration-totals dd").AllTextContentsAsync();
            var visited = int.Parse(totals[0], NumberStyles.Number, CultureInfo.CurrentCulture);
            var discovered = int.Parse(totals[1], NumberStyles.Number, CultureInfo.CurrentCulture);
            Assert.True(visited > discovered && discovered > 0, "The fixture must contain familiar and newly discovered cells.");
            var firstCellHref = await page.Locator(".cell-list a[href*='cell=']").First.GetAttributeAsync("href");
            Assert.NotNull(firstCellHref);
            var cellId = new Uri(new Uri(origin), firstCellHref).Query.TrimStart('?').Split('&')
                .Single(part => part.StartsWith("cell=", StringComparison.Ordinal)).Split('=')[1];
            await page.GotoAsync(origin + $"/map?view=exploration&from=2026-04-01&to=2026-04-30&page=2&activityPage=2&cell={cellId}",
                new() { WaitUntil = WaitUntilState.NetworkIdle });
            await AssertExplorationCompleteAsync(page);
            await Assertions.Expect(page.Locator(".cell-list .pager")).ToContainTextAsync("Page 2");
            var shown = page.GetByRole(AriaRole.Combobox, new() { Name = "Cells shown", Exact = true });
            var filteredResponse = await page.RunAndWaitForResponseAsync(
                () => shown.SelectOptionAsync(new SelectOptionValue { Label = "New in period" }),
                response => response.Url.Contains("/internal/map/exploration?", StringComparison.Ordinal) &&
                    response.Url.Contains("newOnly=yes", StringComparison.Ordinal) && response.Status == 200);
            await AssertExplorationCompleteAsync(page);
            await Assertions.Expect(page.Locator(".exploration-totals dd")).ToHaveTextAsync(totals);
            await Assertions.Expect(page.Locator(".cell-list tbody tr")).ToHaveCountAsync(Math.Min(50, discovered));
            await Assertions.Expect(page.Locator(".cell-list tbody tr th small")).ToHaveTextAsync(Enumerable.Repeat("New", Math.Min(50, discovered)));
            await Assertions.Expect(page.Locator(".cell-list .pager")).ToContainTextAsync($"Page 1 of {Math.Max(1, (int)Math.Ceiling(discovered / 50d))}");
            await Assertions.Expect(page.Locator(".cell-detail")).ToHaveCountAsync(0);
            Assert.DoesNotContain("cell=", page.Url, StringComparison.Ordinal);
            Assert.DoesNotContain("page=", page.Url, StringComparison.Ordinal);
            Assert.DoesNotContain("activityPage=", page.Url, StringComparison.Ordinal);
            using (var payload = JsonDocument.Parse(await filteredResponse.TextAsync()))
            {
                var cells = payload.RootElement.GetProperty("cells").EnumerateArray().ToArray();
                Assert.NotEmpty(cells);
                Assert.All(cells, cell => Assert.Equal(cell.GetProperty("visitedCells").GetInt32(), cell.GetProperty("newCells").GetInt32()));
            }
            await page.Locator(".cell-list a[href*='cell=']").First.ClickAsync();
            var showCell = page.GetByRole(AriaRole.Button, new() { Name = "Show on map", Exact = true });
            await showCell.ClickAsync();
            var canvas = page.Locator(".world-map .maplibregl-canvas");
            var selectedCell = new Uri(page.Url).Query.TrimStart('?').Split('&').Single(part => part.StartsWith("cell=", StringComparison.Ordinal)).Split('=')[1];
            await Assertions.Expect(canvas).ToHaveAttributeAsync("data-focused-cell", selectedCell);
            var focusedCenter = await canvas.GetAttributeAsync("data-camera-center");
            Assert.NotNull(focusedCenter);
            await PanMapFindingCanvasAsync(page, canvas);
            var pannedCenter = await canvas.GetAttributeAsync("data-camera-center");
            var pannedZoom = await canvas.GetAttributeAsync("data-camera-zoom");
            Assert.NotEqual(focusedCenter, pannedCenter);
            var worldCamera = await page.EvaluateAsync<string?>("sessionStorage.getItem('activity-explorer:camera:world-map')");
            Assert.NotNull(worldCamera);
            var selectedCellUrl = page.Url;
            var contributor = page.Locator(".cell-contributors a[href^='/activities/']").First;
            var activityTitle = await contributor.InnerTextAsync();
            await contributor.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/activities/[0-9a-f-]+$"));
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = activityTitle, Exact = true })).ToBeVisibleAsync();
            var detailCanvas = page.Locator("#activity-map .maplibregl-canvas");
            await Assertions.Expect(detailCanvas).ToBeVisibleAsync();
            await PanMapFindingCanvasAsync(page, detailCanvas);
            Assert.Equal(worldCamera, await page.EvaluateAsync<string?>("sessionStorage.getItem('activity-explorer:camera:world-map')"));
            await page.GoBackAsync();
            await Assertions.Expect(page).ToHaveURLAsync(selectedCellUrl);
            await AssertExplorationCompleteAsync(page);
            await Assertions.Expect(canvas).ToHaveAttributeAsync("data-camera-center", pannedCenter!);
            await Assertions.Expect(canvas).ToHaveAttributeAsync("data-camera-zoom", pannedZoom!);
            await Assertions.Expect(shown).ToHaveValueAsync("new");
            await page.GetByRole(AriaRole.Link, new() { Name = "Cumulative", Exact = true }).ClickAsync();
            await AssertExplorationCompleteAsync(page);
            await page.GetByLabel("Through reporting date", new() { Exact = true }).FillAsync("2026-04-01");
            await page.GetByLabel("Through reporting date", new() { Exact = true }).PressAsync("Tab");
            await AssertExplorationCompleteAsync(page);
            await Assertions.Expect(shown.Locator("option")).ToContainTextAsync(["All visited", "New on this date"]);
            var cumulativeTotals = await page.Locator(".exploration-totals dd").AllTextContentsAsync();
            var newOnDate = int.Parse(cumulativeTotals[1], NumberStyles.Number, CultureInfo.CurrentCulture);
            await Assertions.Expect(page.Locator(".cell-list tbody tr")).ToHaveCountAsync(Math.Min(50, newOnDate));
            await shown.SelectOptionAsync(new SelectOptionValue { Label = "All visited" });
            await AssertExplorationCompleteAsync(page);
            await Assertions.Expect(page.Locator(".exploration-totals dd")).ToHaveTextAsync(cumulativeTotals);
            await AssertMapFindingResponsiveAsync(page, "new-cell-finding");
            Assert.Empty(external);
            Assert.Empty(errors);
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

    private static async Task PanMapFindingCanvasAsync(IPage page, ILocator canvas)
    {
        await canvas.ScrollIntoViewIfNeededAsync();
        var box = await canvas.BoundingBoxAsync();
        Assert.NotNull(box);
        var previous = await canvas.GetAttributeAsync("data-camera-center");
        await page.Mouse.MoveAsync(box.X + box.Width * .5f, box.Y + box.Height * .5f);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(box.X + box.Width * .6f, box.Y + box.Height * .5f, new() { Steps = 8 });
        await page.Mouse.UpAsync();
        await Assertions.Expect(canvas).Not.ToHaveAttributeAsync("data-camera-center", previous ?? "");
    }

    private static async Task AssertMapFindingResponsiveAsync(IPage page, string name)
    {
        var captures = Path.Combine(FindRepositoryRoot(), "artifacts", "map-finding-ui");
        Directory.CreateDirectory(captures);
        foreach (var width in new[] { 320, 360, 375, 768, 1121, 1280, 1920 })
        {
            await page.SetViewportSizeAsync(width, 1000);
            await page.Locator(".sidebar").EvaluateAsync("element => Promise.all(element.getAnimations().map(animation => animation.finished))");
            await AssertNoDocumentOverflowAsync(page, $"{name} at {width}px");
            if (width is 375 or 1280)
                await page.ScreenshotAsync(new() { Path = Path.Combine(captures, $"{name}-{width}.png"), FullPage = true });
        }
        await page.SetViewportSizeAsync(1280, 1000);
        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        await page.EvaluateAsync("document.documentElement.style.fontSize='200%'; document.body.style.fontSize='30px'");
        await AssertNoDocumentOverflowAsync(page, $"{name} with enlarged text");
        await page.EvaluateAsync("document.documentElement.style.removeProperty('font-size'); document.body.style.removeProperty('font-size')");
    }

    private static readonly double[][] MapFindingLineCoordinates = [[12.30, 55.65], [12.31, 55.66]];

    private static string CappedMapLines(string kind, int limit) => JsonSerializer.Serialize(new
    {
        type = "FeatureCollection",
        features = Enumerable.Range(0, limit).Select(index => new
        {
            type = "Feature",
            geometry = new { type = "LineString", coordinates = MapFindingLineCoordinates },
            properties = new { id = Guid.NewGuid(), kind, title = $"Synthetic {kind} line {index}", sport = "Cycling" }
        }),
        coverage = new { returnedCount = limit, matchingCount = limit + 1, limit, truncated = true }
    });

    private static async Task<MapFindingBrowserSeed> SeedMapFindingBrowserDataAsync(string dataRoot)
    {
        var seed = await SeedExplorationBrowserDataAsync(dataRoot);
        await using var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot));
        var points = TestSupport.Track(3).Select(point => point with { Latitude = point.Latitude + 54.65, Longitude = point.Longitude + 42.30 }).ToArray();
        var route = BrowserRoute(seed.EastOwner, "Map finding route", points);
        var segment = new Segment
        {
            OwnerId = seed.EastOwner,
            Name = "Map finding segment",
            Sport = SportKind.Cycling,
            SourceKind = SegmentSourceKind.Drawn,
            DistanceMeters = route.DistanceMeters,
            GeometryWkb = route.GeometryWkb,
            MinLatitude = route.MinLatitude,
            MinLongitude = route.MinLongitude,
            MaxLatitude = route.MaxLatitude,
            MaxLongitude = route.MaxLongitude
        };
        db.AddRange(route, segment);
        await db.SaveChangesAsync();
        return new(seed.EastOwner, seed.WestOwner, seed.EmptyOwner, seed.FirstActivity, route.Id, segment.Id,
            "/map", "/map?view=exploration&from=2026-04-01&to=2026-04-30");
    }

    private sealed record MapFindingBrowserSeed(Guid EastOwner, Guid WestOwner, Guid EmptyOwner,
        Guid FirstActivity, Guid RouteId, Guid SegmentId, string LinesUrl, string NewDiscoveriesUrl);
}
