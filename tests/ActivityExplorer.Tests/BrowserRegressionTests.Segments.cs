using System.Text;
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
    public async Task Nested_segments_preserve_shared_history_pass_context_and_accessible_navigation()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var root = FindRepositoryRoot();
        var dataRoot = TestSupport.NewDirectory();
        var options = new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(dataRoot, "activity-explorer.db")}").Options;
        var factory = new SegmentBrowserDbFactory(options);
        var owner = new OwnerProfile { DisplayName = "Section browser athlete" };
        var start = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var loop = Enumerable.Range(0, 81).Select(index => new TrackPoint(start.AddSeconds(index),
            1 + 0.001 * Math.Sin(2 * Math.PI * index / 80), -30 + 0.001 * Math.Cos(2 * Math.PI * index / 80),
            null, 20 + 5 * Math.Sin(2 * Math.PI * index / 80), null, 140, 80, null, null)).ToArray();
        var laps = loop.Concat(loop.Skip(1)).Concat(loop.Skip(1))
            .Select((point, index) => point with { Timestamp = start.AddSeconds(index) }).ToArray();
        var firstActivity = BrowserActivity(owner.Id, "Three laps", SportKind.Running, laps);
        var secondActivity = BrowserActivity(owner.Id, "Slower laps", SportKind.Running,
            laps.Select((point, index) => point with { Timestamp = start.AddDays(1).AddSeconds(index * 2) }).ToArray());
        await using (var db = new ExplorerDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.AddRange(owner, firstActivity, secondActivity);
            await db.SaveChangesAsync();
        }
        var segments = new SegmentService(factory, new SegmentMatcher(), new OwnerMutationLock());
        var parent = await segments.CreateFromActivityAsync(new CreateSegmentRequest(owner.Id, firstActivity.Id, "Two park laps", 0, 160));
        var otherParent = await segments.CreateFromActivityAsync(new CreateSegmentRequest(owner.Id, firstActivity.Id, "One park lap", 0, 80));
        var child = await segments.CreateSubsegmentAsync(new CreateSubsegmentRequest(parent, "Waterside stretch", 20, 40));
        var grandchild = await segments.CreateSubsegmentAsync(new CreateSubsegmentRequest(child, "Last straight", 5, 15));
        var reverse = await segments.CreateFromActivityAsync(new CreateSegmentRequest(owner.Id, firstActivity.Id, "Opposite direction", 20, 40, ReverseDirection: true));
        var noElevation = await segments.CreateAsync(new CreateSegmentPathRequest(owner.Id, "Path without elevation", SportKind.Running,
            TestSupport.Track(80).Select(point => point with { Latitude = point.Latitude + 1, ElevationMeters = null }).ToArray()));
        await segments.CreateSubsegmentAsync(new CreateSubsegmentRequest(noElevation, "Unrecorded slope", 20, 50));
        var captureDirectory = Path.Combine(root, "artifacts", "segments-ui");
        Directory.CreateDirectory(captureDirectory);
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
            var externalRequests = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            page.Console += (_, message) => { if (message.Type == "error") errors.Add(message.Text); };
            page.Request += (_, request) =>
            {
                if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && uri.Host != "127.0.0.1")
                    externalRequests.Add(request.Url);
            };
            var navigation = new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle };
            var parentUrl = origin + $"/segments/{parent}";
            await page.GotoAsync(parentUrl, navigation);
            var childRow = page.Locator($"[data-subsegment-id='{child}']");
            await Assertions.Expect(childRow.Locator(".subsegment-passes li")).ToHaveCountAsync(2);
            await Assertions.Expect(childRow.Locator(".subsegment-passes")).ToContainTextAsync("0:20");
            try { await Assertions.Expect(page.Locator(".segment-section-marker")).ToHaveCountAsync(2); }
            catch (PlaywrightException exception)
            {
                await page.ScreenshotAsync(new() { Path = Path.Combine(captureDirectory, "map-failure.png"), FullPage = true });
                throw new InvalidOperationException($"Section map failed. Browser errors: {string.Join(Environment.NewLine, errors)}", exception);
            }
            await Assertions.Expect(page.Locator(".segment-section-marker").First).ToBeVisibleAsync();
            Assert.Empty(errors);
            var ranges = await page.Locator(".section-distance-track rect").EvaluateAllAsync<double[][]>(
                "rects => rects.map(rect => [Number(rect.getAttribute('x')), Number(rect.getAttribute('width'))])");
            Assert.Equal(2, ranges.Length);
            Assert.InRange(ranges[0][0], 124, 126);
            Assert.InRange(ranges[0][1], 124, 126);
            Assert.InRange(ranges[1][0], 624, 626);

            // Count fitBounds calls to catch a selection that unexpectedly resets a panned map.
            await page.EvaluateAsync("""
                async () => {
                    const maplibre = await import('/vendor/maplibre-gl.mjs');
                    const original = maplibre.Map.prototype.fitBounds;
                    window.sectionFits = 0;
                    maplibre.Map.prototype.fitBounds = function (...args) {
                        window.sectionFits++;
                        return original.apply(this, args);
                    };
                }
                """);
            var mapId = await page.Locator("[data-inspection-map]").GetAttributeAsync("id");
            var sectionButton = page.Locator(".section-position-list button").First;
            await sectionButton.FocusAsync();
            await sectionButton.PressAsync("Enter");
            await Assertions.Expect(sectionButton).ToHaveAttributeAsync("aria-pressed", "true");
            await Assertions.Expect(page.Locator("[data-inspection-map]")).ToHaveAttributeAsync("data-selected-section", child.ToString());
            Assert.Equal(mapId, await page.Locator("[data-inspection-map]").GetAttributeAsync("id"));
            Assert.Equal(0, await page.EvaluateAsync<int>("window.sectionFits"));
            await Assertions.Expect(page).ToHaveURLAsync(parentUrl + $"?section={child}");
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator(".section-position-list button").First).ToHaveAttributeAsync("aria-pressed", "true");

            await page.Locator(".effort-table tbody tr").Nth(1).GetByRole(AriaRole.Link).ClickAsync();
            await Assertions.Expect(childRow.Locator(".subsegment-passes")).ToContainTextAsync("0:40");
            var selectedParentUrl = page.Url;
            await childRow.Locator(".subsegment-passes a").First.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"/segments/{child}\\?effort="));
            await Assertions.Expect(page.GetByRole(AriaRole.Navigation, new() { Name = "Parent segments" })).ToContainTextAsync("Two park laps");
            await Assertions.Expect(page.Locator($"[data-subsegment-id='{grandchild}']")).ToBeVisibleAsync();
            await page.GoBackAsync();
            await Assertions.Expect(page).ToHaveURLAsync(selectedParentUrl);
            await Assertions.Expect(childRow.Locator(".subsegment-passes")).ToContainTextAsync("0:40");
            await Assertions.Expect(page.Locator(".segment-section-marker")).ToHaveCountAsync(2);
            await Assertions.Expect(page.Locator(".segment-section-marker").First).ToBeVisibleAsync();
            await page.Locator(".segment-section-marker").Last.FocusAsync();
            await page.Locator(".segment-section-marker").Last.PressAsync("Enter");
            await Assertions.Expect(page.Locator("[data-inspection-map]")).ToHaveAttributeAsync("data-selected-section", child.ToString());

            foreach (var width in new[] { 320, 360, 375, 768, 1121, 1280, 1920 })
            {
                await page.SetViewportSizeAsync(width, 1000);
                await AssertNoDocumentOverflowAsync(page, $"Nested segment detail at {width}px");
                if (width is 375 or 1280)
                {
                    await Assertions.Expect(page.Locator(".segment-section-marker").First).ToBeVisibleAsync();
                    await page.Locator(".topbar").EvaluateAsync("element => element.style.visibility = 'hidden'");
                    await page.Locator(".subsegment-breakdown").ScreenshotAsync(new() { Path = Path.Combine(captureDirectory, $"breakdown-{width}.png") });
                    await page.Locator(".segment-definition-explorer").ScreenshotAsync(new() { Path = Path.Combine(captureDirectory, $"path-{width}.png") });
                    await page.Locator(".topbar").EvaluateAsync("element => element.style.removeProperty('visibility')");
                }
            }
            await page.SetViewportSizeAsync(1280, 1000);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await page.EvaluateAsync("document.documentElement.style.fontSize='200%'; document.body.style.fontSize='30px'");
            await AssertNoDocumentOverflowAsync(page, "Nested segments with enlarged text");
            await page.EvaluateAsync("document.documentElement.style.removeProperty('font-size'); document.body.style.removeProperty('font-size')");

            await page.GetByRole(AriaRole.Link, new() { Name = "Add sub-segment", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByLabel("Reverse direction after trimming")).ToHaveCountAsync(0);
            await page.GetByLabel("Name", new() { Exact = true }).FillAsync("Finish section with a longer descriptive name");
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Create sub-segment", Exact = true })).ToBeDisabledAsync();
            var startSlider = page.GetByRole(AriaRole.Slider, new() { Name = "Segment start position" });
            await startSlider.FocusAsync();
            await startSlider.PressAsync("ArrowRight");
            foreach (var width in new[] { 375, 768, 1280, 1920 })
            {
                await page.SetViewportSizeAsync(width, 1000);
                await AssertNoDocumentOverflowAsync(page, $"Sub-segment creator at {width}px");
            }
            await page.GetByRole(AriaRole.Button, new() { Name = "Create sub-segment", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"/segments/{parent}\\?section="));
            await Assertions.Expect(page.Locator(".subsegment-table tbody tr")).ToHaveCountAsync(2);
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);

            await page.GotoAsync(origin + $"/segments/{otherParent}/subsegments/new", navigation);
            await page.GetByRole(AriaRole.Button, new() { Name = "Use existing segment", Exact = true }).ClickAsync();
            await page.GetByLabel("Existing segment").SelectOptionAsync(reverse.ToString());
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("same direction");
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Attach segment", Exact = true })).ToHaveCountAsync(0);
            await page.GetByLabel("Existing segment").SelectOptionAsync(child.ToString());
            await Assertions.Expect(page.GetByRole(AriaRole.Region, new() { Name = "Parent and existing sub-segment preview" })).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Attach segment", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(origin + $"/segments/{otherParent}?section={child}");
            Assert.Equal(2, (await segments.GetAsync(child))!.Parents.Count);

            await page.GotoAsync(origin + "/segments", navigation);
            var library = page.Locator(".segment-library");
            await Assertions.Expect(library.GetByRole(AriaRole.Link, new() { Name = "Waterside stretch", Exact = true }).First).Not.ToBeVisibleAsync();
            var parentBranch = library.Locator("li.segment-branch").Filter(new() { Has = page.GetByRole(AriaRole.Link, new() { Name = "Two park laps", Exact = true }) }).First;
            await parentBranch.Locator("summary").First.FocusAsync();
            await parentBranch.Locator("summary").First.PressAsync("Enter");
            await Assertions.Expect(parentBranch.GetByRole(AriaRole.Link, new() { Name = "Waterside stretch", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(parentBranch.GetByText("Shared in 2 segments", new() { Exact = true })).ToBeVisibleAsync();
            var childBranch = parentBranch.Locator("li.segment-branch").Filter(new() { Has = page.GetByRole(AriaRole.Link, new() { Name = "Waterside stretch", Exact = true }) }).First;
            await childBranch.Locator("summary").ClickAsync();
            await Assertions.Expect(childBranch.GetByRole(AriaRole.Link, new() { Name = "Last straight", Exact = true })).ToBeVisibleAsync();
            foreach (var width in new[] { 320, 375, 768, 1280, 1920 })
            {
                await page.SetViewportSizeAsync(width, 1000);
                await AssertNoDocumentOverflowAsync(page, $"Expanded segment library at {width}px");
                if (width is 375 or 1280)
                {
                    await page.Locator(".topbar").EvaluateAsync("element => element.style.visibility = 'hidden'");
                    await library.ScreenshotAsync(new() { Path = Path.Combine(captureDirectory, $"library-{width}.png") });
                    await page.Locator(".topbar").EvaluateAsync("element => element.style.removeProperty('visibility')");
                }
            }

            await page.GotoAsync(parentUrl + $"?section={child}", navigation);
            var before = (await segments.GetAsync(child))!.Efforts.Select(effort => effort.Id).Order().ToArray();
            await childRow.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Remove from this segment") }).ClickAsync();
            await Assertions.Expect(childRow).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("history and other links were kept");
            var detached = (await segments.GetAsync(child))!;
            Assert.Equal(before, detached.Efforts.Select(effort => effort.Id).Order());
            Assert.Equal(otherParent, Assert.Single(detached.Parents).Id);
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(childRow).ToHaveCountAsync(0);

            await page.GotoAsync(origin + $"/segments/{noElevation}", navigation);
            await Assertions.Expect(page.Locator(".segment-grade-profile")).ToContainTextAsync("Not recorded in the source");
            await Assertions.Expect(page.GetByRole(AriaRole.Region, new() { Name = "Sub-segments along the path" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("No matching pass in this effort", new() { Exact = true })).ToBeVisibleAsync();
            await page.GotoAsync(origin + $"/segments/{grandchild}", navigation);
            await Assertions.Expect(page.GetByText("No sub-segments yet.", new() { Exact = false })).ToBeVisibleAsync();
            await page.GotoAsync(origin + $"/segments/{Guid.NewGuid()}/subsegments/new", navigation);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Segment unavailable", Exact = true })).ToBeVisibleAsync();
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

    private sealed class SegmentBrowserDbFactory(DbContextOptions<ExplorerDbContext> options) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);
        public Task<ExplorerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
