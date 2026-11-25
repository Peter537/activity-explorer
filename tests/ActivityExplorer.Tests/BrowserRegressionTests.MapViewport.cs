using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    [Fact]
    public async Task Map_viewport_follows_accepted_pans_and_offers_keyboard_details_for_dense_results()
    {
        TestSupport.RequireBrowser();
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedMapFindingBrowserDataAsync(dataRoot);
        const int vertices = 200_000;
        var points = Enumerable.Range(0, vertices).Select(index =>
        {
            var fraction = (double)index / (vertices - 1);
            return new TrackPoint(null, 55.65 + Math.Sin(fraction * 2 * Math.PI) * .005, 12.30 + fraction * .04,
                null, null, null, null, null, null, null);
        }).ToArray();
        await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
        {
            db.Routes.Add(BrowserRoute(seed.EastOwner, "Dense viewport route", points));
            await db.SaveChangesAsync();
        }

        var origin = $"http://127.0.0.1:{ReservePort()}";
        var output = new StringBuilder();
        using var process = StartApplication(FindWebAssembly(FindRepositoryRoot()), dataRoot, origin, output);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var caughtOldRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRequestFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 1000 } });
            await page.AddInitScriptAsync("""
                sessionStorage.setItem('activity-explorer:camera:world-map',
                  JSON.stringify({ center: [12.32, 55.65], zoom: 8, bearing: 0, pitch: 0 }));
                """);
            var holdNext = 0;
            await page.RouteAsync("**/internal/map/routes?**", async route =>
            {
                if (Interlocked.Exchange(ref holdNext, 0) == 0)
                {
                    await route.ContinueAsync();
                    return;
                }
                caughtOldRequest.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var old = JsonSerializer.Serialize(new
                {
                    type = "FeatureCollection",
                    features = new[] { new
                    {
                        type = "Feature",
                        geometry = new { type = "LineString", coordinates = MapFindingLineCoordinates },
                        properties = new { id = Guid.NewGuid(), kind = "route", title = "Superseded viewport route" }
                    } }
                });
                try { await route.FulfillAsync(new() { ContentType = "application/json", Body = old }); }
                catch (PlaywrightException) when (route.Request.Failure is not null) { }
                finally { oldRequestFinished.TrySetResult(); }
            });
            var firstResponse = await page.RunAndWaitForResponseAsync(() => page.GotoAsync(origin + "/map?layers=routes"), RouteResponse);
            var bytes = (await firstResponse.BodyAsync()).Length;
            Assert.InRange(bytes, 1, 20_000);
            var state = page.GetByRole(AriaRole.Region, new() { Name = "Viewport results" });
            var canvas = page.Locator(".world-map .maplibregl-canvas");
            var link = state.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("^Map finding route") });
            var dense = state.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("^Dense viewport route") });
            await Assertions.Expect(state).ToHaveAttributeAsync("aria-busy", "false");
            await Assertions.Expect(dense).ToBeVisibleAsync();
            await Assertions.Expect(canvas).ToHaveAttributeAsync("data-routes-features", "2");
            await link.FocusAsync();
            await page.EvaluateAsync("() => window.activityExplorerMap.reload(document.querySelector('.world-map').id)");
            await Assertions.Expect(link).ToBeFocusedAsync();

            Interlocked.Exchange(ref holdNext, 1);
            await canvas.FocusAsync();
            await canvas.PressAsync("ArrowRight");
            await caughtOldRequest.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var started = Stopwatch.GetTimestamp();
            var secondResponse = await page.RunAndWaitForResponseAsync(() => canvas.PressAsync("ArrowLeft"), RouteResponse);
            await Assertions.Expect(state).ToHaveAttributeAsync("aria-busy", "false");
            await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
            TestContext.Current.TestOutputHelper!.WriteLine($"Dense route {vertices:N0} vertices. First viewport {bytes:N0} bytes, repeated pan {(await secondResponse.BodyAsync()).Length:N0} bytes. Keyboard pan to accepted map data plus two frames {Stopwatch.GetElapsedTime(started).TotalMilliseconds:N1} ms.");
            release.TrySetResult();
            await oldRequestFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assertions.Expect(state.GetByText("Superseded viewport route")).ToHaveCountAsync(0);
            await Assertions.Expect(canvas).ToHaveAttributeAsync("data-routes-features", "2");
            await Assertions.Expect(link).ToBeVisibleAsync();

            var checkedRoutes = page.GetByRole(AriaRole.Checkbox, new() { Name = "Routes", Exact = true });
            await checkedRoutes.FocusAsync();
            await checkedRoutes.PressAsync("Space");
            await Assertions.Expect(canvas).ToHaveAttributeAsync("data-routes-features", "0");
            await Assertions.Expect(state.GetByRole(AriaRole.Link)).ToHaveCountAsync(0);
            await checkedRoutes.PressAsync("Space");
            await Assertions.Expect(link).ToBeVisibleAsync();
            await link.FocusAsync();
            await link.PressAsync("Enter");
            await Assertions.Expect(page).ToHaveURLAsync($"{origin}/routes/{seed.RouteId}");
        }
        catch
        {
            TestContext.Current.TestOutputHelper!.WriteLine(output.ToString());
            throw;
        }
        finally
        {
            release.TrySetResult();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }

        static bool RouteResponse(IResponse response) => response.Status == 200 &&
            response.Url.Contains("/internal/map/routes?", StringComparison.Ordinal);
    }
}
