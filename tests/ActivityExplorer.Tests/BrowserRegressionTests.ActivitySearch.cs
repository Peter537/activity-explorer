using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    [Fact]
    public async Task Search_preview_fixture_is_created_only_when_explicitly_requested()
    {
        var requestedPath = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_SEARCH_PREVIEW_DATA");
        if (string.IsNullOrWhiteSpace(requestedPath)) return;
        var path = Path.GetFullPath(requestedPath);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException("The search preview fixture requires an empty directory.");
        Directory.CreateDirectory(path);
        var seed = await SeedSearchBrowserDataAsync(path);
        await File.WriteAllTextAsync(Path.Combine(path, "synthetic-fixture.json"), JsonSerializer.Serialize(seed));
        TestContext.Current.TestOutputHelper!.WriteLine($"Synthetic search preview data: {path}");
    }

    [Fact]
    public async Task Activity_search_combines_criteria_before_paging_and_preserves_exact_bulk_membership()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedSearchBrowserDataAsync(dataRoot);
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
            await OpenSearchFixtureProfileAsync(page, origin, seed);
            await OpenCombinedSearchAsync(page, origin, seed);
            await AssertReportingTotalsAsync(page, "30", $"{765d:N1} km", "22:45:00", $"{3465d:N0} m");
            await Assertions.Expect(page.Locator(".activity-row")).ToHaveCountAsync(25);
            var actualIds = await OrganizationVisibleIdsAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".pager")).ToContainTextAsync("Page 2 of 2");
            actualIds.AddRange(await OrganizationVisibleIdsAsync(page));
            Assert.Equal(seed.MatchIds.Order(), actualIds.Order());
            await AssertReportingTotalsAsync(page, "30", $"{765d:N1} km", "22:45:00", $"{3465d:N0} m");

            await page.GetByRole(AriaRole.Button, new() { Name = "Edit all matching", Exact = true }).ClickAsync();
            var editor = page.GetByRole(AriaRole.Region, new() { Name = "Bulk activity editor", Exact = true });
            await editor.GetByLabel("Gear change", new() { Exact = true }).SelectOptionAsync("Set");
            await editor.GetByLabel("Gear name", new() { Exact = true }).FillAsync("Reviewed fixture bike");
            await editor.GetByRole(AriaRole.Button, new() { Name = "Review changes", Exact = true }).ClickAsync();
            await Assertions.Expect(editor).ToContainTextAsync("30 activities");
            await editor.GetByRole(AriaRole.Button, new() { Name = "Apply changes", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByText("Reviewed 30 activities; changed 30.", new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".activity-row")).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Gear", Exact = true })).ToHaveValueAsync("Road bike");
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                var changed = await db.Activities.Where(activity => activity.GearName == "Reviewed fixture bike")
                    .Select(activity => activity.Id).ToArrayAsync();
                Assert.Equal(seed.MatchIds.Order(), changed.Order());
                await db.Activities.Where(activity => seed.MatchIds.Contains(activity.Id))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(activity => activity.GearName, "Road bike"));
            }
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Button, new() { Name = "Switch to Search East", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Delete all 30 matching", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".delete-confirmation")).ToContainTextAsync("exact snapshot contains 30 activities");
            await page.GetByLabel("Min distance (km)", new() { Exact = true }).FillAsync("20");
            await page.GetByLabel("Min distance (km)", new() { Exact = true }).PressAsync("Tab");
            await Assertions.Expect(page.Locator(".delete-confirmation")).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Delete all 30 matching", Exact = true })).ToBeDisabledAsync();
            await Assertions.Expect(page.GetByText("Apply filters to update these results.", new() { Exact = true })).ToBeVisibleAsync();
            await ApplyReportingFiltersAsync(page);
            await AssertReportingTotalsAsync(page, "21", $"{630d:N1} km", "17:30:00", $"{2520d:N0} m");
            await page.GetByRole(AriaRole.Button, new() { Name = "Select this page", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".activity-selector input:checked")).ToHaveCountAsync(21);
            await page.GetByLabel("Max distance (km)", new() { Exact = true }).FillAsync("10");
            await page.GetByLabel("Max distance (km)", new() { Exact = true }).PressAsync("Tab");
            await Assertions.Expect(page.Locator(".activity-selector input:checked")).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Apply filters", Exact = true })).ToBeDisabledAsync();
            await page.GetByLabel("Max distance (km)", new() { Exact = true }).FillAsync("40");
            await page.GetByLabel("Max distance (km)", new() { Exact = true }).PressAsync("Tab");
            await ApplyReportingFiltersAsync(page);
            var bookmark = page.Url;
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Switch to Search East", Exact = true })).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Switch to Search East", Exact = true }).ClickAsync();
            Assert.Equal(bookmark, page.Url);
            await AssertReportingTotalsAsync(page, "21", $"{630d:N1} km", "17:30:00", $"{2520d:N0} m");
            await AssertSearchResponsiveAsync(page);
            Assert.Empty(errors);
            Assert.DoesNotContain("Unhandled exception", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            TestContext.Current.TestOutputHelper!.WriteLine(output.ToString());
            throw;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    [Fact]
    public async Task Activity_search_distinguishes_legacy_literal_and_word_saved_searches()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedSearchBrowserDataAsync(dataRoot);
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
            await OpenSearchFixtureProfileAsync(page, origin, seed);
            await page.GotoAsync(origin + "/activities?q=" + Uri.EscapeDataString("æble grøn"), new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator(".activity-row")).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "No matching activities", Exact = true })).ToBeVisibleAsync();
            await page.GotoAsync(origin + "/activities?q=" + Uri.EscapeDataString("ÆBLE GRØN"), new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator(".activity-row")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".activity-row")).ToContainTextAsync("ÆBLE GRØN literal");
            await page.GotoAsync(origin + "/activities?q=" + Uri.EscapeDataString("æble grøn") + "&textMode=words", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator(".activity-row")).ToHaveCountAsync(25);
            await Assertions.Expect(page.Locator(".activity-totals dd").First).ToHaveTextAsync("45");
            await Assertions.Expect(page.Locator(".pager")).ToContainTextAsync("Page 1 of 2");
            await page.GotoAsync(origin + "/activities?q=" + Uri.EscapeDataString("\"æble grøn\"") + "&textMode=words", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator(".activity-row")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".activity-row")).ToContainTextAsync("ÆBLE GRØN literal");

            await OpenCombinedSearchAsync(page, origin, seed);
            var saved = page.Locator(".saved-searches");
            await saved.Locator("summary").ClickAsync();
            await saved.GetByLabel("Search name", new() { Exact = true }).FillAsync("Combined word search");
            await saved.GetByRole(AriaRole.Button, new() { Name = "Save new search", Exact = true }).ClickAsync();
            await Assertions.Expect(saved.GetByRole(AriaRole.Status)).ToContainTextAsync("Saved search.");
            var savedId = await saved.GetByLabel("Saved search", new() { Exact = true }).InputValueAsync();
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                var stored = await db.SavedSearches.SingleAsync(search => search.Id == Guid.Parse(savedId));
                using var json = JsonDocument.Parse(stored.CriteriaJson);
                Assert.Equal(2, json.RootElement.GetProperty("Version").GetInt32());
            }
            await page.GetByRole(AriaRole.Button, new() { Name = "Reset", Exact = true }).ClickAsync();
            await saved.GetByRole(AriaRole.Button, new() { Name = "Open search", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("textMode=words"));
            await AssertReportingTotalsAsync(page, "30", $"{765d:N1} km", "22:45:00", $"{3465d:N0} m");
            await saved.GetByLabel("Saved search", new() { Exact = true }).SelectOptionAsync(seed.LegacySavedSearch.ToString());
            await saved.GetByRole(AriaRole.Button, new() { Name = "Open search", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".activity-row")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".activity-row")).ToContainTextAsync("ÆBLE GRØN literal");
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                var stored = await db.SavedSearches.SingleAsync(search => search.Id == seed.LegacySavedSearch);
                Assert.Equal(seed.LegacyCriteriaJson, stored.CriteriaJson);
            }
            await saved.GetByLabel("Saved search", new() { Exact = true }).SelectOptionAsync(seed.RepairSavedSearch.ToString());
            var beforeRepair = page.Url;
            await saved.GetByRole(AriaRole.Button, new() { Name = "Open search", Exact = true }).ClickAsync();
            var repair = saved.GetByRole(AriaRole.Region, new() { Name = "Repair saved search", Exact = true });
            await Assertions.Expect(repair).ToContainTextAsync("Retired exclusion");
            await Assertions.Expect(repair.GetByRole(AriaRole.Group, new() { Name = "Repaired excluded tags — exclude any", Exact = true })
                .GetByRole(AriaRole.Checkbox, new() { Name = "Commute", Exact = true })).ToBeCheckedAsync();
            Assert.Equal(beforeRepair, page.Url);
            await repair.GetByRole(AriaRole.Button, new() { Name = "Save repaired search and open", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("excludeTags="));
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                var stored = await db.SavedSearches.SingleAsync(search => search.Id == seed.RepairSavedSearch);
                var repaired = JsonSerializer.Deserialize<SavedSearchCriteria>(stored.CriteriaJson)!;
                Assert.Equal(seed.CommuteTag, Assert.Single(repaired.ExcludedTags!).Id);
                Assert.Equal(seed.TrainingTag, Assert.Single(repaired.Tags).Id);
            }
            await page.GotoAsync(origin + $"/activities?excludeTags={seed.WestTag}&tagOwner={seed.EastOwner}",
                new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Button, new() { Name = "Switch to Search East", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("tag");
            await Assertions.Expect(page.Locator(".activity-row")).ToHaveCountAsync(0);
            Assert.Empty(errors);
        }
        catch
        {
            TestContext.Current.TestOutputHelper!.WriteLine(output.ToString());
            throw;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    private static async Task OpenSearchFixtureProfileAsync(IPage page, string origin, SearchBrowserSeed seed)
    {
        await page.GotoAsync(origin + "/activities", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.GetByLabel("Profile selector").SelectOptionAsync(seed.EastOwner.ToString());
        await Assertions.Expect(page.Locator(".organization-controls")).ToBeVisibleAsync();
    }

    private static async Task OpenCombinedSearchAsync(IPage page, string origin, SearchBrowserSeed seed)
    {
        await page.GotoAsync(origin + seed.CombinedUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.GetByRole(AriaRole.Button, new() { Name = "Switch to Search East", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".organization-controls")).ToBeVisibleAsync();
    }

    private static async Task AssertSearchResponsiveAsync(IPage page)
    {
        var captures = Path.Combine(FindRepositoryRoot(), "artifacts", "search-ui");
        Directory.CreateDirectory(captures);
        foreach (var width in new[] { 320, 360, 375, 768, 1121, 1280, 1920 })
        {
            await page.SetViewportSizeAsync(width, 1000);
            await page.Locator(".sidebar").EvaluateAsync("element => Promise.all(element.getAnimations().map(animation => animation.finished))");
            await AssertNoDocumentOverflowAsync(page, $"Activity search controls at {width}px");
            if (width is 375 or 1280)
                await page.ScreenshotAsync(new() { Path = Path.Combine(captures, $"search-controls-{width}.png"), FullPage = true });
        }
        await page.SetViewportSizeAsync(1280, 1000);
        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        await page.EvaluateAsync("document.documentElement.style.fontSize='200%'; document.body.style.fontSize='30px'");
        await AssertNoDocumentOverflowAsync(page, "Activity search controls with enlarged text");
        await page.EvaluateAsync("document.documentElement.style.removeProperty('font-size'); document.body.style.removeProperty('font-size')");
    }

    private static async Task<SearchBrowserSeed> SeedSearchBrowserDataAsync(string dataRoot)
    {
        await using var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot));
        await db.Database.EnsureCreatedAsync();
        var east = new OwnerProfile { DisplayName = "Search East", TimeZoneId = "Europe/Copenhagen" };
        var west = new OwnerProfile { DisplayName = "Search West", TimeZoneId = "America/Los_Angeles" };
        var empty = new OwnerProfile { DisplayName = "Search Empty", TimeZoneId = "UTC" };
        var training = new Tag { OwnerId = east.Id, Name = "Training", NormalizedName = "TRAINING" };
        var intervals = new Tag { OwnerId = east.Id, Name = "Intervals", NormalizedName = "INTERVALS" };
        var commute = new Tag { OwnerId = east.Id, Name = "Commute", NormalizedName = "COMMUTE" };
        var westTag = new Tag { OwnerId = west.Id, Name = "Training", NormalizedName = "TRAINING" };
        db.AddRange(east, west, empty, training, intervals, commute, westTag);
        var matches = Enumerable.Range(1, 30).Select(index => Candidate($"Grøn interval {index:00}", index)).ToArray();
        db.Activities.AddRange(matches);
        AddDecoy("Distance outside range", activity => activity.DistanceMeters = 41000);
        AddDecoy("Moving time outside range", activity => activity.MovingTimeSeconds = 3661);
        AddDecoy("Ascent outside range", activity => activity.ElevationGainMeters = 201);
        AddDecoy("No recorded power", activity => activity.HasPower = false);
        AddDecoy("Different gear", activity => activity.GearName = "Road bike spare");
        AddDecoy("Different device", activity => activity.DeviceName = "Fictional Edge Plus");
        AddDecoy("Indoor activity", activity => activity.IsIndoor = true);
        AddDecoy("No GPS activity", activity => activity.HasGps = false);
        AddDecoy("Missing interval tag", activity => activity.Tags.RemoveAll(tag => tag.TagId == intervals.Id));
        AddDecoy("Excluded commute", activity => activity.Tags.Add(new ActivityTag { ActivityId = activity.Id, TagId = commute.Id }));
        AddDecoy("Different sport", activity => activity.Sport = SportKind.Running);
        AddDecoy("Different date", activity => activity.StartTimeUtc = new(2026, 1, 3, 12, 0, 0, TimeSpan.Zero));
        AddDecoy("Untagged activity", activity => activity.Tags.Clear());
        AddDecoy("Missing text word", activity => activity.Description = "A steady synthetic climb.");
        var literal = Candidate("ÆBLE GRØN literal", 1);
        literal.Tags.Add(new ActivityTag { ActivityId = literal.Id, TagId = commute.Id });
        db.Activities.Add(literal);
        var western = Candidate("Grøn western ride", 1);
        western.OwnerId = west.Id;
        western.Tags = [new ActivityTag { ActivityId = western.Id, TagId = westTag.Id }];
        db.Activities.Add(western);
        var legacyJson = JsonSerializer.Serialize(new
        {
            Version = 1,
            Sport = (SportKind?)null,
            Search = "ÆBLE GRØN",
            HasPower = (bool?)null,
            Device = (string?)null,
            Tags = Array.Empty<SavedTagReference>(),
            Sort = "start-desc",
            Dates = new ReportingDateSelection(ReportingPreset.AllTime)
        });
        var legacy = new SavedSearch
        {
            OwnerId = east.Id,
            Name = "Legacy literal search",
            NormalizedName = "LEGACY LITERAL SEARCH",
            CriteriaJson = legacyJson
        };
        db.SavedSearches.Add(legacy);
        var missingTag = Guid.NewGuid();
        var repair = new SavedSearch
        {
            OwnerId = east.Id,
            Name = "Missing excluded tag search",
            NormalizedName = "MISSING EXCLUDED TAG SEARCH",
            CriteriaJson = JsonSerializer.Serialize(new SavedSearchCriteria(2, SportKind.Cycling, "æble grøn", null, null,
                [new SavedTagReference(training.Id, training.Name)], "start-desc", new(ReportingPreset.AllTime),
                TextMode: ActivityTextMode.Words, ExcludedTags: [new SavedTagReference(commute.Id, commute.Name),
                    new SavedTagReference(missingTag, "Retired exclusion")]))
        };
        db.SavedSearches.Add(repair);
        await db.SaveChangesAsync();
        var combinedUrl = "/activities?from=2026-01-02&to=2026-01-02&sport=Cycling&q=" + Uri.EscapeDataString("æble grøn")
            + "&textMode=words&power=yes&device=Fictional%20Edge&deviceExact=yes&gear=Road%20bike&indoor=no&gps=yes"
            + "&minDistance=10000&maxDistance=40000&minMoving=1800&maxMoving=3600&minAscent=100&maxAscent=200"
            + $"&tags={training.Id},{intervals.Id}&tagMode=all&excludeTags={commute.Id}&tagPresence=tagged&tagOwner={east.Id}";
        return new SearchBrowserSeed(east.Id, west.Id, empty.Id, training.Id, intervals.Id, commute.Id, westTag.Id,
            matches.Select(activity => activity.Id).ToArray(), literal.Id, legacy.Id, repair.Id, legacyJson, combinedUrl);

        Activity Candidate(string title, int index)
        {
            var activity = ReportingBrowserActivity(east.Id, title,
                new DateTimeOffset(2026, 1, 1, 23, 30, 0, TimeSpan.Zero).AddSeconds(index),
                10000 + index * 1000, 1800 + index * 60, 100 + index);
            activity.Description = "Steady ÆBLE climb after a fictional training session.";
            activity.DeviceName = "Fictional Edge";
            activity.GearName = "Road bike";
            activity.HasPower = true;
            activity.HasGps = true;
            activity.IsIndoor = false;
            activity.Tags = [new ActivityTag { ActivityId = activity.Id, TagId = training.Id },
                new ActivityTag { ActivityId = activity.Id, TagId = intervals.Id }];
            return activity;
        }

        void AddDecoy(string reason, Action<Activity> change)
        {
            var activity = Candidate("Grøn decoy: " + reason, 1);
            change(activity);
            db.Activities.Add(activity);
        }
    }

    private sealed record SearchBrowserSeed(Guid EastOwner, Guid WestOwner, Guid EmptyOwner, Guid TrainingTag, Guid IntervalTag,
        Guid CommuteTag, Guid WestTag, IReadOnlyList<Guid> MatchIds, Guid LiteralActivity, Guid LegacySavedSearch, Guid RepairSavedSearch,
        string LegacyCriteriaJson, string CombinedUrl);
}
