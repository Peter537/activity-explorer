using System.Globalization;
using System.Security.Cryptography;
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
    public async Task Exploration_preview_fixture_is_created_only_when_explicitly_requested()
    {
        var requestedPath = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_EXPLORATION_PREVIEW_DATA");
        if (string.IsNullOrWhiteSpace(requestedPath)) return;
        var path = Path.GetFullPath(requestedPath);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException("The exploration preview fixture requires an empty directory.");
        Directory.CreateDirectory(path);
        var seed = await SeedExplorationBrowserDataAsync(path);
        await File.WriteAllTextAsync(Path.Combine(path, "synthetic-fixture.json"), JsonSerializer.Serialize(seed));
        TestContext.Current.TestOutputHelper!.WriteLine($"Synthetic exploration preview data: {path}");
    }

    [Fact]
    public async Task Exploration_map_indexes_all_history_and_preserves_views_dates_selection_and_blank_privacy()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedExplorationBrowserDataAsync(dataRoot);
        var origin = $"http://127.0.0.1:{ReservePort()}";
        var output = new StringBuilder();
        using var process = StartApplication(FindWebAssembly(FindRepositoryRoot()), dataRoot, origin, output);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync(new() { HasTouch = true, ViewportSize = new() { Width = 1280, Height = 1000 } });
            var errors = new List<string>();
            var warnings = new List<string>();
            var externalRequests = new List<string>();
            var mapRequests = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            page.Console += (_, message) =>
            {
                if (message.Type == "error") errors.Add(message.Text);
                if (message.Type == "warning") warnings.Add(message.Text);
            };
            page.Request += (_, request) =>
            {
                if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
                    uri.GetLeftPart(UriPartial.Authority) != origin) externalRequests.Add(request.Url);
                if (request.Url.Contains("/internal/map/exploration?", StringComparison.Ordinal)) mapRequests.Add(request.Url);
            };
            var navigation = new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle };
            await page.GotoAsync(origin + "/map", navigation);
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Lines", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".exploration-totals")).ToHaveCountAsync(0);
            await page.GetByRole(AriaRole.Link, new() { Name = "Frequency", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Cancel calculation", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByText("Calculation cancelled.", new() { Exact = false })).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Resume calculation", Exact = true }).ClickAsync();
            await AssertExplorationCompleteAsync(page);
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]view=frequency(?:&|$)"));
            await Assertions.Expect(page.Locator(".exploration-totals")).ToContainTextAsync("64");
            await Assertions.Expect(page.GetByText("Limited coverage", new() { Exact = false })).ToBeVisibleAsync();
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                Assert.Equal(64, await db.ActivityExplorationIndexes.CountAsync());
                Assert.Equal(1, await db.ActivityExplorationIndexes.CountAsync(index => index.IsLimited));
                Assert.True(await db.ActivityExplorationCells.Select(cell => cell.CellId).Distinct().CountAsync() > 100);
            }
            using (var filteredCells = JsonDocument.Parse(await (await page.APIRequest.GetAsync(
                       origin + "/internal/map/exploration?view=frequency&from=2026-04-01&to=2026-04-30&zoom=14")).TextAsync()))
                Assert.Equal(30, filteredCells.RootElement.GetProperty("summary").GetProperty("matchingActivities").GetInt32());
            var frequencyTotals = await page.Locator(".exploration-totals dd").AllTextContentsAsync();
            var canvas = page.Locator(".maplibregl-canvas");
            await Assertions.Expect(canvas).ToBeVisibleAsync();
            try { await Assertions.Expect(canvas).ToHaveAttributeAsync("data-exploration-features", new Regex("^[1-9][0-9]*$")); }
            catch (PlaywrightException)
            {
                TestContext.Current.TestOutputHelper!.WriteLine($"Map requests: {string.Join(Environment.NewLine, mapRequests)}");
                var mapStatus = await page.Locator(".map-load-status").AllTextContentsAsync();
                TestContext.Current.TestOutputHelper!.WriteLine($"Map status: {string.Join(" ", mapStatus)}");
                TestContext.Current.TestOutputHelper!.WriteLine($"Browser warnings: {string.Join(Environment.NewLine, warnings)}");
                TestContext.Current.TestOutputHelper!.WriteLine($"Browser errors: {string.Join(Environment.NewLine, errors)}");
                throw;
            }
            await canvas.ScrollIntoViewIfNeededAsync();
            var mapBox = await canvas.BoundingBoxAsync();
            Assert.NotNull(mapBox);
            var pannedRequest = await page.RunAndWaitForRequestAsync(async () =>
            {
                await page.Mouse.MoveAsync(mapBox.X + mapBox.Width * .5f, mapBox.Y + mapBox.Height * .5f);
                await page.Mouse.DownAsync();
                await page.Mouse.MoveAsync(mapBox.X + mapBox.Width * .6f, mapBox.Y + mapBox.Height * .5f, new() { Steps = 8 });
                await page.Mouse.UpAsync();
            }, request => request.Url.Contains("/internal/map/exploration?", StringComparison.Ordinal));
            await Assertions.Expect(page.Locator(".exploration-totals dd")).ToHaveTextAsync(frequencyTotals);
            var mapId = await page.Locator(".world-map").GetAttributeAsync("id");
            var switchedRequest = await page.RunAndWaitForRequestAsync(() =>
                page.GetByRole(AriaRole.Link, new() { Name = "Exploration", Exact = true }).ClickAsync(),
                request => request.Url.Contains("/internal/map/exploration?", StringComparison.Ordinal) && request.Url.Contains("view=exploration", StringComparison.Ordinal));
            await AssertExplorationCompleteAsync(page);
            Assert.Equal(ExplorationViewportParameters(pannedRequest.Url), ExplorationViewportParameters(switchedRequest.Url));
            await Assertions.Expect(page.Locator(".world-map")).ToHaveAttributeAsync("id", mapId!);
            await AssertExplorationMapSelectionAsync(page, switchedRequest.Url);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Monthly first discoveries", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".discovery-history tbody tr")).ToHaveCountAsync(12);
            await page.GetByRole(AriaRole.Button, new() { Name = "Earlier year", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]month=2025-05(?:&|$)"));
            await Assertions.Expect(page.Locator(".discovery-history tbody td")).ToHaveTextAsync(Enumerable.Repeat("0", 12));
            await page.GetByRole(AriaRole.Button, new() { Name = "Later year", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]month=2026-05(?:&|$)"));
            await Assertions.Expect(page.Locator(".discovery-history tbody td").Last).ToHaveTextAsync("4");
            await page.GetByRole(AriaRole.Button, new() { Name = "Next cells", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]page=2(?:&|$)"));
            await page.GetByRole(AriaRole.Button, new() { Name = "Previous cells", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]page=1(?:&|$)"));
            await Assertions.Expect(page.Locator(".cell-list .pager")).ToContainTextAsync("Page 1 of 3");
            var cellLinks = page.Locator("a[href*='cell=']");
            await Assertions.Expect(cellLinks.First).ToBeVisibleAsync();
            var firstCellHref = await cellLinks.First.GetAttributeAsync("href");
            await cellLinks.First.TapAsync();
            Assert.NotNull(firstCellHref);
            var selectedCellUrl = new Uri(new Uri(origin), firstCellHref).AbsoluteUri;
            await Assertions.Expect(page).ToHaveURLAsync(selectedCellUrl);
            await Assertions.Expect(page.Locator(".world-map")).ToHaveAttributeAsync("id", mapId!);
            await Assertions.Expect(page.Locator("a[href^='/activities/']").First).ToBeVisibleAsync();
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page).ToHaveURLAsync(selectedCellUrl);
            await Assertions.Expect(page.Locator("a[href^='/activities/']").First).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "Cumulative", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByLabel("Through reporting date", new() { Exact = true })).ToHaveValueAsync("2026-05-03");
            await page.GetByLabel("Through reporting date", new() { Exact = true }).FillAsync("2026-04-01");
            await page.GetByLabel("Through reporting date", new() { Exact = true }).PressAsync("Tab");
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]through=2026-04-01(?:&|$)"));
            var slider = page.GetByRole(AriaRole.Slider, new() { Name = "Cumulative exploration", Exact = true });
            await slider.FocusAsync();
            await slider.PressAsync("ArrowRight");
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]through=2026-04-02(?:&|$)"));
            await Assertions.Expect(slider).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("ArrowRight");
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]through=2026-04-03(?:&|$)"));
            await Assertions.Expect(slider).ToBeFocusedAsync();
            await page.GoBackAsync();
            await Assertions.Expect(page.GetByLabel("Through reporting date", new() { Exact = true })).ToHaveValueAsync("2026-04-02");
            await page.GoForwardAsync();
            await Assertions.Expect(page.GetByLabel("Through reporting date", new() { Exact = true })).ToHaveValueAsync("2026-04-03");
            await page.GetByRole(AriaRole.Link, new() { Name = "Range", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true }).SelectOptionAsync("custom");
            await page.GetByLabel("From", new() { Exact = true }).FillAsync("2026-04-01");
            await page.GetByLabel("To", new() { Exact = true }).FillAsync("2026-04-30");
            await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters", Exact = true }).ClickAsync();
            await AssertExplorationCompleteAsync(page);
            await Assertions.Expect(page.Locator(".exploration-totals")).ToContainTextAsync("30");
            await page.GetByRole(AriaRole.Link, new() { Name = "Cumulative", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByLabel("Through reporting date", new() { Exact = true })).ToHaveValueAsync("2026-04-03");
            await page.GetByRole(AriaRole.Link, new() { Name = "Range", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByLabel("From", new() { Exact = true })).ToHaveValueAsync("2026-04-01");
            await Assertions.Expect(page.GetByLabel("To", new() { Exact = true })).ToHaveValueAsync("2026-04-30");
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.EmptyOwner.ToString());
            await AssertExplorationCompleteAsync(page);
            await Assertions.Expect(page.Locator(".exploration-totals dd").First).ToHaveTextAsync("0");
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.EastOwner.ToString());
            await AssertExplorationCompleteAsync(page);
            await Assertions.Expect(page.GetByText("Limited coverage", new() { Exact = false })).ToHaveCountAsync(0);
            await page.GetByLabel("Profile selector").EvaluateAsync("""
                (select, owners) => {
                    for (const owner of owners) {
                        select.value = owner;
                        select.dispatchEvent(new Event('change', { bubbles: true }));
                    }
                }
                """, new[] { seed.EmptyOwner.ToString(), seed.EastOwner.ToString(), seed.WestOwner.ToString() });
            await AssertExplorationCompleteAsync(page);
            await Assertions.Expect(page.GetByLabel("Profile selector")).ToHaveValueAsync(seed.WestOwner.ToString());
            await Assertions.Expect(page.Locator(".exploration-totals dd").Nth(2)).ToHaveTextAsync("13");
            await page.GetByLabel("Profile selector").SelectOptionAsync("");
            await page.GotoAsync(origin + "/map?view=exploration", navigation);
            await AssertExplorationCompleteAsync(page);
            await AssertExplorationResponsiveAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Recheck limited activities", Exact = true }).ClickAsync();
            await AssertExplorationCompleteAsync(page);
            await Assertions.Expect(page.GetByText("Limited coverage", new() { Exact = false })).ToBeVisibleAsync();

            foreach (var invalid in new[] { "view=unknown", "view=exploration&coverage=unknown", "view=exploration&coverage=cumulative&through=2020-01-01", "view=exploration&cell=-1", "view=exploration&page=0" })
            {
                await page.GotoAsync(origin + "/map?" + invalid, navigation);
                await Assertions.Expect(page.GetByRole(AriaRole.Alert).First).ToBeVisibleAsync();
            }
            await page.GotoAsync(origin + "/map?view=frequency", navigation);
            await AssertExplorationCompleteAsync(page);
            byte[] validPayload;
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                var stream = await db.ActivityStreams.SingleAsync(stream => stream.ActivityId == seed.FirstActivity);
                validPayload = stream.CompressedPayload;
                stream.CompressedPayload = [1, 2, 3];
                (await db.Activities.SingleAsync(activity => activity.Id == seed.FirstActivity)).ExplorationInputVersion++;
                await db.SaveChangesAsync();
            }
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator(".exploration-error")).ToBeVisibleAsync();
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                var activity = await db.Activities.SingleAsync(activity => activity.Id == seed.FirstActivity);
                var index = await db.ActivityExplorationIndexes.SingleAsync(index => index.ActivityId == seed.FirstActivity);
                Assert.True(index.InputVersion < activity.ExplorationInputVersion);
                (await db.ActivityStreams.SingleAsync(stream => stream.ActivityId == seed.FirstActivity)).CompressedPayload = validPayload;
                activity.ExplorationInputVersion++;
                await db.SaveChangesAsync();
            }
            await page.GetByRole(AriaRole.Button, new() { Name = "Retry calculation", Exact = true }).ClickAsync();
            await AssertExplorationCompleteAsync(page);
            await Assertions.Expect(page.Locator(".exploration-error")).ToHaveCountAsync(0);
            Assert.NotEmpty(mapRequests);
            Assert.Empty(externalRequests);
            Assert.Empty(errors);
            Assert.DoesNotContain("Unhandled exception", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    private static async Task AssertExplorationCompleteAsync(IPage page)
    {
        await Assertions.Expect(page.GetByText("Calculation complete", new() { Exact = false })).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Assertions.Expect(page.Locator(".exploration-totals")).ToBeVisibleAsync();
    }

    private static string ExplorationViewportParameters(string url) => string.Join('&', new Uri(url).Query.TrimStart('?').Split('&')
        .Where(part => part.StartsWith("west=", StringComparison.Ordinal) || part.StartsWith("east=", StringComparison.Ordinal) ||
            part.StartsWith("north=", StringComparison.Ordinal) || part.StartsWith("south=", StringComparison.Ordinal) ||
            part.StartsWith("zoom=", StringComparison.Ordinal)).Order(StringComparer.Ordinal));

    private static async Task AssertExplorationMapSelectionAsync(IPage page, string viewportUrl)
    {
        var canvas = page.Locator(".maplibregl-canvas");
        for (var step = 0; step < 6; step++)
        {
            using var response = JsonDocument.Parse(await (await page.APIRequest.GetAsync(viewportUrl)).TextAsync());
            var cell = response.RootElement.GetProperty("cells")[0];
            var zoom = response.RootElement.GetProperty("renderZoom").GetInt32();
            await Assertions.Expect(canvas).ToHaveAttributeAsync("data-exploration-render-zoom", zoom.ToString(CultureInfo.InvariantCulture));
            var bounds = new Uri(viewportUrl).Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
                .Where(pair => pair[0] is "west" or "east" or "south" or "north")
                .ToDictionary(pair => pair[0], pair => double.Parse(Uri.UnescapeDataString(pair[1]), CultureInfo.InvariantCulture));
            var west = Math.Max(bounds["west"], cell.GetProperty("west").GetDouble());
            var east = Math.Min(bounds["east"], cell.GetProperty("east").GetDouble());
            var south = Math.Max(bounds["south"], cell.GetProperty("south").GetDouble());
            var north = Math.Min(bounds["north"], cell.GetProperty("north").GetDouble());
            var x = ((west + east) / 2 - bounds["west"]) / (bounds["east"] - bounds["west"]);
            var y = (ExplorationMercator(bounds["north"]) - (ExplorationMercator(north) + ExplorationMercator(south)) / 2) /
                (ExplorationMercator(bounds["north"]) - ExplorationMercator(bounds["south"]));
            await canvas.ScrollIntoViewIfNeededAsync();
            var box = await canvas.BoundingBoxAsync();
            Assert.NotNull(box);
            if (zoom < 14)
            {
                var request = await page.RunAndWaitForRequestAsync(() => page.Mouse.ClickAsync(box.X + (float)x * box.Width, box.Y + (float)y * box.Height),
                    request => request.Url.Contains("/internal/map/exploration?", StringComparison.Ordinal));
                await Assertions.Expect(page.Locator(".maplibregl-popup-content")).ToContainTextAsync("Highest frequency:");
                await page.Locator(".maplibregl-popup-close-button").ClickAsync();
                viewportUrl = request.Url;
                continue;
            }
            var cellId = cell.GetProperty("y").GetInt32() * 16384 + cell.GetProperty("x").GetInt32();
            await page.Mouse.ClickAsync(box.X + (float)x * box.Width, box.Y + (float)y * box.Height);
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"[?&]cell={cellId}(?:&|$)"));
            await Assertions.Expect(page.Locator(".cell-detail")).ToContainTextAsync($"{cell.GetProperty("activityCount").GetInt32()} matching activities");
            return;
        }
        Assert.Fail("Grouped exploration polygons did not reach exact selectable cells after six zoom steps.");
    }

    private static double ExplorationMercator(double latitude) => Math.Log(Math.Tan(Math.PI / 4 + latitude * Math.PI / 360));

    private static async Task AssertExplorationResponsiveAsync(IPage page)
    {
        var captures = Path.Combine(FindRepositoryRoot(), "artifacts", "exploration-ui");
        Directory.CreateDirectory(captures);
        foreach (var width in new[] { 320, 360, 375, 768, 1121, 1280, 1920 })
        {
            await page.SetViewportSizeAsync(width, 1000);
            await page.Locator(".sidebar").EvaluateAsync("element => Promise.all(element.getAnimations().map(animation => animation.finished))");
            await AssertNoDocumentOverflowAsync(page, $"Exploration map at {width}px");
            await Assertions.Expect(page.Locator(".exploration-totals")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Cumulative", Exact = true })).ToBeVisibleAsync();
            if (width is 375 or 1280)
                await page.ScreenshotAsync(new() { Path = Path.Combine(captures, $"exploration-{width}.png"), FullPage = true });
        }
        await page.SetViewportSizeAsync(1280, 1000);
        await page.GetByRole(AriaRole.Link, new() { Name = "Cumulative", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Through reporting date", new() { Exact = true })).ToBeVisibleAsync();
        await AssertExplorationCompleteAsync(page);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.EvaluateAsync("""
            () => {
                window.explorationTestFontSizes = [...document.querySelectorAll('body, body *')].map(element => ({
                    element, size: parseFloat(getComputedStyle(element).fontSize),
                    original: element.style.getPropertyValue('font-size'), priority: element.style.getPropertyPriority('font-size')
                }));
                for (const item of window.explorationTestFontSizes)
                    item.element.style.setProperty('font-size', `${item.size * 2}px`, 'important');
            }
            """);
        await AssertNoDocumentOverflowAsync(page, "Exploration map with every computed text size doubled to 200%");
        await page.ScreenshotAsync(new() { Path = Path.Combine(captures, "exploration-200-percent-text.png"), FullPage = true });
        await page.GetByLabel("Through reporting date", new() { Exact = true }).FocusAsync();
        await Assertions.Expect(page.GetByLabel("Through reporting date", new() { Exact = true })).ToBeFocusedAsync();
        await page.EvaluateAsync("""
            () => {
                for (const item of window.explorationTestFontSizes)
                    item.element.style.setProperty('font-size', item.original, item.priority);
                delete window.explorationTestFontSizes;
            }
            """);
        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        var slider = page.GetByRole(AriaRole.Slider, new() { Name = "Cumulative exploration", Exact = true });
        await slider.FocusAsync();
        await slider.PressAsync("Home");
        await Assertions.Expect(page.GetByLabel("Through reporting date", new() { Exact = true })).ToHaveValueAsync("2026-03-01");
        await page.GetByRole(AriaRole.Link, new() { Name = "Range", Exact = true }).ClickAsync();
    }

    private static async Task<ExplorationBrowserSeed> SeedExplorationBrowserDataAsync(string dataRoot)
    {
        await using var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot));
        await db.Database.EnsureCreatedAsync();
        var east = new OwnerProfile { DisplayName = "Exploration East", TimeZoneId = "Europe/Copenhagen" };
        var west = new OwnerProfile { DisplayName = "Exploration West", TimeZoneId = "America/Los_Angeles" };
        var empty = new OwnerProfile { DisplayName = "Exploration Empty", TimeZoneId = "UTC" };
        db.AddRange(east, west, empty);
        var batches = new[] { east, west }.ToDictionary(owner => owner.Id, owner => new ImportBatch
        {
            OwnerId = owner.Id,
            SourceKind = SourceKind.Gpx,
            Status = ImportStatus.Completed,
            DisplayName = "Synthetic exploration sources"
        });
        db.ImportBatches.AddRange(batches.Values);
        Guid firstActivity = default, limitedActivity = default;
        for (var index = 0; index < 64; index++)
        {
            var owner = index < 48 ? east : west;
            var start = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero).AddDays(index);
            var points = Enumerable.Range(0, 161).Select(point => new TrackPoint(
                start.AddSeconds(point * 20), 55.65 + index / 8 * .015, 12.30 + index % 8 * .025 + point * .001,
                null, 10, null, null, null, null, null)).ToArray();
            var activity = BrowserActivity(owner.Id, index == 63 ? "Sample-only exploration ride" : $"Exploration ride {index + 1:00}", SportKind.Cycling, points);
            activity.MovingTimeSeconds = activity.ElapsedTimeSeconds = 3200;
            db.Activities.Add(activity);
            if (index == 0) firstActivity = activity.Id;
            if (index == 63) { limitedActivity = activity.Id; continue; }
            var xml = new StringBuilder("<gpx version=\"1.1\" xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>");
            foreach (var point in points)
                xml.Append(CultureInfo.InvariantCulture, $"<trkpt lat=\"{point.Latitude:R}\" lon=\"{point.Longitude:R}\"><time>{point.Timestamp:O}</time></trkpt>");
            xml.Append("</trkseg></trk></gpx>");
            var bytes = Encoding.UTF8.GetBytes(xml.ToString());
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var relative = $"originals/{owner.Id:N}/{hash}.gpx";
            var absolute = Path.Combine(dataRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            await File.WriteAllBytesAsync(absolute, bytes);
            db.SourceFiles.Add(new SourceFile
            {
                OwnerId = owner.Id,
                ImportBatchId = batches[owner.Id].Id,
                ActivityId = activity.Id,
                OriginalName = $"synthetic-exploration-{index + 1:00}.gpx",
                StoredPath = relative,
                Sha256 = hash,
                Length = bytes.Length,
                SourceKind = SourceKind.Gpx
            });
        }
        await db.SaveChangesAsync();
        return new(east.Id, west.Id, empty.Id, firstActivity, limitedActivity);
    }

    private sealed record ExplorationBrowserSeed(Guid EastOwner, Guid WestOwner, Guid EmptyOwner, Guid FirstActivity, Guid LimitedActivity);
}
