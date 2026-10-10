using System.Text;
using System.Text.Json;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    [Fact]
    public async Task Organization_cross_page_batches_saved_searches_and_bookmarks_recover_explicitly()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedOrganizationBrowserDataAsync(dataRoot);
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
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.EastOwner.ToString());
            await AssertReportingTotalsAsync(page, "30", $"{465d:N1} km", "30:00:00", "300 m");
            await Assertions.Expect(page.Locator(".activity-row")).ToHaveCountAsync(25);
            var selected = await OrganizationVisibleIdsAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Select this page", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".pager")).ToContainTextAsync("Page 2 of 2");
            await Assertions.Expect(page.Locator(".bulk-actions")).ToContainTextAsync("25 selected");
            selected.Add((await OrganizationVisibleIdsAsync(page))[0]);
            await page.Locator(".activity-selector input").First.CheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Edit selected", Exact = true }).ClickAsync();
            var batch = page.GetByRole(AriaRole.Region, new() { Name = "Bulk activity editor" });
            await batch.GetByRole(AriaRole.Group, new() { Name = "Add tags", Exact = true }).GetByLabel("Race", new() { Exact = true }).CheckAsync();
            await batch.GetByLabel("Gear change", new() { Exact = true }).SelectOptionAsync("Set");
            await batch.GetByLabel("Gear name", new() { Exact = true }).FillAsync("Road bike");
            await batch.GetByRole(AriaRole.Button, new() { Name = "Review changes", Exact = true }).ClickAsync();
            await Assertions.Expect(batch).ToContainTextAsync("26 activities");
            await Assertions.Expect(batch).ToContainTextAsync("Set gear to: Road bike");
            await AssertOrganizationResponsiveAsync(page, "batch-review");
            await batch.GetByRole(AriaRole.Button, new() { Name = "Apply changes", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByText("Reviewed 26 activities; changed 26.", new() { Exact = true })).ToBeVisibleAsync();
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                var changed = await db.Activities.Where(activity => activity.GearName == "Road bike").Select(activity => activity.Id).ToListAsync();
                Assert.Equal(selected.Order(), changed.Order());
                Assert.Equal(26, await db.ActivityTags.CountAsync(tag => selected.Contains(tag.ActivityId) && tag.TagId == seed.RaceTag));
            }

            await page.GetByRole(AriaRole.Group, new() { Name = "Tags — match any", Exact = true }).GetByLabel("Recovery", new() { Exact = true }).CheckAsync();
            await ApplyReportingFiltersAsync(page);
            await Assertions.Expect(page.Locator(".activity-totals")).ToContainTextAsync("30");
            var saved = page.Locator(".saved-searches");
            await saved.Locator("summary").ClickAsync();
            await saved.GetByLabel("Search name", new() { Exact = true }).FillAsync("My recovery library");
            await saved.GetByRole(AriaRole.Button, new() { Name = "Save new search", Exact = true }).ClickAsync();
            await Assertions.Expect(saved.GetByRole(AriaRole.Status)).ToContainTextAsync("Saved search.");
            await saved.GetByLabel("Search name", new() { Exact = true }).FillAsync("Renamed recovery library");
            await saved.GetByRole(AriaRole.Button, new() { Name = "Rename search", Exact = true }).ClickAsync();
            await Assertions.Expect(saved.GetByRole(AriaRole.Status)).ToContainTextAsync("Renamed search.");
            await page.GetByRole(AriaRole.Textbox, new() { Name = "Search", Exact = true }).FillAsync("Reporting");
            await ApplyReportingFiltersAsync(page);
            await saved.GetByRole(AriaRole.Button, new() { Name = "Replace with applied filters", Exact = true }).ClickAsync();
            await Assertions.Expect(saved.GetByRole(AriaRole.Status)).ToContainTextAsync("Saved search.");
            await page.GetByRole(AriaRole.Button, new() { Name = "Reset", Exact = true }).ClickAsync();
            await saved.GetByRole(AriaRole.Button, new() { Name = "Open search", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("tags="));
            var bookmark = page.Url;
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByText("This tag filter belongs to another profile. Switch to that profile or explicitly remove the tag filter.", new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".activity-row")).ToHaveCountAsync(0);
            await page.GetByRole(AriaRole.Button, new() { Name = "Switch to Reporting East", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".activity-row")).ToHaveCountAsync(25);
            Assert.Equal(bookmark, page.Url);

            saved = page.Locator(".saved-searches");
            await saved.Locator("summary").ClickAsync();
            await saved.GetByLabel("Saved search", new() { Exact = true }).SelectOptionAsync(seed.MissingSavedSearch.ToString());
            await saved.GetByRole(AriaRole.Button, new() { Name = "Open search", Exact = true }).ClickAsync();
            var repair = saved.GetByRole(AriaRole.Region, new() { Name = "Repair saved search" });
            await Assertions.Expect(repair).ToContainTextAsync("Retired training");
            Assert.Equal(bookmark, page.Url);
            var manager = page.Locator(".tag-manager");
            await manager.Locator("summary").ClickAsync();
            await manager.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^Delete tag Long training label") }).ClickAsync();
            await manager.GetByRole(AriaRole.Button, new() { Name = "Permanently delete tag", Exact = true }).ClickAsync();
            await Assertions.Expect(repair).ToContainTextAsync("Long training label");
            await repair.GetByLabel("Race", new() { Exact = true }).CheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Edit all matching", Exact = true }).ClickAsync();
            await Assertions.Expect(repair.GetByRole(AriaRole.Button, new() { Name = "Save repaired search and open", Exact = true })).ToBeDisabledAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Cancel editing", Exact = true }).ClickAsync();
            await AssertOrganizationResponsiveAsync(page, "saved-search-repair");
            await repair.GetByRole(AriaRole.Button, new() { Name = "Save repaired search and open", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(seed.RaceTag.ToString()));
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                var search = await db.SavedSearches.SingleAsync(item => item.Id == seed.MissingSavedSearch);
                var criteria = JsonSerializer.Deserialize<SavedSearchCriteria>(search.CriteriaJson)!;
                Assert.Equal(seed.RaceTag, Assert.Single(criteria.Tags).Id);
            }
            await page.GoBackAsync();
            await Assertions.Expect(page).ToHaveURLAsync(bookmark);
            await page.GoForwardAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(seed.RaceTag.ToString()));
            await saved.GetByRole(AriaRole.Button, new() { Name = "Delete search", Exact = true }).ClickAsync();
            await saved.GetByRole(AriaRole.Button, new() { Name = "Confirm delete search", Exact = true }).ClickAsync();
            await Assertions.Expect(saved.GetByRole(AriaRole.Status)).ToContainTextAsync("Deleted saved search.");
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.EmptyOwner.ToString());
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Edit selected", Exact = true })).ToHaveCountAsync(0);
            Assert.DoesNotContain("tags=", page.Url, StringComparison.Ordinal);
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
    public async Task Organization_stale_batches_reject_and_filtered_snapshots_exclude_later_imports()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedOrganizationBrowserDataAsync(dataRoot);
        var origin = $"http://127.0.0.1:{ReservePort()}";
        var output = new StringBuilder();
        using var process = StartApplication(FindWebAssembly(FindRepositoryRoot()), dataRoot, origin, output);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync();
            await page.GotoAsync(origin + "/activities", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.EastOwner.ToString());
            await page.GetByRole(AriaRole.Group, new() { Name = "Tags — match any", Exact = true }).GetByLabel("Recovery", new() { Exact = true }).CheckAsync();
            await ApplyReportingFiltersAsync(page);
            await PrepareRemoval();
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                var member = await db.Activities.SingleAsync(activity => activity.Id == seed.ActivityId);
                member.Description = "An intervening local edit";
                member.MutationVersion++;
                await db.SaveChangesAsync();
            }
            var batch = page.GetByRole(AriaRole.Region, new() { Name = "Bulk activity editor" });
            await batch.GetByRole(AriaRole.Button, new() { Name = "Apply changes", Exact = true }).ClickAsync();
            await Assertions.Expect(batch.GetByRole(AriaRole.Alert)).ToContainTextAsync("Nothing changed");
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
                Assert.Equal(30, await db.ActivityTags.CountAsync(tag => tag.TagId == seed.RecoveryTag));
            await batch.GetByRole(AriaRole.Button, new() { Name = "Refresh activities", Exact = true }).ClickAsync();
            await PrepareRemoval();
            var lateId = Guid.NewGuid();
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                db.Activities.Add(new Activity
                {
                    Id = lateId,
                    OwnerId = seed.EastOwner,
                    Title = "Later matching import",
                    Sport = SportKind.Cycling,
                    NaturalFingerprint = lateId.ToString(),
                    StartTimeUtc = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero),
                    Tags = [new ActivityTag { ActivityId = lateId, TagId = seed.RecoveryTag }]
                });
                await db.SaveChangesAsync();
            }
            await batch.GetByRole(AriaRole.Button, new() { Name = "Apply changes", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByText("Reviewed 30 activities; changed 30.", new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".activity-row")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".activity-row")).ToContainTextAsync("Later matching import");
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
                Assert.Equal(lateId, await db.ActivityTags.Where(tag => tag.TagId == seed.RecoveryTag).Select(tag => tag.ActivityId).SingleAsync());

            async Task PrepareRemoval()
            {
                await page.GetByRole(AriaRole.Button, new() { Name = "Edit all matching", Exact = true }).ClickAsync();
                var editor = page.GetByRole(AriaRole.Region, new() { Name = "Bulk activity editor" });
                await editor.GetByRole(AriaRole.Group, new() { Name = "Remove tags", Exact = true }).GetByLabel("Recovery", new() { Exact = true }).CheckAsync();
                await editor.GetByRole(AriaRole.Button, new() { Name = "Review changes", Exact = true }).ClickAsync();
                await Assertions.Expect(editor).ToContainTextAsync("30 activities");
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    private static async Task<List<Guid>> OrganizationVisibleIdsAsync(IPage page)
    {
        var links = await page.Locator(".activity-row").EvaluateAllAsync<string[]>("elements => elements.map(element => element.getAttribute('href'))");
        return links.Select(link => Guid.Parse(link.Split('/').Last())).ToList();
    }
}
