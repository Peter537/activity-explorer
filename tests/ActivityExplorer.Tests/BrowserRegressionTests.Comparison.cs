using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    [Fact]
    public async Task Comparison_preview_fixture_is_created_only_when_explicitly_requested()
    {
        var requestedPath = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_COMPARISON_PREVIEW_DATA");
        if (string.IsNullOrWhiteSpace(requestedPath)) return;
        var path = Path.GetFullPath(requestedPath);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException("The comparison preview fixture requires an empty directory.");
        Directory.CreateDirectory(path);
        var seed = await SeedComparisonBrowserDataAsync(path);
        await File.WriteAllTextAsync(Path.Combine(path, "synthetic-fixture.json"), JsonSerializer.Serialize(seed));
        TestContext.Current.TestOutputHelper!.WriteLine($"Synthetic comparison preview data: {path}");
    }

    [Fact]
    public async Task Segment_comparison_preserves_pair_history_inspection_and_truthful_unavailable_states()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var root = FindRepositoryRoot();
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedComparisonBrowserDataAsync(dataRoot);
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
                if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && uri.Host != "127.0.0.1") externalRequests.Add(request.Url);
            };
            var navigation = new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle };
            var segmentUrl = origin + $"/segments/{seed.SegmentId}";
            await page.GotoAsync(segmentUrl + $"?effort={seed.ComparisonEffortId}&section={seed.ChildId}", navigation);
            await page.GetByRole(AriaRole.Button, new() { Name = "Compare two efforts", Exact = true }).ClickAsync();
            var baseline = page.GetByLabel("Baseline", new() { Exact = true });
            var comparison = page.GetByLabel("Comparison", new() { Exact = true });
            var view = page.Locator("[data-comparison-view]");
            await Assertions.Expect(baseline).ToHaveValueAsync(seed.BaselineEffortId.ToString());
            await Assertions.Expect(comparison).ToHaveValueAsync(seed.ComparisonEffortId.ToString());
            await Assertions.Expect(view).ToHaveAttributeAsync("data-comparison-bound", "true");
            await Assertions.Expect(view.Locator(".comparison-total strong")).ToHaveTextAsync("+25 s");
            var axisPositions = await view.Locator("[data-comparison-metric='0'] .comparison-y-axis span")
                .EvaluateAllAsync<double[]>("nodes => nodes.map(node => node.getBoundingClientRect().top)");
            Assert.True(axisPositions.Length >= 4);
            Assert.All(axisPositions.Zip(axisPositions.Skip(1)), positions => Assert.True(positions.Second - positions.First > 10));
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"view=compare.*baseline={seed.BaselineEffortId}.*comparison={seed.ComparisonEffortId}"));
            Assert.Contains($"effort={seed.ComparisonEffortId}", page.Url, StringComparison.Ordinal);
            Assert.Contains($"section={seed.ChildId}", page.Url, StringComparison.Ordinal);
            await Assertions.Expect(view.Locator(".comparison-children tbody tr")).ToHaveCountAsync(2);
            await Assertions.Expect(view.Locator(".comparison-values tbody tr")).ToHaveCountAsync(4);
            var originalUrl = page.Url;
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(view).ToHaveAttributeAsync("data-comparison-bound", "true");
            await Assertions.Expect(comparison).ToHaveValueAsync(seed.ComparisonEffortId.ToString());
            await page.GetByRole(AriaRole.Button, new() { Name = "Swap efforts", Exact = true }).ClickAsync();
            await Assertions.Expect(view.Locator(".comparison-total strong")).ToHaveTextAsync("−25 s");
            var swappedUrl = page.Url;
            await page.GoBackAsync();
            await Assertions.Expect(page).ToHaveURLAsync(originalUrl);
            await Assertions.Expect(view.Locator(".comparison-total strong")).ToHaveTextAsync("+25 s");
            await page.GoForwardAsync();
            await Assertions.Expect(page).ToHaveURLAsync(swappedUrl);
            await page.GoBackAsync();
            await Assertions.Expect(view).ToHaveAttributeAsync("data-comparison-bound", "true");

            var map = page.Locator("[data-inspection-map]");
            var mapId = await map.GetAttributeAsync("id");
            await page.EvaluateAsync("""
                async () => {
                    const maplibre = await import('/vendor/maplibre-gl.mjs');
                    const original = maplibre.Map.prototype.fitBounds;
                    window.comparisonFits = 0;
                    maplibre.Map.prototype.fitBounds = function (...args) { window.comparisonFits++; return original.apply(this, args); };
                }
                """);
            var slider = view.GetByRole(AriaRole.Slider, new() { Name = "Segment distance", Exact = true });
            await slider.FocusAsync();
            await slider.PressAsync("End");
            await Assertions.Expect(view.Locator(".comparison-location")).ToContainTextAsync("+25");
            await Assertions.Expect(view.Locator("[data-inspection-value='baseline-elapsed']")).ToContainTextAsync("200");
            await Assertions.Expect(view.Locator("[data-inspection-value='comparison-elapsed']")).ToContainTextAsync("225");
            var plot = view.Locator("[data-comparison-metric='0'] .comparison-plot");
            await plot.ScrollIntoViewIfNeededAsync();
            var bounds = await plot.BoundingBoxAsync();
            Assert.NotNull(bounds);
            await page.Mouse.MoveAsync(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            await Assertions.Expect(view.Locator(".comparison-location")).ToContainTextAsync("−25");
            var middleDistance = await view.GetAttributeAsync("data-inspection-distance");
            await Assertions.Expect(map).ToHaveAttributeAsync("data-comparison-distance", middleDistance!);
            await page.Touchscreen.TapAsync(bounds.X + bounds.Width * 0.75f, bounds.Y + bounds.Height / 2);
            await Assertions.Expect(map).ToHaveAttributeAsync("data-comparison-distance", (await view.GetAttributeAsync("data-inspection-distance"))!);
            Assert.Equal(mapId, await map.GetAttributeAsync("id"));
            Assert.Equal(0, await page.EvaluateAsync<int>("window.comparisonFits"));

            var captures = Path.Combine(root, "artifacts", "comparison-ui");
            Directory.CreateDirectory(captures);
            foreach (var width in new[] { 320, 360, 375, 768, 1121, 1280, 1920 })
            {
                await page.SetViewportSizeAsync(width, 1000);
                await AssertNoDocumentOverflowAsync(page, $"Segment comparison at {width}px");
                if (width is 375 or 1280)
                    await CaptureComparisonViewAsync(page, view, Path.Combine(captures, $"comparison-{width}.png"));
            }
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await page.SetViewportSizeAsync(1280, 1000);
            await page.EvaluateAsync("document.documentElement.style.fontSize='200%'; document.body.style.fontSize='30px'");
            await page.WaitForFunctionAsync("""
                () => {
                    const panels = [...document.querySelectorAll('.comparison-chart:not(.comparison-gap)')];
                    return getComputedStyle(document.documentElement).fontSize === '32px' && panels.length === 3 &&
                        panels.every((panel, index) => index === 0 || panel.getBoundingClientRect().top - panels[index - 1].getBoundingClientRect().top > 100);
                }
                """);
            await AssertNoDocumentOverflowAsync(page, "Segment comparison with enlarged text");
            var enlargedChartPositions = await view.Locator(".comparison-chart:not(.comparison-gap)")
                .EvaluateAllAsync<double[]>("nodes => nodes.map(node => node.getBoundingClientRect().top)");
            Assert.All(enlargedChartPositions.Zip(enlargedChartPositions.Skip(1)), positions => Assert.True(positions.Second - positions.First > 100));
            await CaptureComparisonViewAsync(page, view, Path.Combine(captures, "comparison-enlarged.png"));
            await page.EvaluateAsync("document.documentElement.style.removeProperty('font-size'); document.body.style.removeProperty('font-size')");
            await baseline.SelectOptionAsync(seed.NoSensorEffortId.ToString());
            await comparison.SelectOptionAsync(seed.OtherNoSensorEffortId.ToString());
            await Assertions.Expect(view.Locator(".comparison-empty")).ToHaveCountAsync(3);
            await Assertions.Expect(view.Locator("[data-inspection-value='baseline-heart']")).ToHaveTextAsync("Not recorded");
            Assert.Equal(mapId, await map.GetAttributeAsync("id"));
            Assert.Equal(0, await page.EvaluateAsync<int>("window.comparisonFits"));
            await baseline.SelectOptionAsync(seed.BaselineEffortId.ToString());
            await comparison.SelectOptionAsync(seed.GapEffortId.ToString());
            await Assertions.Expect(view).ToContainTextAsync("Aligned comparison unavailable");
            await Assertions.Expect(view.Locator(".comparison-plot")).ToHaveCountAsync(0);
            await CaptureComparisonViewAsync(page, view, Path.Combine(captures, "comparison-unavailable.png"));
            Assert.Equal(mapId, await map.GetAttributeAsync("id"));
            Assert.Equal(0, await page.EvaluateAsync<int>("window.comparisonFits"));
            await comparison.SelectOptionAsync(seed.NoSensorEffortId.ToString());
            await comparison.SelectOptionAsync(seed.ComparisonEffortId.ToString());
            await Assertions.Expect(view.Locator(".comparison-total strong")).ToHaveTextAsync("+25 s");
            await Assertions.Expect(view).ToHaveAttributeAsync("data-comparison-bound", "true");
            await Assertions.Expect(page.Locator(".effort-table tbody tr")).ToHaveCountAsync(5);
            var dataOptions = new DbContextOptionsBuilder<ExplorerDbContext>()
                .UseSqlite($"Data Source={Path.Combine(dataRoot, "activity-explorer.db")}").Options;
            Guid damagedActivityId;
            byte[] originalPayload;
            await using (var db = new ExplorerDbContext(dataOptions))
            {
                damagedActivityId = await db.SegmentEfforts.Where(effort => effort.Id == seed.BaselineEffortId)
                    .Select(effort => effort.ActivityId).SingleAsync();
                originalPayload = await db.ActivityStreams.Where(stream => stream.ActivityId == damagedActivityId)
                    .Select(stream => stream.CompressedPayload).SingleAsync();
                await db.ActivityStreams.Where(stream => stream.ActivityId == damagedActivityId)
                    .ExecuteUpdateAsync(update => update.SetProperty(stream => stream.CompressedPayload, new byte[] { 1, 2, 3 }));
            }
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Retry comparison", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".effort-table tbody tr")).ToHaveCountAsync(5);
            await Assertions.Expect(baseline).ToHaveValueAsync(seed.BaselineEffortId.ToString());
            await Assertions.Expect(view).ToHaveCountAsync(0);
            await using (var db = new ExplorerDbContext(dataOptions))
                await db.ActivityStreams.Where(stream => stream.ActivityId == damagedActivityId)
                    .ExecuteUpdateAsync(update => update.SetProperty(stream => stream.CompressedPayload, originalPayload));
            await page.GetByRole(AriaRole.Button, new() { Name = "Retry comparison", Exact = true }).ClickAsync();
            await Assertions.Expect(view).ToHaveAttributeAsync("data-comparison-bound", "true");
            await Assertions.Expect(view.Locator(".comparison-total strong")).ToHaveTextAsync("+25 s");
            await page.GetByRole(AriaRole.Button, new() { Name = "Inspect effort", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".effort-diagnostics")).ToBeVisibleAsync();
            Assert.Contains($"effort={seed.ComparisonEffortId}", page.Url, StringComparison.Ordinal);
            Assert.Contains($"section={seed.ChildId}", page.Url, StringComparison.Ordinal);
            await page.GotoAsync(segmentUrl + $"?effort={seed.BaselineEffortId}", navigation);
            await page.GetByRole(AriaRole.Button, new() { Name = "Compare two efforts", Exact = true }).ClickAsync();
            await Assertions.Expect(comparison).ToHaveValueAsync("");
            await Assertions.Expect(page.Locator(".comparison-controls").GetByRole(AriaRole.Status)).ToContainTextAsync("Choose another effort");
            await comparison.SelectOptionAsync(seed.ComparisonEffortId.ToString());
            await Assertions.Expect(view).ToHaveAttributeAsync("data-comparison-bound", "true");
            await page.EvaluateAsync("""
                async () => {
                    const maplibre = await import('/vendor/maplibre-gl.mjs');
                    const original = maplibre.Map.prototype.addSource;
                    maplibre.Map.prototype.addSource = function (...args) {
                        window.returnedInspectionMap = this;
                        return original.apply(this, args);
                    };
                }
                """);
            await page.GetByRole(AriaRole.Button, new() { Name = "Inspect effort", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".effort-diagnostics")).ToBeVisibleAsync();
            Assert.Contains($"effort={seed.BaselineEffortId}", page.Url, StringComparison.Ordinal);
            await page.WaitForFunctionAsync("""
                () => window.returnedInspectionMap?.getContainer().id === document.querySelector('[data-inspection-map]')?.id &&
                    window.returnedInspectionMap.getSource('selected-effort')?.serialize().data.geometry?.coordinates.length === 101
                """);
            await page.GotoAsync(segmentUrl + $"?view=compare&baseline=malformed&comparison={seed.ComparisonEffortId}", navigation);
            await Assertions.Expect(view).ToContainTextAsync("Aligned comparison unavailable");
            await Assertions.Expect(page.Locator(".effort-table tbody tr")).ToHaveCountAsync(5);
            await baseline.SelectOptionAsync(seed.BaselineEffortId.ToString());
            await Assertions.Expect(view).ToHaveAttributeAsync("data-comparison-bound", "true");
            await page.GotoAsync(segmentUrl + $"?view=compare&baseline={seed.BaselineEffortId.ToString().ToUpperInvariant()}&comparison={seed.ComparisonEffortId:N}", navigation);
            await Assertions.Expect(view).ToHaveAttributeAsync("data-comparison-bound", "true");
            await Assertions.Expect(baseline).ToHaveValueAsync(seed.BaselineEffortId.ToString());
            await Assertions.Expect(comparison).ToHaveValueAsync(seed.ComparisonEffortId.ToString());
            await Assertions.Expect(view.Locator(".comparison-total strong")).ToHaveTextAsync("+25 s");
            Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
            Assert.Empty(externalRequests);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    private static async Task CaptureComparisonViewAsync(IPage page, ILocator view, string path)
    {
        var header = page.Locator(".topbar");
        var previousVisibility = await header.EvaluateAsync<string>("element => { const previous = element.style.visibility; element.style.visibility = 'hidden'; return previous; }");
        try { await view.ScreenshotAsync(new() { Path = path }); }
        finally
        {
            await header.EvaluateAsync("(element, previous) => { if (previous) element.style.visibility = previous; else element.style.removeProperty('visibility'); }", previousVisibility);
        }
    }

    private static async Task<ComparisonBrowserSeed> SeedComparisonBrowserDataAsync(string dataRoot)
    {
        var options = new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(dataRoot, "activity-explorer.db")}").Options;
        var factory = new SegmentBrowserDbFactory(options);
        var owner = new OwnerProfile { DisplayName = "Comparison preview athlete" };
        var start = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var definition = Enumerable.Range(0, 101).Select(index => new TrackPoint(start.AddSeconds(index * 2),
            1 + index * 0.0001, -30 + index * 0.0001, null, 20 + Math.Min(index, 50) * 1.2,
            index < 50 ? 6 : 9, 130 + index % 15, 80, index < 50 ? 250 : 160, null)).ToArray();
        var baseline = BrowserActivity(owner.Id, "Baseline climb and flat", SportKind.Cycling, definition);
        var comparisonPoints = definition.Select((point, index) => point with
        {
            Timestamp = start.AddDays(1).AddSeconds(index <= 50 ? index * 1.5 : 75 + (index - 50) * 3),
            SpeedMetersPerSecond = index < 50 ? 8 : 6,
            HeartRate = 135 + index % 15,
            PowerWatts = index < 50 ? 300 : 130
        }).ToArray();
        var comparison = BrowserActivity(owner.Id, "Faster climb and slower flat", SportKind.Cycling, comparisonPoints);
        var noSensorPoints = definition.Select((point, index) => point with
        {
            Timestamp = start.AddDays(2).AddSeconds(index * 3),
            SpeedMetersPerSecond = null,
            HeartRate = null,
            PowerWatts = null
        }).ToArray();
        var noSensor = BrowserActivity(owner.Id, "Path without sensor readings", SportKind.Cycling, noSensorPoints);
        var otherNoSensor = BrowserActivity(owner.Id, "Second path without sensors", SportKind.Cycling,
            noSensorPoints.Select((point, index) => point with { Timestamp = start.AddDays(3).AddSeconds(index * 3.5) }).ToArray());
        var gap = BrowserActivity(owner.Id, "Legacy pass with recording break", SportKind.Cycling,
            definition.Select((point, index) => point with { Timestamp = start.AddDays(4).AddSeconds(index * 2 + (index >= 60 ? 60 : 0)) }).ToArray());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.AddRange(owner, baseline, comparison, noSensor, otherNoSensor, gap);
            await db.SaveChangesAsync();
        }
        var service = new SegmentService(factory, new SegmentMatcher(), new OwnerMutationLock());
        var segment = await service.CreateFromActivityAsync(new(owner.Id, baseline.Id, "Climb and riverside comparison", 0, 100));
        var child = await service.CreateSubsegmentAsync(new(segment, "Upper climb", 20, 40));
        await service.CreateSubsegmentAsync(new(segment, "Riverside flat", 65, 85));
        var gapEffort = new SegmentEffort
        {
            OwnerId = owner.Id,
            SegmentId = segment,
            ActivityId = gap.Id,
            StartPointIndex = 0,
            EndPointIndex = 100,
            StartTimeUtc = start.AddDays(4),
            ElapsedSeconds = 260,
            MovingSeconds = 200,
            CoveragePercent = 100,
            Rank = 3,
            MetricComputationVersion = SegmentEffortMetricVersions.Legacy
        };
        await using (var db = factory.CreateDbContext())
        {
            db.SegmentEfforts.Add(gapEffort);
            await db.SaveChangesAsync();
        }
        var detail = (await service.GetAsync(segment))!;
        return new(owner.Id, segment, child, detail.Efforts.Single(effort => effort.ActivityId == baseline.Id).Id,
            detail.Efforts.Single(effort => effort.ActivityId == comparison.Id).Id,
            detail.Efforts.Single(effort => effort.ActivityId == noSensor.Id).Id,
            detail.Efforts.Single(effort => effort.ActivityId == otherNoSensor.Id).Id, gapEffort.Id);
    }

    private sealed record ComparisonBrowserSeed(Guid OwnerId, Guid SegmentId, Guid ChildId,
        Guid BaselineEffortId, Guid ComparisonEffortId, Guid NoSensorEffortId, Guid OtherNoSensorEffortId, Guid GapEffortId);
}
