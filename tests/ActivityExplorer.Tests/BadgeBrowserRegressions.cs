using System.Text;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    [Fact]
    public async Task Badges_preserve_history_profiles_filters_evidence_and_responsive_layout()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var root = FindRepositoryRoot();
        var dataRoot = TestSupport.NewDirectory();
        var owner = Guid.NewGuid();
        var emptyOwner = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite($"Data Source={Path.Combine(dataRoot, "activity-explorer.db")}").Options;
        await using (var db = new ExplorerDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Owners.AddRange(new OwnerProfile { Id = owner, DisplayName = "Badge Example", TimeZoneId = "UTC" }, new OwnerProfile { Id = emptyOwner, DisplayName = "Empty Example" });
            db.Activities.Add(new Activity { OwnerId = owner, Title = "Fictional historic ride", Sport = SportKind.Cycling, NaturalFingerprint = "badge-2018", StartTimeUtc = new(2018, 4, 2, 10, 0, 0, TimeSpan.Zero), DistanceMeters = 50000, MovingTimeSeconds = 3600 });
            for (var day = 2; day <= 5; day++)
                db.Activities.Add(new Activity { OwnerId = owner, Title = $"Fictional April ride {day}", Sport = SportKind.Cycling, NaturalFingerprint = $"badge-april-{day}", StartTimeUtc = new(2026, 4, day, 10, 0, 0, TimeSpan.Zero), DistanceMeters = 100000, MovingTimeSeconds = 7200 });
            db.Activities.Add(new Activity { OwnerId = owner, Title = "Later activity excluded from April", Sport = SportKind.Cycling, NaturalFingerprint = "badge-may", StartTimeUtc = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero), DistanceMeters = 1000000, MovingTimeSeconds = 7200 });
            await db.SaveChangesAsync();
        }
        var origin = $"http://127.0.0.1:{ReservePort()}";
        var output = new StringBuilder();
        using var process = StartApplication(FindWebAssembly(root), dataRoot, origin, output);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 1000 }, Locale = "en-GB" });
            var errors = new List<string>();
            page.PageError += (_, message) => errors.Add(message);
            await page.GotoAsync(origin + "/badges", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator(".badge-profile-grid article")).ToHaveCountAsync(2);
            await page.GetByRole(AriaRole.Link, new() { Name = "Explore Badge Example's badges" }).ClickAsync();
            await Assertions.Expect(page.Locator(".badge-grid")).ToBeVisibleAsync();
            var all = origin + $"/badges?owner={owner}&month=2026-04&view=month";
            await page.GotoAsync(all, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator(".badge-card")).ToHaveCountAsync(24);
            await Assertions.Expect(page.Locator(".snapshot-note")).ToContainTextAsync(new DateOnly(2026, 4, 30).ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture));
            await page.GetByRole(AriaRole.Link, new() { Name = "Next", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("page=2"));
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator(".badge-pagination")).ToContainTextAsync("Page 2");
            await page.GoBackAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator(".badge-pagination")).ToContainTextAsync("Page 1");
            await page.GetByLabel("Sport", new() { Exact = true }).SelectOptionAsync("cycling");
            await page.GetByLabel("Badge type").SelectOptionAsync("monthly");
            await page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync("completed");
            var fourHundred = page.Locator(".badge-card").Filter(new() { HasText = "Monthly distance · 400 km" });
            await Assertions.Expect(fourHundred).ToBeVisibleAsync();
            await Assertions.Expect(fourHundred.Locator("svg text").Last).ToHaveTextAsync("2026-04");
            Assert.Equal("400", await fourHundred.Locator("progress").GetAttributeAsync("value"));
            await Assertions.Expect(page.Locator(".badge-card").Filter(new() { HasText = "800 km" })).ToHaveCountAsync(0);
            await fourHundred.ClickAsync();
            await Assertions.Expect(page.Locator(".detail-art svg text").Last).ToHaveTextAsync("2026-04");
            await Assertions.Expect(page.Locator(".detail-copy")).ToContainTextAsync(new DateOnly(2026, 4, 5).ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture));
            await Assertions.Expect(page.Locator(".badge-evidence li")).ToHaveCountAsync(4);
            Assert.DoesNotContain("Later activity", await page.Locator(".badge-evidence").InnerTextAsync(), StringComparison.Ordinal);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Related tiers and editions" })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".edition-year")).ToHaveCountAsync(DateTimeOffset.UtcNow.Year - 2018 + 1);
            var detailUrl = page.Url;
            foreach (var width in new[] { 320, 360, 375, 768, 1121, 1280, 1920 })
            {
                await AssertNoHorizontalOverflowAsync(page, all, width);
                await AssertNoHorizontalOverflowAsync(page, detailUrl, width);
            }
            await page.SetViewportSizeAsync(1280, 1000);
            await page.GotoAsync(all, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.ScreenshotAsync(new() { Path = Path.Combine(dataRoot, "badges-desktop.png"), FullPage = true });
            await page.SetViewportSizeAsync(375, 850);
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.ScreenshotAsync(new() { Path = Path.Combine(dataRoot, "badges-mobile.png"), FullPage = true });
            await page.GetByRole(AriaRole.Link, new() { Name = "Collection", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".badge-results-heading")).ToContainTextAsync("families");
            await page.GetByLabel("Search badges").FillAsync("does-not-exist");
            await page.GetByLabel("Search badges").PressAsync("Enter");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "No matching badges" })).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "Reset filters", Exact = true }).First.ClickAsync();
            await Assertions.Expect(page.Locator(".badge-card").First).ToBeVisibleAsync();
            await page.GotoAsync(origin + $"/badges?owner={emptyOwner}", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator(".badge-summary")).ToContainTextAsync("0 points");
            await Assertions.Expect(page.Locator(".badge-card").First).ToBeVisibleAsync();
            await page.SetViewportSizeAsync(1280, 900);
            await page.GetByLabel("Profile selector").SelectOptionAsync(owner.ToString());
            await Assertions.Expect(page.Locator(".badge-summary")).ToContainTextAsync("Badge Example");
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByLabel("Profile selector")).ToHaveValueAsync(owner.ToString());
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            Assert.Equal("none", await page.Locator(".badge-card").First.EvaluateAsync<string>("element => getComputedStyle(element).transitionProperty"));
            await page.Locator(".badge-card").First.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(page.Locator(".badge-detail")).ToBeVisibleAsync();
            await page.EvaluateAsync("() => document.documentElement.style.fontSize = '200%'");
            await AssertNoDocumentOverflowAsync(page, "Badges with enlarged text");
            await page.GotoAsync(origin + $"/badges/unknown-badge?owner={owner}", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("unavailable");
            await page.GotoAsync(origin + "/badges?owner=invalid", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("profile is unavailable");
            Assert.Empty(errors);
            TestContext.Current.TestOutputHelper!.WriteLine($"Badge screenshots: {dataRoot}");
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
}
