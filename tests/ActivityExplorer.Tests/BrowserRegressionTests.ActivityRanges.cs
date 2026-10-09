using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using ActivityExplorer.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    private static readonly double[] FullResetRangePositions = [0, 11];
    private static readonly double[] TailResetRangePositions = [6, 11];
    [Fact]
    public async Task Range_preview_fixture_is_created_only_when_explicitly_requested()
    {
        var requestedPath = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_RANGE_PREVIEW_DATA");
        if (string.IsNullOrWhiteSpace(requestedPath)) return;
        var path = Path.GetFullPath(requestedPath);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException("The range preview fixture requires an empty directory.");
        Directory.CreateDirectory(path);
        var seed = await SeedRangeBrowserDataAsync(path);
        await File.WriteAllTextAsync(Path.Combine(path, "synthetic-fixture.json"), JsonSerializer.Serialize(seed));
        TestContext.Current.TestOutputHelper!.WriteLine($"Synthetic range preview data: {path}");
    }

    [Fact]
    public async Task Exact_attempt_links_ranges_gestures_and_history_preserve_source_intervals()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var root = FindRepositoryRoot();
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedRangeBrowserDataAsync(dataRoot);
        var origin = $"http://127.0.0.1:{ReservePort()}";
        var output = new StringBuilder();
        using var process = StartApplication(FindWebAssembly(root), dataRoot, origin, output);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync(new() { HasTouch = true, ViewportSize = new() { Width = 1280, Height = 1000 } });
            var errors = new List<string>();
            var externalRequests = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            page.Console += (_, message) => { if (message.Type == "error") errors.Add(message.Text); };
            page.Request += (_, request) =>
            {
                if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && uri.Host != "127.0.0.1")
                    externalRequests.Add(request.Url);
            };
            var navigation = new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle };
            var attempts = $"/records/attempts?sport=cycling&kind=distanceeffort&key=5%20km&owner={seed.OwnerId}";
            await page.GotoAsync(origin + attempts, navigation);
            await Assertions.Expect(page.Locator(".attempt-table tbody tr")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".attempt-table tbody th a")).ToHaveAttributeAsync("href", $"/activities/{seed.DenseActivityId}");
            var exactHref = await page.Locator(".attempt-range-link").GetAttributeAsync("href");
            Assert.NotNull(exactHref);
            Assert.Contains("range=", exactHref);
            Assert.Contains("stream=", exactHref);
            await page.Locator(".attempt-range-link").ClickAsync();
            await WaitForRangeSummaryAsync(page);
            var originalRange = await RangePositionsAsync(page);
            Assert.Equal(5000d / 11, originalRange[1] - originalRange[0], 8);
            Assert.True(originalRange.Any(position => position != Math.Floor(position)), "The 5 km attempt must retain a fractional source boundary.");
            await Assertions.Expect(page.Locator(".range-metrics")).ToContainTextAsync(new Regex("5[,.]0 km"));
            await Assertions.Expect(page.Locator(".activity-range-control")).ToContainTextAsync("from first timestamp");
            var summary = await page.Locator(".range-metrics").InnerTextAsync();
            var mapId = await page.Locator("#activity-map .detail-map").GetAttributeAsync("id");
            var chart = page.Locator(".time-series-chart").First;
            Assert.InRange(await chart.Locator(".chart-sample").CountAsync(), 1, 600);
            await Assertions.Expect(chart.Locator(".chart-range-line").First).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Distance", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".synchronized-charts")).ToHaveAttributeAsync("data-axis-kind", "distance");
            await WaitForRangeSummaryAsync(page);
            Assert.Equal(originalRange, await RangePositionsAsync(page));
            Assert.Equal(summary, await page.Locator(".range-metrics").InnerTextAsync());
            Assert.Equal(mapId, await page.Locator("#activity-map .detail-map").GetAttributeAsync("id"));
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await WaitForRangeSummaryAsync(page);
            Assert.Equal(originalRange, await RangePositionsAsync(page));
            await Assertions.Expect(page.Locator(".synchronized-charts")).ToHaveAttributeAsync("data-axis-kind", "distance");
            await page.GoBackAsync();
            await Assertions.Expect(page.Locator(".synchronized-charts")).ToHaveAttributeAsync("data-axis-kind", "time");
            Assert.Equal(originalRange, await RangePositionsAsync(page));
            await page.GoForwardAsync();
            await Assertions.Expect(page.Locator(".synchronized-charts")).ToHaveAttributeAsync("data-axis-kind", "distance");
            await page.GetByRole(AriaRole.Navigation, new() { Name = "Activity sections" }).GetByRole(AriaRole.Link, new() { Name = "Map", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("range=.*#activity-map$"));
            Assert.Equal(originalRange, await RangePositionsAsync(page));

            await page.GetByRole(AriaRole.Button, new() { Name = "Select range", Exact = true }).ClickAsync();
            await Assertions.Expect(chart).ToHaveClassAsync(new Regex("range-selecting"));
            await Assertions.Expect(chart).ToHaveAttributeAsync("data-range-ready", "true");
            await chart.ScrollIntoViewIfNeededAsync();
            var plot = await chart.Locator(".chart-plot").BoundingBoxAsync();
            Assert.NotNull(plot);
            var beforeDrag = page.Url;
            var historyLength = await page.EvaluateAsync<int>("history.length");
            var sourcePositionsPerPlot = await chart.Locator(".chart-sample").Last.EvaluateAsync<double>(
                "sample => Number(sample.dataset.sourcePosition) * 800 / Number(sample.getAttribute('cx'))");
            await page.Mouse.MoveAsync(plot.X + plot.Width * 0.2f, plot.Y + plot.Height * 0.5f);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(plot.X + plot.Width * 0.55f, plot.Y + plot.Height * 0.5f, new() { Steps = 6 });
            Assert.Equal(beforeDrag, page.Url);
            Assert.Equal(historyLength, await page.EvaluateAsync<int>("history.length"));
            await Assertions.Expect(chart.Locator(".chart-range-preview-line").First).ToBeVisibleAsync();
            await page.Mouse.UpAsync();
            await Assertions.Expect(page).Not.ToHaveURLAsync(beforeDrag);
            await page.WaitForFunctionAsync("start => Number(document.querySelector('.activity-range-control').dataset.rangeStart) !== start", originalRange[0]);
            await WaitForRangeSummaryAsync(page);
            Assert.Equal(historyLength + 1, await page.EvaluateAsync<int>("history.length"));
            var dragged = await RangePositionsAsync(page);
            Assert.True(dragged[0] > 0 && dragged[1] > dragged[0] && dragged[1] < 1200);
            Assert.InRange(dragged[0], sourcePositionsPerPlot * 0.2 - 1, sourcePositionsPerPlot * 0.2 + 1);
            Assert.InRange(dragged[1], sourcePositionsPerPlot * 0.55 - 1, sourcePositionsPerPlot * 0.55 + 1);

            await chart.ScrollIntoViewIfNeededAsync();
            plot = await chart.Locator(".chart-plot").BoundingBoxAsync();
            Assert.NotNull(plot);
            var beforeCancel = page.Url;
            await page.Mouse.MoveAsync(plot.X + plot.Width * 0.3f, plot.Y + plot.Height * 0.5f);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(plot.X + plot.Width * 0.7f, plot.Y + plot.Height * 0.5f);
            await page.Keyboard.PressAsync("Escape");
            await page.Mouse.UpAsync();
            Assert.Equal(beforeCancel, page.Url);
            await Assertions.Expect(chart.Locator(".chart-range-preview-line")).ToHaveCountAsync(0);
            await page.Touchscreen.TapAsync(plot.X + plot.Width * 0.1f, plot.Y + plot.Height * 0.5f);
            Assert.Equal(beforeCancel, page.Url);
            await page.Touchscreen.TapAsync(plot.X + plot.Width * 0.4f, plot.Y + plot.Height * 0.5f);
            await Assertions.Expect(page).Not.ToHaveURLAsync(beforeCancel);
            await page.WaitForFunctionAsync("start => Number(document.querySelector('.activity-range-control').dataset.rangeStart) !== start", dragged[0]);
            await WaitForRangeSummaryAsync(page);
            var tapped = await RangePositionsAsync(page);
            await chart.ScrollIntoViewIfNeededAsync();
            plot = await chart.Locator(".chart-plot").BoundingBoxAsync();
            Assert.NotNull(plot);
            await page.Touchscreen.TapAsync(plot.X + plot.Width * 0.7f, plot.Y + plot.Height * 0.5f);
            await Assertions.Expect(chart.Locator(".chart-range-preview-endpoint").First).ToBeVisibleAsync();
            var startForward = page.GetByRole(AriaRole.Button, new() { Name = "Move range start forward", Exact = true });
            await startForward.FocusAsync();
            await startForward.PressAsync("Enter");
            await Assertions.Expect(page.Locator(".activity-range-control")).ToHaveAttributeAsync("data-range-start", (Math.Floor(tapped[0]) + 1).ToString("R", CultureInfo.InvariantCulture));
            await Assertions.Expect(chart.Locator(".chart-range-preview-endpoint")).ToHaveCountAsync(0);
            await chart.ScrollIntoViewIfNeededAsync();
            plot = await chart.Locator(".chart-plot").BoundingBoxAsync();
            Assert.NotNull(plot);
            await page.Touchscreen.TapAsync(plot.X + plot.Width * 0.25f, plot.Y + plot.Height * 0.5f);
            await page.Touchscreen.TapAsync(plot.X + plot.Width * 0.45f, plot.Y + plot.Height * 0.5f);
            await page.WaitForFunctionAsync("start => Number(document.querySelector('.activity-range-control').dataset.rangeStart) !== start", Math.Floor(tapped[0]) + 1);
            await WaitForRangeSummaryAsync(page);
            var freshTaps = await RangePositionsAsync(page);
            Assert.InRange(freshTaps[0], sourcePositionsPerPlot * 0.25 - 1, sourcePositionsPerPlot * 0.25 + 1);
            Assert.InRange(freshTaps[1], sourcePositionsPerPlot * 0.45 - 1, sourcePositionsPerPlot * 0.45 + 1);
            var beforeKeyboard = (await RangePositionsAsync(page))[1];
            await page.GetByLabel("Range end", new() { Exact = true }).FocusAsync();
            await page.Keyboard.PressAsync("ArrowLeft");
            await page.Keyboard.PressAsync("Tab");
            await page.WaitForFunctionAsync("end => Number(document.querySelector('.activity-range-control').dataset.rangeEnd) < end", beforeKeyboard);
            await page.GetByRole(AriaRole.Button, new() { Name = "Finish selecting", Exact = true }).ClickAsync();
            await chart.Locator(".chart-accessible-values > summary").ClickAsync();
            var otherOutput = await page.Locator(".time-series-chart").Nth(1).Locator(".chart-exact-output").TextContentAsync();
            await chart.GetByRole(AriaRole.Slider).FocusAsync();
            await page.Keyboard.PressAsync("End");
            await Assertions.Expect(chart.Locator(".chart-exact-output")).ToContainTextAsync("Sample 600 of 600");
            Assert.Equal(otherOutput, await page.Locator(".time-series-chart").Nth(1).Locator(".chart-exact-output").TextContentAsync());
            await AssertRangeResponsiveAsync(page, root);
            await page.GetByRole(AriaRole.Button, new() { Name = "Clear selection", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".activity-range-control")).ToHaveCountAsync(0);
            Assert.DoesNotContain("range=", page.Url);

            await page.GotoAsync(origin + attempts + "&view=progress", navigation);
            await Assertions.Expect(page.Locator(".history-table .attempt-range-link")).ToHaveAttributeAsync("href", exactHref);
            await Assertions.Expect(page.Locator(".history-table tbody th a")).ToHaveAttributeAsync("href", $"/activities/{seed.DenseActivityId}");
            await Assertions.Expect(page.Locator(".chart-exact-output .attempt-range-link")).ToHaveAttributeAsync("href", exactHref);
            await page.GotoAsync(origin + $"/records/attempts?sport=cycling&kind=distance&key=Longest%20distance&owner={seed.OwnerId}", navigation);
            var wholeLinks = await page.Locator(".attempt-range-link").EvaluateAllAsync<string[]>("links => links.map(link => link.getAttribute('href'))");
            Assert.NotEmpty(wholeLinks);
            Assert.All(wholeLinks, link => Assert.DoesNotContain("range=", link));
            Assert.Empty(errors);
            Assert.Empty(externalRequests);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    [Fact]
    public async Task Range_sections_missing_data_and_stale_links_recover_without_losing_whole_activities()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var root = FindRepositoryRoot();
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedRangeBrowserDataAsync(dataRoot);
        var origin = $"http://127.0.0.1:{ReservePort()}";
        var output = new StringBuilder();
        using var process = StartApplication(FindWebAssembly(root), dataRoot, origin, output);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 1000 } });
            var errors = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            var navigation = new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle };
            await page.GotoAsync(origin + $"/activities/{seed.ResetActivityId}", navigation);
            await Assertions.Expect(page.GetByLabel("Recording section")).ToBeVisibleAsync();
            Assert.True(await page.Locator(".time-series-chart .chart-sample").EvaluateAllAsync<bool>("samples => samples.some(sample => Number(sample.dataset.axis) < 0)"));
            await page.GetByRole(AriaRole.Button, new() { Name = "Select range", Exact = true }).ClickAsync();
            await WaitForRangeSummaryAsync(page);
            Assert.Equal(FullResetRangePositions, await RangePositionsAsync(page));
            await Assertions.Expect(page.Locator(".range-metrics")).ToContainTextAsync("Known duration · partial");
            await Assertions.Expect(page.Locator(".range-metrics")).ToContainTextAsync("percentage unavailable");
            await Assertions.Expect(page.Locator(".range-gap-notice")).ToBeVisibleAsync();
            var summary = await page.Locator(".range-metrics").InnerTextAsync();
            await page.GetByLabel("Recording section").SelectOptionAsync(new SelectOptionValue { Index = 1 });
            Assert.Equal(FullResetRangePositions, await RangePositionsAsync(page));
            await page.GetByRole(AriaRole.Button, new() { Name = "Distance", Exact = true }).ClickAsync();
            await WaitForRangeSummaryAsync(page);
            Assert.Equal(summary, await page.Locator(".range-metrics").InnerTextAsync());
            await page.GetByLabel("Range start", new() { Exact = true }).EvaluateAsync("input => { input.value = '6'; input.dispatchEvent(new Event('change', { bubbles: true })); }");
            await Assertions.Expect(page.Locator(".activity-range-control")).ToHaveAttributeAsync("data-range-start", "6");
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await WaitForRangeSummaryAsync(page);
            Assert.Equal(TailResetRangePositions, await RangePositionsAsync(page));

            await page.GotoAsync(origin + $"/activities/{seed.IndoorActivityId}", navigation);
            await Assertions.Expect(page.Locator("#activity-map")).ToHaveCountAsync(0);
            await page.GetByRole(AriaRole.Button, new() { Name = "Select range", Exact = true }).ClickAsync();
            await WaitForRangeSummaryAsync(page);
            await Assertions.Expect(page.Locator(".range-metrics")).ToContainTextAsync("Not recorded");
            await Assertions.Expect(page.Locator(".range-metrics")).ToContainTextAsync("covered");
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Create segment", Exact = true })).ToBeDisabledAsync();
            var validUrl = page.Url;
            string reversedAfterResolution;
            await using (var db = new ExplorerDbContext(RangeBrowserOptions(dataRoot)))
            {
                var stream = await db.ActivityStreams.SingleAsync(stream => stream.ActivityId == seed.IndoorActivityId);
                var points = TrackCodec.Decode(stream.CompressedPayload);
                var range = new ActivityRange(new(0, 2, 0.6, points[0].Timestamp!.Value.AddSeconds(3)),
                    ActivityRangeBoundary.FromPosition(points, 1.25)!);
                reversedAfterResolution = ActivityRangeUrl.Serialize(range);
            }
            foreach (var malformed in new[] { "broken", "1,0,0,NaN,-,1,1,0,-", "1,9000,9000,0,-,9001,9001,0,-", "1,5,5,0,-,1,1,0,-", reversedAfterResolution })
            {
                var malformedUrl = new UriBuilder(validUrl);
                malformedUrl.Query = Regex.Replace(malformedUrl.Query.TrimStart('?'), "range=[^&]*", "range=" + Uri.EscapeDataString(malformed));
                await page.GotoAsync(malformedUrl.Uri.AbsoluteUri, navigation);
                await Assertions.Expect(page.Locator(".range-error")).ToContainTextAsync("whole activity is still available");
                await Assertions.Expect(page.Locator(".activity-range-control")).ToHaveCountAsync(0);
                await Assertions.Expect(page.Locator(".metric-grid")).ToBeVisibleAsync();
            }
            await using (var db = new ExplorerDbContext(RangeBrowserOptions(dataRoot)))
            {
                var activity = await db.Activities.SingleAsync(activity => activity.Id == seed.IndoorActivityId);
                activity.Title = "Renamed indoor range ride";
                await db.SaveChangesAsync();
            }
            await page.GotoAsync(validUrl, navigation);
            await WaitForRangeSummaryAsync(page);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Renamed indoor range ride", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".range-error")).ToHaveCountAsync(0);
            await using (var db = new ExplorerDbContext(RangeBrowserOptions(dataRoot)))
            {
                var stream = await db.ActivityStreams.SingleAsync(stream => stream.ActivityId == seed.IndoorActivityId);
                var points = TrackCodec.Decode(stream.CompressedPayload).ToArray();
                points[0] = points[0] with { PowerWatts = 321 };
                stream.CompressedPayload = TrackCodec.Encode(points);
                await db.SaveChangesAsync();
            }
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator(".range-error")).ToContainTextAsync("whole activity is still available");
            await Assertions.Expect(page.Locator(".activity-range-control")).ToHaveCountAsync(0);
            await page.GetByRole(AriaRole.Button, new() { Name = "Select range", Exact = true }).ClickAsync();
            await WaitForRangeSummaryAsync(page);
            await Assertions.Expect(page.Locator(".range-error")).ToHaveCountAsync(0);
            Assert.NotEqual(validUrl, page.Url);
            Assert.Empty(errors);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    private static async Task WaitForRangeSummaryAsync(IPage page)
    {
        await Assertions.Expect(page.Locator(".activity-range-summary")).ToHaveAttributeAsync("aria-busy", "false");
        await Assertions.Expect(page.Locator(".range-metrics")).ToBeVisibleAsync();
    }

    private static Task<double[]> RangePositionsAsync(IPage page) => page.Locator(".activity-range-control")
        .EvaluateAsync<double[]>("element => [Number(element.dataset.rangeStart), Number(element.dataset.rangeEnd)]");

    private static async Task AssertRangeResponsiveAsync(IPage page, string root)
    {
        var captureDirectory = Path.Combine(root, "artifacts", "range-ui");
        Directory.CreateDirectory(captureDirectory);
        foreach (var width in new[] { 320, 360, 375, 768, 1121, 1280, 1920 })
        {
            await page.SetViewportSizeAsync(width, 1000);
            await page.Locator(".sidebar").EvaluateAsync("element => Promise.all(element.getAnimations().map(animation => animation.finished))");
            if (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth"))
            {
                var offenders = await page.EvaluateAsync<string>("""
                    () => JSON.stringify([...document.querySelectorAll('body *')]
                        .map(element => ({ tag: element.tagName, className: element.getAttribute('class'),
                            right: element.getBoundingClientRect().right, width: element.getBoundingClientRect().width }))
                        .filter(element => element.right > document.documentElement.clientWidth + 1 && element.width > 0)
                        .slice(-20))
                    """);
                TestContext.Current.TestOutputHelper!.WriteLine($"Overflow at {width}px: {offenders}");
                await page.ScreenshotAsync(new() { Path = Path.Combine(captureDirectory, $"range-overflow-{width}.png"), FullPage = true });
            }
            await AssertNoDocumentOverflowAsync(page, $"Activity range at {width}px");
            if (width is 375 or 1280)
                await page.ScreenshotAsync(new() { Path = Path.Combine(captureDirectory, $"range-{width}.png"), FullPage = true });
            if (width == 1280)
            {
                await page.Locator(".range-panel").EvaluateAsync("element => element.scrollIntoView({ block: 'center', behavior: 'instant' })");
                await page.Locator(".range-panel").ScreenshotAsync(new() { Path = Path.Combine(captureDirectory, "range-summary-desktop.png") });
            }
        }
        await page.SetViewportSizeAsync(1280, 1000);
        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        await page.EvaluateAsync("document.documentElement.style.fontSize='200%'; document.body.style.fontSize='30px'");
        await AssertNoDocumentOverflowAsync(page, "Activity range with enlarged text");
        await page.ScreenshotAsync(new() { Path = Path.Combine(captureDirectory, "range-enlarged-text.png"), FullPage = true });
        await page.EvaluateAsync("document.documentElement.style.removeProperty('font-size'); document.body.style.removeProperty('font-size')");
    }

    private static DbContextOptions<ExplorerDbContext> RangeBrowserOptions(string dataRoot) =>
        new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite($"Data Source={Path.Combine(dataRoot, "activity-explorer.db")}").Options;

    private static async Task<RangeBrowserSeed> SeedRangeBrowserDataAsync(string dataRoot)
    {
        var options = RangeBrowserOptions(dataRoot);
        var owner = new OwnerProfile { DisplayName = "Range preview athlete", TimeZoneId = "Europe/Copenhagen" };
        var origin = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var densePoints = Enumerable.Range(0, 1201).Select(index => new TrackPoint(
            origin.AddSeconds(90 + index * 2 + index % 2), 55 + 0.00001 * index, 10 + 0.0001 * index,
            index * 11, 20 + 10 * Math.Sin(index / 20d), index % 2 == 0 ? 11 : 11d / 3,
            100 + index % 5 * 10, 80 + index % 3, 200, 15, 16)).ToArray();
        var dense = BrowserActivity(owner.Id, "Dense fractional benchmark ride", SportKind.Cycling, densePoints);
        dense.StartTimeUtc = origin;
        dense.DistanceMeters = 13200;
        dense.ElapsedTimeSeconds = 2490;
        dense.MovingTimeSeconds = 2400;
        var indoorTimes = new[] { 0, 2, 5, 9, 14, 20, 27, 35, 44 };
        var indoorPoints = indoorTimes.Select((seconds, index) => new TrackPoint(origin.AddDays(1).AddSeconds(seconds),
            null, null, seconds * 10, null, 10, index == 4 ? null : 130, 80,
            index == 4 ? null : index == 1 ? 0 : 100, null)).ToArray();
        var indoor = BrowserActivity(owner.Id, "Indoor range ride with missing sensors", SportKind.Cycling, indoorPoints);
        indoor.IsIndoor = true;
        indoor.DistanceMeters = 440;
        indoor.ElapsedTimeSeconds = 44;
        var resetTimes = new[] { 0, 10, 20, 65, 75, 85, -10, 0, 10, 20, 30, 40 };
        var resetPoints = resetTimes.Select((seconds, index) => new TrackPoint(origin.AddDays(2).AddSeconds(seconds),
            index is 3 or 4 ? null : 55 + index * 0.0001, index is 3 or 4 ? null : 10 + index * 0.0001,
            index < 6 ? index * 100 : (index - 6) * 100, index == 4 ? null : 10 + index,
            10, index == 3 ? null : index == 7 ? 0 : 140, index == 5 ? null : 90,
            index is 3 or 4 ? null : 180, null)).ToArray();
        var reset = BrowserActivity(owner.Id, "Recording resets and missing GPS", SportKind.Cycling, resetPoints);
        reset.DistanceMeters = 1000;
        await using (var db = new ExplorerDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.AddRange(owner, dense, indoor, reset);
            await db.SaveChangesAsync();
        }
        await new StatisticsService(new AttemptBrowserDbFactory(options)).RecomputeAsync(owner.Id);
        return new(owner.Id, dense.Id, indoor.Id, reset.Id);
    }

    private sealed record RangeBrowserSeed(Guid OwnerId, Guid DenseActivityId, Guid IndoorActivityId, Guid ResetActivityId);
}
