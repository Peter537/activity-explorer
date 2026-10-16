using System.Text;
using System.Text.RegularExpressions;
using ActivityExplorer.Core.Domain;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    [Fact]
    public async Task Todo_acceptance_journey_connects_saved_search_discovery_and_period_records()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var search = await SeedCrossViewBrowserDataAsync(dataRoot);
        var library = await SeedLibraryBrowserDataAsync(dataRoot);
        var finding = await SeedFindingBrowserDataAsync(dataRoot);
        var records = await SeedPeriodRecordsBrowserDataAsync(dataRoot);
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
            page.PageError += (_, error) => errors.Add(error);
            await page.GotoAsync(origin + "/activities", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await SelectTodoJourneyProfileAsync(page, search.Search.EastOwner);
            var filters = page.GetByRole(AriaRole.Form, new() { Name = "Activity filters", Exact = true });
            await filters.GetByLabel("Search", new() { Exact = true }).FillAsync("æble grøn");
            await filters.GetByRole(AriaRole.Combobox, new() { Name = "Sport", Exact = true }).SelectOptionAsync(nameof(SportKind.Cycling));
            await filters.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true }).SelectOptionAsync("custom");
            await filters.GetByLabel("From", new() { Exact = true }).FillAsync("2026-01-02");
            await filters.GetByLabel("To", new() { Exact = true }).FillAsync("2026-01-02");
            await filters.GetByLabel("To", new() { Exact = true }).PressAsync("Tab");
            await filters.GetByRole(AriaRole.Combobox, new() { Name = "Tag matching", Exact = true }).SelectOptionAsync("all");
            var included = filters.GetByRole(AriaRole.Group, new() { Name = "Tags — match all", Exact = true });
            await included.GetByRole(AriaRole.Checkbox, new() { Name = "Training", Exact = true }).CheckAsync();
            await included.GetByRole(AriaRole.Checkbox, new() { Name = "Intervals", Exact = true }).CheckAsync();
            await filters.GetByRole(AriaRole.Group, new() { Name = "Exclude tags", Exact = true })
                .GetByRole(AriaRole.Checkbox, new() { Name = "Commute", Exact = true }).CheckAsync();
            await filters.GetByRole(AriaRole.Combobox, new() { Name = "Tag presence", Exact = true }).SelectOptionAsync("tagged");
            var secondary = filters.Locator("details.more-filters");
            if (!await secondary.EvaluateAsync<bool>("element => element.open")) await secondary.Locator("summary").ClickAsync();
            await filters.GetByRole(AriaRole.Combobox, new() { Name = "Power", Exact = true }).SelectOptionAsync("yes");
            await filters.GetByLabel("Device", new() { Exact = true }).FillAsync("Fictional Edge");
            await filters.GetByRole(AriaRole.Checkbox, new() { Name = "Exact device name", Exact = true }).CheckAsync();
            await filters.GetByRole(AriaRole.Combobox, new() { Name = "Text matching", Exact = true }).SelectOptionAsync("words");
            var gear = filters.GetByRole(AriaRole.Combobox, new() { Name = "Gear", Exact = true });
            await Assertions.Expect(gear.GetByRole(AriaRole.Option, new() { NameRegex = new Regex("^Road bike$", RegexOptions.IgnoreCase) })).ToHaveCountAsync(1);
            var gearValues = await gear.Locator("option").EvaluateAllAsync<string[]>("options => options.map(option => option.value)");
            await gear.SelectOptionAsync(gearValues.Single(value => value.Equals("Road bike", StringComparison.OrdinalIgnoreCase)));
            await filters.GetByRole(AriaRole.Combobox, new() { Name = "Environment", Exact = true }).SelectOptionAsync("no");
            foreach (var (label, value) in new[]
            {
                ("Min distance (km)", "10"), ("Max distance (km)", "40"),
                ("Min moving time (min)", "30"), ("Max moving time (min)", "60"),
                ("Min ascent (m)", "100"), ("Max ascent (m)", "200")
            }) await filters.GetByLabel(label, new() { Exact = true }).FillAsync(value);
            await filters.GetByRole(AriaRole.Button, new() { Name = "Apply filters", Exact = true }).ClickAsync();
            await AssertReportingTotalsAsync(page, "30", $"{765d:N1} km", "22:45:00", $"{3465d:N0} m");
            var saved = page.Locator(".saved-searches");
            await saved.Locator("summary").ClickAsync();
            await saved.GetByLabel("Search name", new() { Exact = true }).FillAsync("Complete TODO acceptance search");
            await saved.GetByRole(AriaRole.Button, new() { Name = "Save new search", Exact = true }).ClickAsync();
            await Assertions.Expect(saved.GetByRole(AriaRole.Status)).ToContainTextAsync("Saved search.");
            var savedId = await saved.GetByRole(AriaRole.Combobox, new() { Name = "Saved search", Exact = true }).InputValueAsync();
            Assert.True(Guid.TryParse(savedId, out _));
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Button, new() { Name = "Switch to Search East", Exact = true }).ClickAsync();
            await AssertReportingTotalsAsync(page, "30", $"{765d:N1} km", "22:45:00", $"{3465d:N0} m");
            await page.GetByRole(AriaRole.Button, new() { Name = "Reset", Exact = true }).ClickAsync();
            if (!await saved.EvaluateAsync<bool>("element => element.open")) await saved.Locator("summary").ClickAsync();
            await saved.GetByRole(AriaRole.Combobox, new() { Name = "Saved search", Exact = true }).SelectOptionAsync(savedId);
            await saved.GetByRole(AriaRole.Button, new() { Name = "Open search", Exact = true }).ClickAsync();
            await AssertReportingTotalsAsync(page, "30", $"{765d:N1} km", "22:45:00", $"{3465d:N0} m");
            await page.GetByRole(AriaRole.Link, new() { Name = "Show matching activities on map", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".world-map .maplibregl-canvas")).ToHaveAttributeAsync("data-activities-features", "29");
            await page.GoBackAsync();
            await AssertReportingTotalsAsync(page, "30", $"{765d:N1} km", "22:45:00", $"{3465d:N0} m");
            await page.GetByRole(AriaRole.Link, new() { Name = "View progress", Exact = true }).ClickAsync();
            await AssertProgressTotalsAsync(page, "30", $"{765d:N1} km", "22:45:00", $"{3465d:N0} m", "1");
            await page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("^Browse activities recorded with ") }).ClickAsync();
            await AssertReportingTotalsAsync(page, "30", $"{765d:N1} km", "22:45:00", $"{3465d:N0} m");
            Assert.Contains("deviceExact=yes", page.Url, StringComparison.Ordinal);
            Assert.Contains("excludeTags=", page.Url, StringComparison.Ordinal);

            await page.GetByRole(AriaRole.Link, new() { Name = "Segments", Exact = true }).ClickAsync();
            await SelectTodoJourneyProfileAsync(page, library.OwnerId);
            await page.GetByLabel("Search segments", new() { Exact = true }).FillAsync("Needle");
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Segment sport", Exact = true }).SelectOptionAsync(nameof(SportKind.Running));
            await page.GetByRole(AriaRole.Button, new() { Name = "Apply segment filters", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".segment-library").GetByRole(AriaRole.Link, new() { Name = "Needle turn", Exact = true })).ToHaveCountAsync(2);
            await page.Locator(".segment-library").GetByRole(AriaRole.Link, new() { Name = "One park lap", Exact = true }).ClickAsync();
            await page.Locator($"[data-effort-id='{library.SecondPass}']").GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Use as baseline") }).ClickAsync();
            await page.Locator($"[data-effort-id='{library.FirstPass}']").GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Use as comparison") }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Baseline", Exact = true })).ToHaveValueAsync(library.SecondPass.ToString());
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Comparison", Exact = true })).ToHaveValueAsync(library.FirstPass.ToString());

            await page.GetByRole(AriaRole.Link, new() { Name = "Imports", Exact = true }).ClickAsync();
            await SelectTodoJourneyProfileAsync(page, finding.ImportOwner);
            var history = page.Locator("details.import-history");
            if (!await history.EvaluateAsync<bool>("element => element.open")) await history.Locator("summary").First.ClickAsync();
            await history.GetByLabel("Search imports", new() { Exact = true }).FillAsync("Archive target 01");
            await history.GetByRole(AriaRole.Button, new() { Name = "Apply history filters", Exact = true }).ClickAsync();
            await Assertions.Expect(history.Locator(".import-entry")).ToHaveCountAsync(1);
            await history.GetByRole(AriaRole.Link, new() { Name = "View linked activities", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".activity-totals dd").First).ToHaveTextAsync("2");
            Assert.Equal(finding.LinkedActivities.Order(), (await OrganizationVisibleIdsAsync(page)).Order());
            Assert.Contains($"importBatch={finding.LinkedBatch}", page.Url, StringComparison.Ordinal);

            await page.GetByRole(AriaRole.Link, new() { Name = "Goals", Exact = true }).ClickAsync();
            await SelectTodoJourneyProfileAsync(page, finding.GoalOwner);
            await page.GetByLabel("Goal name", new() { Exact = true }).FillAsync("Renamed endurance");
            await page.GetByRole(AriaRole.Button, new() { Name = "Apply goal filters", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".goal-list article.panel")).ToHaveCountAsync(1);
            await page.GetByRole(AriaRole.Link, new() { Name = "Renamed endurance", Exact = true }).ClickAsync();
            await page.GetByLabel("Edition date", new() { Exact = true }).FillAsync("2022-02-03");
            await page.GetByRole(AriaRole.Button, new() { Name = "Find edition", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]edition=2022-01-31(?:&|$)"));
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Historic endurance", Exact = true })).ToBeVisibleAsync();

            await page.GetByRole(AriaRole.Link, new() { Name = "Records", Exact = true }).ClickAsync();
            await SelectTodoJourneyProfileAsync(page, records.Owner);
            await page.Locator(".sport-options").GetByRole(AriaRole.Link, new() { Name = "Cycling", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]sport=cycling(?:&|$)"));
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Record scope" }).SelectOptionAsync("outdoor");
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]scope=outdoor(?:&|$)"));
            await Assertions.Expect(page.Locator(".records-panel .panel-heading > span")).ToHaveTextAsync("Outdoor only");
            await AssertPeriodRecordActivityAsync(page, RecordKind.Distance, "Longest distance", records.LifetimeWinner);
            await SetRecordBrowserPeriodAsync(page, "2026-01-02", "2026-01-05");
            await AssertPeriodRecordActivityAsync(page, RecordKind.DistanceEffort, "5 km", records.PeriodWinner);
            var recordUrl = page.Url;
            await PeriodBrowserRecordRow(page, RecordKind.DistanceEffort, "5 km").Locator(".benchmark-link").ClickAsync();
            await Assertions.Expect(page.Locator(".attempt-table tbody tr")).ToHaveCountAsync(3);
            await Assertions.Expect(page.Locator(".attempt-table tbody tr").First).ToContainTextAsync("Period boundary winner");
            await page.GetByRole(AriaRole.Link, new() { Name = "Back to Records", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(recordUrl);
            await AssertPeriodRecordActivityAsync(page, RecordKind.DistanceEffort, "5 km", records.PeriodWinner);
            var captures = Path.Combine(FindRepositoryRoot(), "artifacts", "todo-journey");
            Directory.CreateDirectory(captures);
            await page.ScreenshotAsync(new() { Path = Path.Combine(captures, "complete.png"), FullPage = true });
            Assert.Empty(errors);
            Assert.DoesNotContain("Unhandled exception", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        catch { TestContext.Current.TestOutputHelper!.WriteLine(output.ToString()); throw; }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    private static async Task SelectTodoJourneyProfileAsync(IPage page, Guid owner)
    {
        var selector = page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" });
        await selector.SelectOptionAsync(owner.ToString());
        await Assertions.Expect(selector).ToHaveValueAsync(owner.ToString());
    }
}
