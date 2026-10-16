using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    [Fact]
    public async Task Cross_view_preview_fixture_is_created_only_when_explicitly_requested()
    {
        var requestedPath = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_CROSS_VIEW_PREVIEW_DATA");
        if (string.IsNullOrWhiteSpace(requestedPath)) return;
        var path = Path.GetFullPath(requestedPath);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException("The cross-view preview fixture requires an empty directory.");
        Directory.CreateDirectory(path);
        var seed = await SeedCrossViewBrowserDataAsync(path);
        await File.WriteAllTextAsync(Path.Combine(path, "synthetic-fixture.json"), JsonSerializer.Serialize(seed));
        TestContext.Current.TestOutputHelper!.WriteLine($"Synthetic cross-view preview data: {path}");
    }

    [Fact]
    public async Task Cross_view_searches_reconcile_all_pages_map_reporting_calendar_and_exact_drilldowns()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedCrossViewBrowserDataAsync(dataRoot);
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
            var mapRequests = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            page.Request += (_, request) => { if (request.Url.Contains("/internal/map/activities?", StringComparison.Ordinal)) mapRequests.Add(request.Url); };
            await OpenCombinedSearchAsync(page, origin, seed.Search);
            await AssertReportingTotalsAsync(page, "30", $"{765d:N1} km", "22:45:00", $"{3465d:N0} m");
            await page.GetByRole(AriaRole.Link, new() { Name = "Show matching activities on map", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "World Map", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".world-map .maplibregl-canvas")).ToHaveAttributeAsync("data-activities-features", "29");
            Assert.NotEmpty(mapRequests);
            using (var response = JsonDocument.Parse(await (await page.APIRequest.GetAsync(mapRequests.Last())).TextAsync()))
            {
                var ids = response.RootElement.GetProperty("features").EnumerateArray()
                    .Select(feature => feature.GetProperty("properties").GetProperty("id").GetGuid()).Order().ToArray();
                Assert.Equal(seed.GpsMatchIds.Order(), ids);
                Assert.Equal(29, response.RootElement.GetProperty("coverage").GetProperty("matchingCount").GetInt32());
            }
            var mapBookmark = page.Url;
            var frequencyHref = await page.GetByRole(AriaRole.Link, new() { Name = "Frequency", Exact = true }).GetAttributeAsync("href");
            Assert.NotNull(frequencyHref);
            Assert.DoesNotContain("tags=", frequencyHref, StringComparison.Ordinal);
            Assert.DoesNotContain("q=", frequencyHref, StringComparison.Ordinal);
            Assert.DoesNotContain("minDistance=", frequencyHref, StringComparison.Ordinal);
            await page.GoBackAsync();
            await AssertReportingTotalsAsync(page, "30", $"{765d:N1} km", "22:45:00", $"{3465d:N0} m");
            var calendarAction = await page.GetByRole(AriaRole.Link, new() { Name = "View in calendar", Exact = true }).GetAttributeAsync("href");
            Assert.Contains("month=2026-01", calendarAction, StringComparison.Ordinal);
            Assert.Contains("excludeTags=", calendarAction, StringComparison.Ordinal);
            await page.GetByRole(AriaRole.Link, new() { Name = "View progress", Exact = true }).ClickAsync();
            await AssertProgressTotalsAsync(page, "30", $"{765d:N1} km", "22:45:00", $"{3465d:N0} m", "1");
            await Assertions.Expect(page.Locator(".spark-chart .chart-sample")).ToHaveAttributeAsync("data-value", "765");
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Compare with", Exact = true }).SelectOptionAsync("previous-period");
            await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".progress-totals .metric-card small").First).ToContainTextAsync("Compared with 4");
            await Assertions.Expect(page.Locator(".progress-totals .metric-card small").First).ToContainTextAsync("+26");
            var dashboardBookmark = page.Url;
            await AssertProgressResponsiveAsync(page, "cross-view-dashboard");
            await AssertProgressEnlargedTextAsync(page, "Cross-view dashboard");
            var opening = page.Locator(".saved-searches");
            await opening.Locator("summary").ClickAsync();
            await Assertions.Expect(opening.GetByLabel("Search name", new() { Exact = true })).ToHaveCountAsync(0);
            await Assertions.Expect(opening.GetByRole(AriaRole.Button, new() { Name = "Save new search", Exact = true })).ToHaveCountAsync(0);

            foreach (var name in new[] { "^Browse Cycling activities$", "^Browse activities recorded with ", "^Browse activities using " })
            {
                var link = page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex(name) });
                await Assertions.Expect(link).ToContainTextAsync("30");
                await link.ClickAsync();
                await AssertReportingTotalsAsync(page, "30", $"{765d:N1} km", "22:45:00", $"{3465d:N0} m");
                Assert.Contains("from=2026-01-02", page.Url, StringComparison.Ordinal);
                Assert.Contains("excludeTags=", page.Url, StringComparison.Ordinal);
                Assert.Contains("minDistance=10000", page.Url, StringComparison.Ordinal);
                if (name.Contains("recorded", StringComparison.Ordinal)) Assert.Contains("deviceExact=yes", page.Url, StringComparison.Ordinal);
                await page.GoBackAsync();
                await AssertProgressTotalsAsync(page, "30", $"{765d:N1} km", "22:45:00", $"{3465d:N0} m", "1");
            }
            await page.GetByRole(AriaRole.Link, new() { Name = "Training calendar", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Training calendar", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByLabel("Month", new() { Exact = true })).ToHaveValueAsync("2026-01");
            await Assertions.Expect(page.Locator(".calendar-totals dd")).ToHaveTextAsync(["34", $"{813d:N1} km", "24:45:00", $"{3865d:N0} m", "2"]);
            await AssertProgressResponsiveAsync(page, "cross-view-calendar");
            var januarySecond = page.Locator(".calendar-grid .calendar-day[data-date='2026-01-02']");
            await Assertions.Expect(januarySecond).ToContainTextAsync("30");
            await januarySecond.ClickAsync();
            await AssertReportingTotalsAsync(page, "30", $"{765d:N1} km", "22:45:00", $"{3465d:N0} m");
            Assert.Contains("from=2026-01-02", page.Url, StringComparison.Ordinal);
            Assert.Contains("to=2026-01-02", page.Url, StringComparison.Ordinal);
            await page.GoBackAsync();
            var monthProgress = page.GetByRole(AriaRole.Link, new() { Name = "Progress dashboard", Exact = true });
            var monthHref = await monthProgress.GetAttributeAsync("href");
            Assert.Contains("from=2026-01-01", monthHref, StringComparison.Ordinal);
            Assert.Contains("to=2026-01-31", monthHref, StringComparison.Ordinal);
            await monthProgress.ClickAsync();
            await AssertProgressTotalsAsync(page, "34", $"{813d:N1} km", "24:45:00", $"{3865d:N0} m", "2");

            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                var saved = await db.SavedSearches.SingleAsync(search => search.Id == seed.CombinedSavedSearch);
                var criteria = JsonSerializer.Deserialize<SavedSearchCriteria>(saved.CriteriaJson)!;
                saved.CriteriaJson = JsonSerializer.Serialize(criteria with { Search = "No matching fictional activity" });
                await db.SaveChangesAsync();
            }
            await page.GotoAsync(dashboardBookmark, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Alert).First).ToContainTextAsync("another profile");
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.Search.EastOwner.ToString());
            await AssertProgressTotalsAsync(page, "30", $"{765d:N1} km", "22:45:00", $"{3465d:N0} m", "1");
            await page.GotoAsync(mapBookmark, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.Search.EastOwner.ToString());
            await Assertions.Expect(page.Locator(".world-map .maplibregl-canvas")).ToHaveAttributeAsync("data-activities-features", "29");
            Assert.DoesNotContain(seed.CombinedSavedSearch.ToString(), dashboardBookmark, StringComparison.Ordinal);
            Assert.DoesNotContain(seed.CombinedSavedSearch.ToString(), mapBookmark, StringComparison.Ordinal);
            Assert.Empty(errors);
        }
        catch { TestContext.Current.TestOutputHelper!.WriteLine(output.ToString()); throw; }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    [Fact]
    public async Task Cross_view_searches_keep_profile_repair_relative_periods_and_unassigned_gear_explicit()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedCrossViewBrowserDataAsync(dataRoot);
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
            var noGearUrl = seed.Search.CombinedUrl.Replace("/activities?", "/?", StringComparison.Ordinal).Replace("&gear=Road%20bike", "", StringComparison.Ordinal);
            await page.GotoAsync(origin + noGearUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.Search.EastOwner.ToString());
            await Assertions.Expect(page.Locator(".progress-totals .metric-card > strong").First).ToHaveTextAsync("32");
            await page.GetByRole(AriaRole.Link, new() { Name = "Browse activities with unassigned gear", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".activity-totals dd").First).ToHaveTextAsync("1");
            await Assertions.Expect(page.Locator(".activity-row")).ToContainTextAsync("Grøn unassigned drill");
            Assert.Contains("gearUnassigned=yes", page.Url, StringComparison.Ordinal);
            Assert.Contains("excludeTags=", page.Url, StringComparison.Ordinal);
            await page.GoBackAsync();
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.Search.EmptyOwner.ToString());
            await Assertions.Expect(page.GetByRole(AriaRole.Alert).First).ToContainTextAsync("another profile");
            await Assertions.Expect(page.Locator(".progress-totals")).ToHaveCountAsync(0);
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.Search.EastOwner.ToString());
            await Assertions.Expect(page.Locator(".progress-totals .metric-card > strong").First).ToHaveTextAsync("32");
            var saved = page.Locator(".saved-searches");
            await saved.Locator("summary").ClickAsync();
            await saved.GetByRole(AriaRole.Combobox, new() { Name = "Saved search", Exact = true }).SelectOptionAsync(seed.RelativeSavedSearch.ToString());
            await saved.GetByRole(AriaRole.Button, new() { Name = "Open search", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]period=this-month(?:&|$)"));
            await Assertions.Expect(page.Locator(".progress-totals .metric-card > strong").First).ToHaveTextAsync("1");
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh dashboard", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".progress-totals .metric-card > strong").First).ToHaveTextAsync("1");
            Assert.DoesNotContain("from=", page.Url, StringComparison.Ordinal);
            Assert.DoesNotContain("to=", page.Url, StringComparison.Ordinal);
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.Search.EastOwner.ToString());
            await Assertions.Expect(page.Locator(".progress-totals .metric-card > strong").First).ToHaveTextAsync("1");
            await page.GetByRole(AriaRole.Link, new() { Name = "Training calendar", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".calendar-totals dd").First).ToHaveTextAsync("1");
            var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen"));
            await Assertions.Expect(page.GetByLabel("Month", new() { Exact = true })).ToHaveValueAsync(localNow.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            saved = page.Locator(".saved-searches");
            await saved.Locator("summary").ClickAsync();
            await saved.GetByRole(AriaRole.Combobox, new() { Name = "Saved search", Exact = true }).SelectOptionAsync(seed.Search.RepairSavedSearch.ToString());
            var beforeRepair = page.Url;
            await saved.GetByRole(AriaRole.Button, new() { Name = "Open search", Exact = true }).ClickAsync();
            await Assertions.Expect(saved.GetByRole(AriaRole.Region, new() { Name = "Repair saved search", Exact = true })).ToContainTextAsync("Retired exclusion");
            Assert.Equal(beforeRepair, page.Url);
            await saved.GetByRole(AriaRole.Button, new() { Name = "Save repaired search and open", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("excludeTags="));
            await Assertions.Expect(saved.GetByRole(AriaRole.Region, new() { Name = "Repair saved search", Exact = true })).ToHaveCountAsync(0);
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                var repaired = JsonSerializer.Deserialize<SavedSearchCriteria>((await db.SavedSearches.SingleAsync(search => search.Id == seed.Search.RepairSavedSearch)).CriteriaJson)!;
                Assert.Equal(seed.Search.CommuteTag, Assert.Single(repaired.ExcludedTags!).Id);
            }
            await page.GotoAsync(origin + $"/activities/{seed.Search.MatchIds[0]}", new() { WaitUntil = WaitUntilState.NetworkIdle });
            var assigned = page.GetByRole(AriaRole.List, new() { Name = "Assigned tags", Exact = true });
            await assigned.GetByRole(AriaRole.Link, new() { Name = "Training", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Switch to Search East", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Group, new() { Name = "Tags — match any", Exact = true })
                .GetByRole(AriaRole.Checkbox, new() { Name = "Training", Exact = true })).ToBeCheckedAsync();
            Assert.Contains($"tagOwner={seed.Search.EastOwner}", page.Url, StringComparison.Ordinal);
            Assert.Empty(errors);
        }
        catch { TestContext.Current.TestOutputHelper!.WriteLine(output.ToString()); throw; }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    private static async Task<CrossViewBrowserSeed> SeedCrossViewBrowserDataAsync(string dataRoot)
    {
        var search = await SeedSearchBrowserDataAsync(dataRoot);
        var combinedUrl = search.CombinedUrl.Replace("&gps=yes", "", StringComparison.Ordinal);
        search = search with { CombinedUrl = combinedUrl };
        await using var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot));
        foreach (var activity in await db.Activities.Include(activity => activity.Stream).ToArrayAsync()) MoveToPreviewArea(activity);
        var noGps = await db.Activities.SingleAsync(activity => activity.Id == search.MatchIds[0]);
        noGps.HasGps = false;
        var caseVariant = await db.Activities.SingleAsync(activity => activity.Id == search.MatchIds[1]);
        caseVariant.DeviceName = " fictional EDGE ";
        caseVariant.GearName = " ROAD BIKE ";
        var gpsDecoy = await db.Activities.SingleAsync(activity => activity.Title == "Grøn decoy: No GPS activity");
        db.ActivityTags.Add(new ActivityTag { ActivityId = gpsDecoy.Id, TagId = search.CommuteTag });
        (await db.Activities.SingleAsync(activity => activity.Title == "Grøn decoy: Different date")).StartTimeUtc = new(2026, 2, 3, 12, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 4; index++) db.Activities.Add(Candidate($"Grøn previous period {index + 1}", new(2026, 1, 1, 12, index, 0, TimeSpan.Zero)));
        var wrongComparison = Candidate("Grøn previous period wrong gear", new(2026, 1, 1, 12, 30, 0, TimeSpan.Zero));
        wrongComparison.GearName = "Road bike spare";
        db.Activities.Add(wrongComparison);
        var unassigned = Candidate("Grøn unassigned drill", new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero));
        unassigned.GearName = null;
        db.Activities.Add(unassigned);
        var relativeActivity = Candidate("Relative rolling activity", DateTimeOffset.UtcNow);
        relativeActivity.Description = null;
        db.Activities.Add(relativeActivity);
        var criteria = new SavedSearchCriteria(2, SportKind.Cycling, "æble grøn", true, "Fictional Edge",
            [new(search.TrainingTag, "Training"), new(search.IntervalTag, "Intervals")], "start-desc",
            new(ReportingPreset.Custom, new(2026, 1, 2), new(2026, 1, 2)), TextMode: ActivityTextMode.Words,
            DeviceExact: true, Gear: "Road bike", IsIndoor: false,
            MinDistanceMeters: 10000, MaxDistanceMeters: 40000, MinMovingSeconds: 1800, MaxMovingSeconds: 3600,
            MinAscentMeters: 100, MaxAscentMeters: 200, TagMode: ActivityTagMode.All,
            ExcludedTags: [new(search.CommuteTag, "Commute")], TagPresence: ActivityTagPresence.Tagged);
        var combined = new SavedSearch
        {
            OwnerId = search.EastOwner,
            Name = "Combined cross-view search",
            NormalizedName = "COMBINED CROSS-VIEW SEARCH",
            CriteriaJson = JsonSerializer.Serialize(criteria)
        };
        var relative = new SavedSearch
        {
            OwnerId = search.EastOwner,
            Name = "Relative month search",
            NormalizedName = "RELATIVE MONTH SEARCH",
            CriteriaJson = JsonSerializer.Serialize(new SavedSearchCriteria(2, null, "Relative", null, null,
                [new(search.TrainingTag, "Training")], "start-desc", new(ReportingPreset.ThisMonth), TextMode: ActivityTextMode.Words))
        };
        db.AddRange(combined, relative);
        await db.SaveChangesAsync();
        return new(search, combined.Id, relative.Id, search.MatchIds.Skip(1).ToArray(), criteria);

        Activity Candidate(string title, DateTimeOffset start)
        {
            var activity = ReportingBrowserActivity(search.EastOwner, title, start, 12000, 1800, 100);
            activity.Description = "Fictional ÆBLE training.";
            activity.DeviceName = "Fictional Edge";
            activity.GearName = "Road bike";
            activity.HasPower = true;
            activity.IsIndoor = false;
            activity.Tags = [new ActivityTag { ActivityId = activity.Id, TagId = search.TrainingTag },
                new ActivityTag { ActivityId = activity.Id, TagId = search.IntervalTag }];
            MoveToPreviewArea(activity);
            return activity;
        }

        static void MoveToPreviewArea(Activity activity)
        {
            var points = TrackCodec.Decode(activity.Stream!.CompressedPayload)
                .Select(point => point with { Latitude = point.Latitude + 54.65, Longitude = point.Longitude + 42.30 }).ToArray();
            activity.Stream.CompressedPayload = TrackCodec.Encode(points);
            activity.GeometryWkb = GeometryCodec.ToWkb(points);
            activity.SimplifiedGeometryWkb = GeometryCodec.ToWkb(points, 0.00001);
            var bounds = GeometryCodec.Bounds(points);
            activity.MinLatitude = bounds.MinLat;
            activity.MinLongitude = bounds.MinLon;
            activity.MaxLatitude = bounds.MaxLat;
            activity.MaxLongitude = bounds.MaxLon;
        }
    }

    private sealed record CrossViewBrowserSeed(SearchBrowserSeed Search, Guid CombinedSavedSearch, Guid RelativeSavedSearch,
        IReadOnlyList<Guid> GpsMatchIds, SavedSearchCriteria Criteria);
}
