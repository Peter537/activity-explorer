using System.Text;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    [Fact]
    public async Task Rowing_import_charts_filters_and_indoor_records_work_in_the_browser()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var root = FindRepositoryRoot();
        var dataRoot = TestSupport.NewDirectory();
        var inputRoot = TestSupport.NewDirectory();
        var input = TestSupport.RowingFit(inputRoot);
        var origin = $"http://127.0.0.1:{ReservePort()}";
        var output = new StringBuilder();
        using var process = StartApplication(FindWebAssembly(root), dataRoot, origin, output);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = 1280, Height = 900 } });
            var errors = new List<string>();
            page.PageError += (_, message) => errors.Add(message);
            await page.GotoAsync(origin + "/profiles", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByLabel("Display name").FillAsync("Browser rower");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create profile", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Browser rower", Exact = true })).ToBeVisibleAsync();
            await page.GotoAsync(origin + "/imports", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByLabel("Choose one or more files").SetInputFilesAsync(input);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Queue selected files", Exact = true }).ClickAsync();

            var options = new DbContextOptionsBuilder<ExplorerDbContext>()
                .UseSqlite($"Data Source={Path.Combine(dataRoot, "activity-explorer.db")}").Options;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            Guid activityId = Guid.Empty;
            while (DateTimeOffset.UtcNow < deadline)
            {
                await using var db = new ExplorerDbContext(options);
                if (await db.StatisticSnapshots.AnyAsync(record => record.Scope == RecordScope.Indoor))
                {
                    activityId = await db.Activities.Select(activity => activity.Id).SingleAsync();
                    break;
                }
                await Task.Delay(100);
            }
            Assert.NotEqual(Guid.Empty, activityId);
            await page.GotoAsync(origin + "/activities?sport=Rowing", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Sport", Exact = true })).ToHaveValueAsync("Rowing");
            await Assertions.Expect(page.Locator(".activity-row")).ToHaveCountAsync(1);
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Sport", Exact = true })).ToHaveValueAsync("Rowing");

            var detailUrl = $"{origin}/activities/{activityId}";
            await page.GotoAsync(detailUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create segment", Exact = true })).ToBeDisabledAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create route", Exact = true })).ToBeDisabledAsync();
            Assert.Empty(await page.Locator("#activity-map").AllAsync());
            Assert.DoesNotContain("Pedal revolutions", await page.Locator("#activity-details").InnerTextAsync(), StringComparison.Ordinal);
            Assert.Contains("Total strokes", await page.Locator("#activity-details").InnerTextAsync(), StringComparison.Ordinal);
            await Assertions.Expect(page.Locator(".time-series-chart[data-unit='spm']")).ToBeVisibleAsync();
            var pace = page.Locator(".time-series-chart[data-unit='min/500 m']");
            await Assertions.Expect(pace).ToBeVisibleAsync();
            Assert.Contains("3:20 /500 m", await page.Locator("main").InnerTextAsync(), StringComparison.Ordinal);
            await pace.Locator(".chart-accessible-values > summary").ClickAsync();
            await pace.GetByRole(AriaRole.Slider).FocusAsync();
            await pace.GetByRole(AriaRole.Slider).PressAsync("ArrowRight");
            Assert.Contains("min/500 m", await pace.GetByRole(AriaRole.Slider).GetAttributeAsync("aria-valuetext"), StringComparison.Ordinal);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Distance", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".synchronized-charts")).ToHaveAttributeAsync("data-axis-kind", "distance");

            foreach (var width in new[] { 375, 768, 1280, 1920 })
            {
                await AssertNoHorizontalOverflowAsync(page, detailUrl, width);
                await AssertNoHorizontalOverflowAsync(page, origin + "/records?scope=indoor", width);
                await Assertions.Expect(page.GetByRole(AriaRole.Table, new PageGetByRoleOptions { Name = "Rowing distance bests", Exact = true })).ToBeVisibleAsync();
            }
            await page.SetViewportSizeAsync(1280, 900);
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator("#record-scope")).ToHaveValueAsync("indoor");
            await page.Locator("#record-scope").SelectOptionAsync("outdoor");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "No outdoor only records calculated" })).ToBeVisibleAsync();
            await page.GoBackAsync(new PageGoBackOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator("#record-scope")).ToHaveValueAsync("indoor");
            await page.GoForwardAsync(new PageGoForwardOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator("#record-scope")).ToHaveValueAsync("outdoor");
            await page.GotoAsync(origin + "/records?scope=indoor", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            var records = page.GetByRole(AriaRole.Table, new PageGetByRoleOptions { Name = "Rowing distance bests", Exact = true });
            Assert.Equal(3, await records.Locator("tbody tr").CountAsync());
            await records.GetByRole(AriaRole.Link).First.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(detailUrl);
            await page.GotoAsync(origin + "/profiles", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByLabel("Display name").FillAsync("Empty profile");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create profile", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Empty profile", Exact = true })).ToBeVisibleAsync();
            await page.GotoAsync(origin + "/records?scope=indoor", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByLabel("Profile selector").SelectOptionAsync(new SelectOptionValue { Label = "Empty profile" });
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "No indoor only records calculated" })).ToBeVisibleAsync();
            await page.GetByLabel("Profile selector").SelectOptionAsync(new SelectOptionValue { Label = "Browser rower" });
            await Assertions.Expect(page.Locator("#record-scope")).ToHaveValueAsync("indoor");
            await Assertions.Expect(records).ToBeVisibleAsync();
            await Assertions.Expect(page).ToHaveURLAsync(origin + "/records?scope=indoor");
            await page.GotoAsync(origin + "/records?scope=invalid", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page).ToHaveURLAsync(origin + "/records");
            await page.GotoAsync(origin + "/map?sport=Rowing", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Sport", Exact = true })).ToHaveValueAsync("Rowing");
            Assert.DoesNotContain(errors, error => !IsKnownHeadlessMapLibreError(error));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
            await DeleteDirectoryAsync(inputRoot);
        }
    }
}
