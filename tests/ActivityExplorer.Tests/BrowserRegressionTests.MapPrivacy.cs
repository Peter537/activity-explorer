using System.Text;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    [Fact]
    public async Task Online_map_renders_controlled_attribution_after_explicit_opt_in()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;

        var dataRoot = TestSupport.NewDirectory();
        var origin = $"http://127.0.0.1:{ReservePort()}";
        var output = new StringBuilder();
        using var process = StartApplication(FindWebAssembly(FindRepositoryRoot()), dataRoot, origin, output);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync();
            var requestedStyles = 0;
            await page.RouteAsync("https://**/*", async route =>
            {
                if (route.Request.Url != "https://tiles.openfreemap.org/styles/liberty")
                {
                    await route.AbortAsync();
                    return;
                }

                Interlocked.Increment(ref requestedStyles);
                await route.FulfillAsync(new()
                {
                    ContentType = "application/json",
                    Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*" },
                    Body = """
                        {"version":8,"sources":{"fixture":{"type":"geojson",
                        "attribution":"Fictional map contributor",
                        "data":{"type":"Feature","properties":{},"geometry":{"type":"LineString","coordinates":[[9,50],[11,50]]}}}},
                        "layers":[{"id":"fixture-line","type":"line","source":"fixture"}]}
                        """
                });
            });

            var blank = await page.GotoAsync(origin + "/map") ?? throw new InvalidOperationException("Map did not navigate.");
            Assert.DoesNotContain("openfreemap.org", blank.Headers["content-security-policy"], StringComparison.Ordinal);
            await Assertions.Expect(page.Locator("canvas.maplibregl-canvas")).ToBeVisibleAsync();
            Assert.Equal(0, Volatile.Read(ref requestedStyles));

            await page.GotoAsync(origin + "/settings", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.RunAndWaitForResponseAsync(
                () => page.GetByRole(AriaRole.Checkbox, new() { Name = "Enable OpenFreeMap on every map" }).CheckAsync(),
                response => response.Url == origin + "/settings" && response.Request.ResourceType == "document");
            var online = await page.GotoAsync(origin + "/map") ?? throw new InvalidOperationException("Online map did not navigate.");
            Assert.Contains("https://tiles.openfreemap.org", online.Headers["content-security-policy"], StringComparison.Ordinal);
            await Assertions.Expect(page.Locator(".maplibregl-ctrl-attrib-inner")).ToContainTextAsync("Fictional map contributor");
            Assert.True(Volatile.Read(ref requestedStyles) > 0);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }
}
