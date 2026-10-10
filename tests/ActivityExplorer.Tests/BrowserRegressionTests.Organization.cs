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
    public async Task Organization_preview_fixture_is_created_only_when_explicitly_requested()
    {
        var requestedPath = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_ORGANIZATION_PREVIEW_DATA");
        if (string.IsNullOrWhiteSpace(requestedPath)) return;
        var path = Path.GetFullPath(requestedPath);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException("The organization preview fixture requires an empty directory.");
        Directory.CreateDirectory(path);
        var seed = await SeedOrganizationBrowserDataAsync(path);
        await File.WriteAllTextAsync(Path.Combine(path, "synthetic-fixture.json"), JsonSerializer.Serialize(seed));
        TestContext.Current.TestOutputHelper!.WriteLine($"Synthetic organization preview data: {path}");
    }

    [Fact]
    public async Task Organization_tags_support_keyboard_editing_stale_recovery_and_owner_transfer()
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
            var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 1000 }, HasTouch = true });
            var page = await context.NewPageAsync();
            var errors = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            await page.GotoAsync(origin + "/activities", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator(".activity-row").Filter(new() { HasText = "Reporting ride 30" })).ToContainTextAsync("Recovery");
            await Assertions.Expect(page.GetByText("Manage tags", new() { Exact = true })).ToHaveCountAsync(0);
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.EastOwner.ToString());

            var manager = page.Locator(".tag-manager");
            await manager.Locator("summary").FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(manager).ToHaveAttributeAsync("open", "");
            await manager.GetByLabel("New tag name", new() { Exact = true }).FillAsync("  Tempo  ");
            await manager.GetByRole(AriaRole.Button, new() { Name = "Create tag", Exact = true }).ClickAsync();
            await Assertions.Expect(manager.GetByRole(AriaRole.Status)).ToContainTextAsync("Tag created.");
            await manager.GetByLabel("New tag name", new() { Exact = true }).FillAsync("TEMPO");
            await manager.GetByRole(AriaRole.Button, new() { Name = "Create tag", Exact = true }).ClickAsync();
            await Assertions.Expect(manager.GetByRole(AriaRole.Alert)).ToContainTextAsync("already exists");
            await Assertions.Expect(manager.GetByLabel("New tag name", new() { Exact = true })).ToHaveValueAsync("TEMPO");
            await manager.GetByRole(AriaRole.Button, new() { Name = "Rename tag Tempo", Exact = true }).ClickAsync();
            await Assertions.Expect(manager.GetByLabel("Tag name", new() { Exact = true })).ToBeFocusedAsync();
            await manager.GetByLabel("Tag name", new() { Exact = true }).FillAsync("Tempo sessions");
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(manager.GetByRole(AriaRole.Status)).ToContainTextAsync("Tag renamed.");
            await manager.GetByRole(AriaRole.Button, new() { Name = "Delete tag Recovery", Exact = true }).ClickAsync();
            var review = manager.Locator(".tag-delete-review");
            await Assertions.Expect(review).ToContainTextAsync("30 activities");
            await Assertions.Expect(review).ToContainTextAsync("1 saved search will need repair");
            await AssertOrganizationResponsiveAsync(page, "tag-management-review");
            await review.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            await Assertions.Expect(manager.Locator("summary")).ToBeFocusedAsync();
            await manager.GetByRole(AriaRole.Button, new() { Name = "Delete tag Tempo sessions", Exact = true }).ClickAsync();
            await review.GetByRole(AriaRole.Button, new() { Name = "Permanently delete tag", Exact = true }).ClickAsync();
            await Assertions.Expect(manager.GetByRole(AriaRole.Status)).ToContainTextAsync("Tag deleted.");

            await page.GotoAsync(origin + $"/activities/{seed.ActivityId}?axis=distance", new() { WaitUntil = WaitUntilState.NetworkIdle });
            var tagEditor = page.GetByRole(AriaRole.Region, new() { Name = "Activity tags", Exact = true });
            await Assertions.Expect(tagEditor.GetByRole(AriaRole.List, new() { Name = "Assigned tags" })).ToContainTextAsync("Recovery");
            await tagEditor.GetByRole(AriaRole.Button, new() { Name = "Edit tags", Exact = true }).FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            var raceCheckbox = tagEditor.GetByRole(AriaRole.Checkbox, new() { Name = "Race", Exact = true });
            await raceCheckbox.FocusAsync();
            await page.Keyboard.PressAsync("Space");
            await Assertions.Expect(raceCheckbox).Not.ToBeCheckedAsync();
            var visibleFocus = await raceCheckbox.EvaluateAsync<bool>("element => getComputedStyle(element).outlineStyle !== 'none' || getComputedStyle(element).boxShadow !== 'none'");
            Assert.True(visibleFocus, "The focused tag checkbox must have a visible focus indicator.");
            await tagEditor.GetByLabel("New tag name", new() { Exact = true }).FillAsync("Technical drills");
            await tagEditor.GetByRole(AriaRole.Button, new() { Name = "Create tag", Exact = true }).TapAsync();
            await Assertions.Expect(tagEditor.GetByRole(AriaRole.Checkbox, new() { Name = "Technical drills", Exact = true })).ToBeCheckedAsync();
            await AssertOrganizationResponsiveAsync(page, "activity-tag-editor");
            await tagEditor.GetByRole(AriaRole.Button, new() { Name = "Save tags", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByText("Activity tags saved.", new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(tagEditor.GetByRole(AriaRole.List, new() { Name = "Assigned tags" })).ToContainTextAsync("Technical drills");
            Assert.EndsWith("?axis=distance", page.Url, StringComparison.Ordinal);
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                var activity = await db.Activities.SingleAsync(item => item.Id == seed.ActivityId);
                Assert.False(activity.UserEdited);
                Assert.Equal(1, activity.MutationVersion);
            }

            await tagEditor.GetByRole(AriaRole.Button, new() { Name = "Edit tags", Exact = true }).ClickAsync();
            await tagEditor.GetByRole(AriaRole.Checkbox, new() { Name = "Race", Exact = true }).CheckAsync();
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                var activity = await db.Activities.SingleAsync(item => item.Id == seed.ActivityId);
                activity.MutationVersion++;
                await db.SaveChangesAsync();
            }
            await tagEditor.GetByRole(AriaRole.Button, new() { Name = "Save tags", Exact = true }).ClickAsync();
            await Assertions.Expect(tagEditor.GetByRole(AriaRole.Alert)).ToContainTextAsync("Nothing changed");
            await tagEditor.GetByRole(AriaRole.Button, new() { Name = "Reload activity", Exact = true }).ClickAsync();
            await Assertions.Expect(tagEditor.GetByRole(AriaRole.Button, new() { Name = "Edit tags", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(tagEditor.GetByRole(AriaRole.List, new() { Name = "Assigned tags" })).Not.ToContainTextAsync("Race");

            await page.GetByRole(AriaRole.Button, new() { Name = "Edit activity", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile", Exact = true }).SelectOptionAsync(seed.WestOwner.ToString());
            await page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByText("Activity metadata saved locally.", new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".page-metadata")).ToContainTextAsync("Reporting West");
            await Assertions.Expect(tagEditor.GetByRole(AriaRole.List, new() { Name = "Assigned tags" })).ToContainTextAsync("Technical drills");
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                var assigned = await db.ActivityTags.Where(item => item.ActivityId == seed.ActivityId).Include(item => item.Tag).ToArrayAsync();
                Assert.All(assigned, item => Assert.Equal(seed.WestOwner, item.Tag!.OwnerId));
                Assert.Contains(assigned, item => item.TagId == seed.WestRecoveryTag);
                Assert.Equal(1, await db.Tags.CountAsync(tag => tag.OwnerId == seed.WestOwner && tag.NormalizedName == "RECOVERY"));
                Assert.True(await db.Tags.AnyAsync(tag => tag.Id == seed.RecoveryTag));
            }
            await tagEditor.GetByRole(AriaRole.Button, new() { Name = "Edit tags", Exact = true }).ClickAsync();
            await Assertions.Expect(tagEditor.GetByRole(AriaRole.Checkbox, new() { Name = "Race", Exact = true })).ToHaveCountAsync(0);
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

    private static async Task AssertOrganizationResponsiveAsync(IPage page, string name)
    {
        var captures = Path.Combine(FindRepositoryRoot(), "artifacts", "organization-ui");
        Directory.CreateDirectory(captures);
        foreach (var width in new[] { 320, 360, 375, 768, 1121, 1280, 1920 })
        {
            await page.SetViewportSizeAsync(width, 1000);
            await page.Locator(".sidebar").EvaluateAsync("element => Promise.all(element.getAnimations().map(animation => animation.finished))");
            await AssertNoDocumentOverflowAsync(page, $"{name} at {width}px");
            if (width is 375 or 1280)
                await page.ScreenshotAsync(new() { Path = Path.Combine(captures, $"{name}-{width}.png"), FullPage = true });
        }
        await page.SetViewportSizeAsync(1280, 1000);
        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        await page.EvaluateAsync("document.documentElement.style.fontSize='200%'; document.body.style.fontSize='30px'");
        await AssertNoDocumentOverflowAsync(page, $"{name} with enlarged text");
        await page.EvaluateAsync("document.documentElement.style.removeProperty('font-size'); document.body.style.removeProperty('font-size')");
    }

    private static async Task<OrganizationBrowserSeed> SeedOrganizationBrowserDataAsync(string dataRoot)
    {
        var reporting = await SeedReportingBrowserDataAsync(dataRoot);
        await using var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot));
        var recovery = new Tag { OwnerId = reporting.EastOwner, Name = "Recovery", NormalizedName = "RECOVERY" };
        var race = new Tag { OwnerId = reporting.EastOwner, Name = "Race", NormalizedName = "RACE" };
        var longName = "Long training label with a deliberately unbroken ending " + new string('x', 24);
        var longTag = new Tag { OwnerId = reporting.EastOwner, Name = longName, NormalizedName = longName.ToUpperInvariant() };
        var westRecovery = new Tag { OwnerId = reporting.WestOwner, Name = "Recovery", NormalizedName = "RECOVERY" };
        db.Tags.AddRange(recovery, race, longTag, westRecovery);
        var activities = await db.Activities.Where(activity => activity.OwnerId == reporting.EastOwner).OrderBy(activity => activity.Title).ToArrayAsync();
        for (var index = 0; index < activities.Length; index++)
        {
            db.ActivityTags.Add(new ActivityTag { ActivityId = activities[index].Id, TagId = recovery.Id });
            if (index < 15) db.ActivityTags.Add(new ActivityTag { ActivityId = activities[index].Id, TagId = race.Id });
        }
        db.ActivityTags.Add(new ActivityTag { ActivityId = activities[0].Id, TagId = longTag.Id });
        var missingTag = Guid.NewGuid();
        var criteria = new SavedSearchCriteria(1, SportKind.Cycling, "Reporting", null, null,
            [new SavedTagReference(recovery.Id, recovery.Name)], "start-desc", new(ReportingPreset.AllTime));
        var valid = new SavedSearch
        {
            OwnerId = reporting.EastOwner,
            Name = "Recovery rides",
            NormalizedName = "RECOVERY RIDES",
            CriteriaJson = JsonSerializer.Serialize(criteria)
        };
        var missing = new SavedSearch
        {
            OwnerId = reporting.EastOwner,
            Name = "Retired tag search",
            NormalizedName = "RETIRED TAG SEARCH",
            CriteriaJson = JsonSerializer.Serialize(criteria with { Tags = [new SavedTagReference(missingTag, "Retired training"), new SavedTagReference(longTag.Id, longTag.Name)] })
        };
        db.SavedSearches.AddRange(valid, missing);
        await db.SaveChangesAsync();
        return new OrganizationBrowserSeed(reporting.EastOwner, reporting.WestOwner, reporting.EmptyOwner, activities[0].Id,
            recovery.Id, race.Id, longTag.Id, missingTag, valid.Id, missing.Id, westRecovery.Id);
    }

    private sealed record OrganizationBrowserSeed(Guid EastOwner, Guid WestOwner, Guid EmptyOwner, Guid ActivityId,
        Guid RecoveryTag, Guid RaceTag, Guid LongTag, Guid MissingTag, Guid ValidSavedSearch, Guid MissingSavedSearch, Guid WestRecoveryTag);
}
