using System.Globalization;
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
    public async Task Reporting_preview_fixture_is_created_only_when_explicitly_requested()
    {
        var requestedPath = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_REPORTING_PREVIEW_DATA");
        if (string.IsNullOrWhiteSpace(requestedPath)) return;
        var path = Path.GetFullPath(requestedPath);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException("The reporting preview fixture requires an empty directory.");
        Directory.CreateDirectory(path);
        var seed = await SeedReportingBrowserDataAsync(path);
        await File.WriteAllTextAsync(Path.Combine(path, "synthetic-fixture.json"), JsonSerializer.Serialize(seed));
        TestContext.Current.TestOutputHelper!.WriteLine($"Synthetic reporting preview data: {path}");
    }

    [Fact]
    public async Task Reporting_totals_dates_and_deletion_follow_the_applied_filter()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedReportingBrowserDataAsync(dataRoot);
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
            const string customQuery = "/activities?from=2026-01-02&to=2026-01-02";
            await page.GotoAsync(origin + customQuery, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true })).ToHaveValueAsync("custom");
            await AssertReportingTotalsAsync(page, "31", $"{473d:N1} km", "33:00:00", "380 m");
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".activity-row")).ToHaveCountAsync(25);
            await Assertions.Expect(page.GetByText("Western previous day", new() { Exact = true })).ToHaveCountAsync(0);
            await page.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".pager")).ToContainTextAsync("Page 2 of 2");
            await Assertions.Expect(page.Locator(".activity-row")).ToHaveCountAsync(6);
            await AssertReportingTotalsAsync(page, "31", $"{473d:N1} km", "33:00:00", "380 m");
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.EastOwner.ToString());
            await Assertions.Expect(page.Locator(".pager")).ToContainTextAsync("Page 1 of 2");
            Assert.DoesNotContain("page=", page.Url, StringComparison.Ordinal);
            await AssertReportingTotalsAsync(page, "30", $"{465d:N1} km", "30:00:00", "300 m");
            var expectedLocalDate = new DateTime(2026, 1, 2, 0, 59, 0).ToString("g", CultureInfo.CurrentCulture);
            await Assertions.Expect(page.Locator(".activity-row").First).ToContainTextAsync(expectedLocalDate);
            await Assertions.Expect(page.Locator(".activity-selection-row").First.GetByRole(AriaRole.Checkbox))
                .ToHaveAccessibleNameAsync(new Regex(Regex.Escape(expectedLocalDate)));

            foreach (var sort in new[] { "start-asc", "distance-desc", "duration-desc", "elevation-desc", "start-desc" })
            {
                await page.GetByRole(AriaRole.Combobox, new() { Name = "Sort", Exact = true }).SelectOptionAsync(sort);
                await ApplyReportingFiltersAsync(page);
                await AssertReportingTotalsAsync(page, "30", $"{465d:N1} km", "30:00:00", "300 m");
            }
            await page.GetByRole(AriaRole.Button, new() { Name = "Select this page", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Delete selected", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".delete-confirmation")).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Last year" });
            await Assertions.Expect(page.Locator(".delete-confirmation")).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByText("Apply filters to update these results.", new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Delete selected", Exact = true })).ToBeDisabledAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Delete all 30 matching", Exact = true })).ToBeDisabledAsync();
            await Assertions.Expect(page.Locator(".activity-selection-row input").First).ToBeDisabledAsync();
            await Assertions.Expect(page.Locator(".activity-selection-row input:checked")).ToHaveCountAsync(0);
            await AssertReportingTotalsAsync(page, "30", $"{465d:N1} km", "30:00:00", "300 m");

            await page.GotoAsync(origin + customQuery, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.EmptyOwner.ToString());
            await AssertReportingTotalsAsync(page, "0", "0 m", "0:00", "0 m");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "No matching activities", Exact = true })).ToBeVisibleAsync();
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.EastOwner.ToString());
            await AssertReportingTotalsAsync(page, "30", $"{465d:N1} km", "30:00:00", "300 m");
            await page.GetByLabel("Profile selector").EvaluateAsync("""
                (select, owners) => {
                    for (const owner of owners) {
                        select.value = owner;
                        select.dispatchEvent(new Event('change', { bubbles: true }));
                    }
                }
                """, new[] { seed.EmptyOwner.ToString(), seed.EastOwner.ToString(), seed.WestOwner.ToString() });
            await AssertReportingTotalsAsync(page, "1", $"{8d:N1} km", "3:00:00", "80 m");
            await Assertions.Expect(page.Locator(".activity-row")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".activity-row")).ToContainTextAsync("Western same day");

            await AssertReportingRelativeNavigationAsync(page, origin, "/activities");
            await AssertReportingInvalidQueriesAsync(page, origin, "/activities");
            await page.GotoAsync(origin + customQuery, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByLabel("From", new() { Exact = true }).FillAsync("2026-01-03");
            await page.GetByLabel("From", new() { Exact = true }).PressAsync("Tab");
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Apply filters", Exact = true })).ToBeDisabledAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Delete all 31 matching", Exact = true })).ToBeDisabledAsync();
            await Assertions.Expect(page.GetByLabel("From", new() { Exact = true })).ToHaveValueAsync("2026-01-03");

            await page.GotoAsync(origin + customQuery, new() { WaitUntil = WaitUntilState.NetworkIdle });
            var captures = Path.Combine(FindRepositoryRoot(), "artifacts", "reporting-ui");
            Directory.CreateDirectory(captures);
            foreach (var width in new[] { 320, 360, 375, 768, 1121, 1280, 1920 })
            {
                await page.SetViewportSizeAsync(width, 1000);
                await page.Locator(".sidebar").EvaluateAsync("element => Promise.all(element.getAnimations().map(animation => animation.finished))");
                await AssertNoDocumentOverflowAsync(page, $"Reporting filters and totals at {width}px");
                await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true })).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator(".activity-totals")).ToBeVisibleAsync();
                var firstTitle = page.Locator(".activity-row .activity-title").First;
                await Assertions.Expect(firstTitle.Locator("strong")).ToBeVisibleAsync();
                await Assertions.Expect(firstTitle.Locator("strong")).ToHaveTextAsync("Western same day");
                await Assertions.Expect(firstTitle.Locator("small")).ToBeVisibleAsync();
                await Assertions.Expect(firstTitle.Locator("small")).ToContainTextAsync("Reporting West");
                await Assertions.Expect(firstTitle.Locator("small")).ToContainTextAsync(new DateTime(2026, 1, 2, 12, 0, 0).ToString("g", CultureInfo.CurrentCulture));
                var clippedIdentity = await firstTitle.EvaluateAsync<bool>("""
                    title => [...title.querySelectorAll('strong, small')].some(element =>
                        element.scrollWidth > element.clientWidth + 1 || element.scrollHeight > element.clientHeight + 1)
                    """);
                Assert.False(clippedIdentity, $"Activity title, owner, and date must remain fully readable at {width}px.");
                if (width is 375 or 1280)
                    await page.ScreenshotAsync(new() { Path = Path.Combine(captures, $"activities-{width}.png"), FullPage = true });
            }
            await page.SetViewportSizeAsync(1280, 1000);
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true }).FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            await page.Keyboard.PressAsync("Shift+Tab");
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true })).ToBeFocusedAsync();
            Assert.NotEqual("none", await page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true })
                .EvaluateAsync<string>("element => getComputedStyle(element).outlineStyle"));
            await page.EvaluateAsync("document.documentElement.style.fontSize='200%'; document.body.style.fontSize='30px'");
            await AssertNoDocumentOverflowAsync(page, "Reporting filters and totals with enlarged text");
            await page.EvaluateAsync("document.documentElement.style.removeProperty('font-size'); document.body.style.removeProperty('font-size')");

            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.EastOwner.ToString());
            await page.GetByRole(AriaRole.Button, new() { Name = "Delete all 30 matching", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".delete-confirmation")).ToContainTextAsync("exact snapshot contains 30 activities");
            await AddReportingLateImportAsync(dataRoot, seed.EastOwner);
            await page.GetByRole(AriaRole.Button, new() { Name = "Permanently delete 30 activities", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByText("Reporting imported after confirmation", new() { Exact = true })).ToBeVisibleAsync();
            await AssertReportingTotalsAsync(page, "1", $"{5d:N1} km", "1:00:00", "10 m");
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
                await db.Owners.Where(owner => owner.Id == seed.EastOwner)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(owner => owner.TimeZoneId, "Unavailable/Example"));
            await page.GotoAsync(origin + customQuery, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("timezone");
            await page.GetByRole(AriaRole.Link, new() { Name = "Choose a timezone in Profiles", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByLabel("Reporting timezone", new() { Exact = true })).ToHaveCountAsync(3);
            await Assertions.Expect(page.GetByLabel("Badge timezone", new() { Exact = true })).ToHaveCountAsync(0);
            var eastProfile = page.Locator(".entity-card").Filter(new() { HasText = "Reporting East" });
            await eastProfile.GetByLabel("Reporting timezone", new() { Exact = true }).SelectOptionAsync("UTC");
            await eastProfile.GetByRole(AriaRole.Button, new() { Name = "Save timezone", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("saved");
            await page.GotoAsync(origin + customQuery, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await AssertReportingTotalsAsync(page, "2", $"{13d:N1} km", "4:00:00", "90 m");
            Assert.Empty(errors);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    [Fact]
    public async Task Reporting_map_preserves_relative_dates_owner_membership_and_blank_privacy()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedReportingBrowserDataAsync(dataRoot);
        var origin = $"http://127.0.0.1:{ReservePort()}";
        var output = new StringBuilder();
        using var process = StartApplication(FindWebAssembly(FindRepositoryRoot()), dataRoot, origin, output);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 1000 } });
            var externalRequests = new List<string>();
            var mapRequests = new List<string>();
            page.Request += (_, request) =>
            {
                if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                    && uri.GetLeftPart(UriPartial.Authority) != origin) externalRequests.Add(request.Url);
                if (request.Url.Contains("/internal/map/activities", StringComparison.Ordinal)) mapRequests.Add(request.Url);
            };
            await page.GotoAsync(origin + "/map?from=2026-01-02&to=2026-01-02", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true })).ToHaveValueAsync("custom");
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
            using (var features = JsonDocument.Parse(await (await page.APIRequest.GetAsync(origin + "/internal/map/activities?from=2026-01-02&to=2026-01-02")).TextAsync()))
            {
                var titles = features.RootElement.GetProperty("features").EnumerateArray()
                    .Select(feature => feature.GetProperty("properties").GetProperty("title").GetString()).ToArray();
                Assert.Equal(31, titles.Length);
                Assert.Contains("Western same day", titles);
                Assert.DoesNotContain("Western previous day", titles);
            }
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.EastOwner.ToString());
            await Assertions.Expect(page.Locator(".map-panel")).ToBeVisibleAsync();
            await AssertReportingRelativeNavigationAsync(page, origin, "/map");
            await AssertReportingInvalidQueriesAsync(page, origin, "/map");
            var finalMapRequestStart = mapRequests.Count;
            await page.RouteAsync("**/internal/map/activities?**", route => route.FulfillAsync(new()
            {
                Status = 503,
                ContentType = "application/json",
                Body = "{}"
            }), new() { Times = 1 });
            await page.GotoAsync(origin + "/map?period=today", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Some map lines could not be loaded");
            await page.GetByRole(AriaRole.Button, new() { Name = "Retry map lines", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".map-load-status")).ToContainTextAsync("No lines in this view");
            foreach (var width in new[] { 320, 360, 375, 768, 1121, 1280, 1920 })
            {
                await page.SetViewportSizeAsync(width, 1000);
                await page.Locator(".sidebar").EvaluateAsync("element => Promise.all(element.getAnimations().map(animation => animation.finished))");
                await AssertNoDocumentOverflowAsync(page, $"World Map reporting filters at {width}px");
                await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true })).ToBeVisibleAsync();
            }
            await page.SetViewportSizeAsync(1280, 1000);
            await page.EvaluateAsync("document.documentElement.style.fontSize='200%'; document.body.style.fontSize='30px'");
            await AssertNoDocumentOverflowAsync(page, "World Map reporting filters with enlarged text");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            Assert.NotEmpty(mapRequests);
            Assert.Contains(mapRequests, url => url.Contains("period=today", StringComparison.Ordinal));
            var capturedReferences = mapRequests.Skip(finalMapRequestStart)
                .Select(url => new Uri(url).Query.TrimStart('?').Split('&').SingleOrDefault(part => part.StartsWith("asOf=", StringComparison.Ordinal)))
                .ToArray();
            Assert.NotEmpty(capturedReferences);
            Assert.All(capturedReferences, value => Assert.False(string.IsNullOrEmpty(value), "Map activity requests must carry the displayed period's captured reference instant."));
            Assert.Single(capturedReferences.Distinct());
            Assert.DoesNotContain("asOf=", page.Url, StringComparison.Ordinal);
            Assert.Empty(externalRequests);
            Assert.DoesNotContain("Unhandled exception", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    private static async Task AssertReportingRelativeNavigationAsync(IPage page, string origin, string route)
    {
        await page.GotoAsync(origin + route + "?period=today&from=bad&to=2026-01-01", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page).ToHaveURLAsync(origin + route + "?period=today");
        var period = page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true });
        await Assertions.Expect(period.Locator("option")).ToHaveTextAsync([
            "All time", "Today", "This week", "Last week", "This month", "Last month", "Year to date", "Last year", "Custom range"]);
        await Assertions.Expect(period).ToHaveValueAsync("today");
        await Assertions.Expect(page.GetByLabel("From", new() { Exact = true })).ToHaveCountAsync(0);
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(period).ToHaveValueAsync("today");
        await period.SelectOptionAsync(new SelectOptionValue { Label = "Last week" });
        await ApplyReportingFiltersAsync(page);
        await Assertions.Expect(page).ToHaveURLAsync(origin + route + "?period=last-week");
        await page.GoBackAsync();
        await Assertions.Expect(period).ToHaveValueAsync("today");
        await page.GoForwardAsync();
        await Assertions.Expect(period).ToHaveValueAsync("last-week");
        await period.SelectOptionAsync(new SelectOptionValue { Label = "Custom range" });
        await page.GetByLabel("From", new() { Exact = true }).FillAsync("2026-01-02");
        await page.GetByLabel("To", new() { Exact = true }).FillAsync("2026-01-02");
        await ApplyReportingFiltersAsync(page);
        await Assertions.Expect(page).ToHaveURLAsync(origin + route + "?from=2026-01-02&to=2026-01-02");
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(period).ToHaveValueAsync("custom");
        await Assertions.Expect(page.GetByLabel("From", new() { Exact = true })).ToHaveValueAsync("2026-01-02");
        await period.SelectOptionAsync(new SelectOptionValue { Label = "All time" });
        await ApplyReportingFiltersAsync(page);
        await Assertions.Expect(page).ToHaveURLAsync(origin + route);
    }

    private static async Task AssertReportingInvalidQueriesAsync(IPage page, string origin, string route)
    {
        foreach (var query in new[] { "from=bad", "from=2026-01-03&to=2026-01-02", "period=unknown", "to=9999-12-31" })
        {
            await page.GotoAsync(origin + route + "?" + query, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToBeVisibleAsync();
            await Assertions.Expect(page).ToHaveURLAsync(origin + route + "?" + query);
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Apply filters", Exact = true })).ToBeDisabledAsync();
            await Assertions.Expect(page.Locator(".activity-row")).ToHaveCountAsync(0);
            if (route == "/map") await Assertions.Expect(page.Locator(".map-panel")).ToHaveCountAsync(0);
        }
    }

    private static async Task ApplyReportingFiltersAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Apply filters to update these results.", new() { Exact = true })).ToHaveCountAsync(0);
    }

    private static async Task AssertReportingTotalsAsync(IPage page, string count, string distance, string moving, string ascent)
    {
        await Assertions.Expect(page.Locator(".activity-totals dt")).ToHaveTextAsync(["Activities", "Distance", "Moving time", "Ascent"]);
        await Assertions.Expect(page.Locator(".activity-totals dd")).ToHaveTextAsync([count, distance, moving, ascent]);
    }

    private static async Task<ReportingBrowserSeed> SeedReportingBrowserDataAsync(string dataRoot)
    {
        var options = ReportingBrowserOptions(dataRoot);
        await using var db = new ExplorerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var east = new OwnerProfile { DisplayName = "Reporting East", TimeZoneId = "Europe/Copenhagen" };
        var west = new OwnerProfile { DisplayName = "Reporting West", TimeZoneId = "America/Los_Angeles" };
        var empty = new OwnerProfile { DisplayName = "Reporting Empty", TimeZoneId = "UTC" };
        db.AddRange(east, west, empty);
        for (var index = 0; index < 30; index++)
            db.Activities.Add(ReportingBrowserActivity(east.Id, $"Reporting ride {index + 1:00}",
                new DateTimeOffset(2026, 1, 1, 23, 30, 0, TimeSpan.Zero).AddMinutes(index), (index + 1) * 1000, 3600, 10));
        db.Activities.Add(ReportingBrowserActivity(west.Id, "Western previous day", new(2026, 1, 1, 23, 30, 0, TimeSpan.Zero), 7000, 7200, 70));
        db.Activities.Add(ReportingBrowserActivity(west.Id, "Western same day", new(2026, 1, 2, 20, 0, 0, TimeSpan.Zero), 8000, 10800, 80));
        await db.SaveChangesAsync();
        return new ReportingBrowserSeed(east.Id, west.Id, empty.Id);
    }

    private static async Task AddReportingLateImportAsync(string dataRoot, Guid owner)
    {
        await using var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot));
        db.Activities.Add(ReportingBrowserActivity(owner, "Reporting imported after confirmation", new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero), 5000, 3600, 10));
        await db.SaveChangesAsync();
    }

    private static Activity ReportingBrowserActivity(Guid owner, string title, DateTimeOffset start, double distance, double moving, double ascent)
    {
        var points = TestSupport.Track(2).Select((point, index) => point with { Timestamp = start.AddSeconds(index) }).ToArray();
        var activity = BrowserActivity(owner, title, SportKind.Cycling, points);
        activity.DistanceMeters = distance;
        activity.MovingTimeSeconds = moving;
        activity.ElapsedTimeSeconds = moving;
        activity.ElevationGainMeters = ascent;
        return activity;
    }

    private static DbContextOptions<ExplorerDbContext> ReportingBrowserOptions(string dataRoot) =>
        new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite($"Data Source={Path.Combine(dataRoot, "activity-explorer.db")}").Options;

    private sealed record ReportingBrowserSeed(Guid EastOwner, Guid WestOwner, Guid EmptyOwner);
}
