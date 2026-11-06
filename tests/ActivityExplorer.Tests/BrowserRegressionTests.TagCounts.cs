using System.Text;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    [Fact]
    public async Task Bulk_tag_membership_and_activity_deletion_refresh_open_manager_counts()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        const string tagName = "Synthetic live tag count";
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
            await Assertions.Expect(page.Locator(".activity-row")).ToHaveCountAsync(25);
            var manager = page.Locator(".tag-manager");
            await manager.Locator("summary").ClickAsync();
            await manager.GetByLabel("New tag name", new() { Exact = true }).FillAsync(tagName);
            await manager.GetByRole(AriaRole.Button, new() { Name = "Create tag", Exact = true }).ClickAsync();
            await Assertions.Expect(manager.GetByRole(AriaRole.Status)).ToContainTextAsync("Tag created.");
            var query = manager.GetByLabel("Find managed tags", new() { Exact = true });
            await query.FillAsync(tagName);
            var count = manager.Locator(".managed-tag .tag-description small");
            await Assertions.Expect(count).ToHaveTextAsync("0 activities");

            await page.Locator(".activity-selector input").Nth(0).CheckAsync();
            await page.Locator(".activity-selector input").Nth(1).CheckAsync();
            await ChangeTagsAsync(add: true);
            await Assertions.Expect(page.GetByText("Reviewed 2 activities; changed 2.", new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".activity-row").Filter(new() { HasText = tagName })).ToHaveCountAsync(2);
            await Assertions.Expect(count).ToHaveTextAsync("2 activities");

            await page.Locator(".activity-selector input").Nth(0).CheckAsync();
            await ChangeTagsAsync(add: false);
            await Assertions.Expect(page.GetByText("Reviewed 1 activities; changed 1.", new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(count).ToHaveTextAsync("1 activity");

            await page.Locator(".activity-selector input").Nth(1).CheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Delete selected", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Permanently delete activity", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByText("Permanently deleted 1 activity.", new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(count).ToHaveTextAsync("0 activities");
            await Assertions.Expect(manager).ToHaveAttributeAsync("open", "");
            await Assertions.Expect(query).ToHaveValueAsync(tagName);

            async Task ChangeTagsAsync(bool add)
            {
                await page.GetByRole(AriaRole.Button, new() { Name = "Edit selected", Exact = true }).ClickAsync();
                var editor = page.GetByRole(AriaRole.Region, new() { Name = "Bulk activity editor", Exact = true });
                await editor.GetByRole(AriaRole.Group, new() { Name = add ? "Add tags" : "Remove tags", Exact = true })
                    .GetByLabel(tagName, new() { Exact = true }).CheckAsync();
                await editor.GetByRole(AriaRole.Button, new() { Name = "Review changes", Exact = true }).ClickAsync();
                await editor.GetByRole(AriaRole.Button, new() { Name = "Apply changes", Exact = true }).ClickAsync();
            }
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
}
