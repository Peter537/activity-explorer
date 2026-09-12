using System.Text;
using System.Text.RegularExpressions;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    [Fact]
    public async Task Benchmark_attempts_preserve_links_modes_context_and_responsive_tables()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var root = FindRepositoryRoot();
        var dataRoot = TestSupport.NewDirectory();
        var options = new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite($"Data Source={Path.Combine(dataRoot, "activity-explorer.db")}").Options;
        var owner = new OwnerProfile { DisplayName = "Attempt browser athlete" };
        var emptyOwner = new OwnerProfile { DisplayName = "Empty attempt athlete" };
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var points = Enumerable.Range(0, 1_001).Select(index => new TrackPoint(start.AddSeconds(index), 0, index * 0.00009,
            index * 10, 10, 10, 130, 80, 200, 15)).ToArray();
        var ride = BrowserActivity(owner.Id, "Repeated effort browser ride", SportKind.Cycling, points);
        ride.DistanceMeters = 10_000;
        ride.ElevationGainMeters = 50;
        await using (var db = new ExplorerDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.AddRange(owner, emptyOwner, ride);
            var indoor = BrowserActivity(owner.Id, "Indoor attempt ride", SportKind.Cycling, points);
            indoor.IsIndoor = true;
            db.Add(indoor);
            await db.SaveChangesAsync();
        }
        await new StatisticsService(new AttemptBrowserDbFactory(options)).RecomputeAsync(owner.Id);
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
            var navigationOptions = new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle };
            await page.GotoAsync(origin + "/records?sport=cycling&scope=outdoor", navigationOptions);
            await page.GetByLabel("Profile selector").SelectOptionAsync(owner.Id.ToString());
            var benchmarks = page.Locator(".benchmark-link");
            await Assertions.Expect(benchmarks.First).ToBeVisibleAsync();
            await Assertions.Expect(benchmarks.First).ToHaveAttributeAsync("href", new Regex($"&owner={owner.Id}$"));
            var links = await benchmarks.EvaluateAllAsync<string[]>("links => links.map(link => link.getAttribute('href'))");
            Assert.Contains(links, link => link.Contains("kind=distance&"));
            Assert.Contains(links, link => link.Contains("kind=distanceeffort&"));
            Assert.Contains(links, link => link.Contains("kind=timeddistanceeffort&"));
            Assert.Contains(links, link => link.Contains("kind=powercurve&"));
            // Every achieved benchmark has a usable history, including whole-activity categories.
            foreach (var link in links)
            {
                await page.GotoAsync(origin + link, navigationOptions);
                await Assertions.Expect(page.Locator(".attempt-table tbody tr").First).ToBeVisibleAsync();
                await Assertions.Expect(page.GetByLabel("Profile selector")).ToHaveValueAsync(owner.Id.ToString());
                await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Back to Records" })).ToHaveAttributeAsync("href", "/records?sport=cycling&scope=outdoor");
            }
            await page.GetByRole(AriaRole.Link, new() { Name = "Back to Records" }).ClickAsync();
            var fiveKm = page.GetByRole(AriaRole.Link, new() { Name = "5 km attempts for Cycling", Exact = true });
            await fiveKm.FocusAsync();
            await fiveKm.PressAsync("Tab");
            await Assertions.Expect(fiveKm.Locator("..").Locator(".activity-link")).ToBeFocusedAsync();
            await fiveKm.PressAsync("Enter");
            await Assertions.Expect(page.Locator(".attempt-table tbody tr")).ToHaveCountAsync(1);
            await page.GetByLabel("Attempts per activity").SelectOptionAsync("multiple");
            await Assertions.Expect(page.Locator(".attempt-table tbody tr")).ToHaveCountAsync(2);
            var detailUrl = page.Url;
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByLabel("Attempts per activity")).ToHaveValueAsync("multiple");
            await Assertions.Expect(page.GetByLabel("Profile selector")).ToHaveValueAsync(owner.Id.ToString());
            await page.GoBackAsync();
            await Assertions.Expect(page.GetByLabel("Attempts per activity")).ToHaveValueAsync("best");
            await page.GoForwardAsync();
            await Assertions.Expect(page.GetByLabel("Attempts per activity")).ToHaveValueAsync("multiple");
            foreach (var width in new[] { 320, 360, 375, 768, 1121, 1280, 1920 })
            {
                await page.SetViewportSizeAsync(width, 1000);
                await page.Locator(".sidebar").EvaluateAsync("element => Promise.all(element.getAnimations().map(animation => animation.finished))");
                await AssertNoDocumentOverflowAsync(page, $"Benchmark attempts at {width}px");
                foreach (var cell in await page.Locator(".attempt-table tbody tr").First.Locator("td, th").AllAsync())
                    await Assertions.Expect(cell).ToBeVisibleAsync();
                if (width is 375 or 1280)
                {
                    var captures = Path.Combine(root, "artifacts", "record-attempts-ui");
                    Directory.CreateDirectory(captures);
                    await page.ScreenshotAsync(new() { Path = Path.Combine(captures, $"attempts-{width}.png"), FullPage = true });
                }
            }
            await page.SetViewportSizeAsync(1280, 1000);
            await page.GetByLabel("Attempts per activity").FocusAsync();
            await Assertions.Expect(page.GetByLabel("Attempts per activity")).ToBeFocusedAsync();
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await page.EvaluateAsync("document.documentElement.style.fontSize='200%'; document.body.style.fontSize='30px'");
            await AssertNoDocumentOverflowAsync(page, "Attempts with enlarged text");
            await page.EvaluateAsync("document.documentElement.style.removeProperty('font-size'); document.body.style.removeProperty('font-size')");
            await page.Locator(".attempt-table tbody a").First.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(origin + $"/activities/{ride.Id}");
            await page.GoBackAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("^" + Regex.Escape(detailUrl) + "$"));
            await page.GetByLabel("Profile selector").SelectOptionAsync(emptyOwner.Id.ToString());
            await Assertions.Expect(page.Locator(".empty-state")).ToContainTextAsync("No matching attempts");
            await page.GetByLabel("Profile selector").SelectOptionAsync("");
            await Assertions.Expect(page.Locator(".attempt-table tbody tr")).ToHaveCountAsync(2);
            await page.GotoAsync(origin + links.First(link => link.Contains("kind=powercurve&key=5%20s")) + "&mode=multiple", navigationOptions);
            await Assertions.Expect(page.Locator(".attempt-table tbody tr")).ToHaveCountAsync(50);
            await page.GetByRole(AriaRole.Link, new() { Name = "Next", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".attempt-pagination")).ToContainTextAsync("Page 2 of 4");
            await page.GetByLabel("Profile selector").SelectOptionAsync(emptyOwner.Id.ToString());
            await Assertions.Expect(page.Locator(".empty-state")).ToContainTextAsync("No matching attempts");
            Assert.DoesNotContain("page=", page.Url);
            await page.GotoAsync(origin + "/records/attempts?sport=cycling&kind=distanceeffort&key=invalid", navigationOptions);
            await Assertions.Expect(page.Locator(".empty-state")).ToContainTextAsync("Benchmark not found");
            // A damaged payload must expose a retry state, never silently omit an activity.
            await using (var db = new ExplorerDbContext(options))
            {
                (await db.ActivityStreams.SingleAsync(stream => stream.ActivityId == ride.Id)).CompressedPayload = [0, 1, 2];
                await db.SaveChangesAsync();
            }
            await page.GotoAsync(detailUrl, navigationOptions);
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Attempts could not be loaded");
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Try again" })).ToBeVisibleAsync();
            Assert.Empty(errors);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    private sealed class AttemptBrowserDbFactory(DbContextOptions<ExplorerDbContext> options) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);
        public Task<ExplorerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
