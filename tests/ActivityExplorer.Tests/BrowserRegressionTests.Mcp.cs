using System.Text;
using ActivityExplorer.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using ModelContextProtocol.Client;
using static Microsoft.Playwright.Assertions;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    [Fact]
    public async Task Mcp_web_review_file_selection_decline_and_stale_changes_are_browser_safe()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        await using var runtime = await McpWriteRuntime.StartAsync();
        await using var client = await McpClient.CreateAsync(McpWriteStdioTests.Transport(runtime.Fixture));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new() { Width = 375, Height = 900 }, ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(runtime.Origin + "/mcp");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "MCP access", Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByLabel("File for a later import")).ToBeEnabledAsync();
        await page.GetByLabel("File for a later import").SetInputFilesAsync(new FilePayload
        { Name = "Approved synthetic track.gpx", MimeType = "application/gpx+xml", Buffer = Encoding.UTF8.GetBytes(TestSupport.Gpx()) });
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("No import has started.");
        var assets = McpReadOnlyTests.Data(await client.CallToolAsync("get_write_context", new Dictionary<string, object?> { ["profileId"] = runtime.Fixture.Owner, ["kind"] = "assets" }));
        Assert.Equal(1, assets.GetProperty("total").GetInt32());
        await using (var db = await runtime.Fixture.Factory.CreateDbContextAsync()) Assert.Empty(await db.ImportBatches.ToArrayAsync());

        async Task<Guid> Prepare(string name)
        {
            var result = McpReadOnlyTests.Data(await client.CallToolAsync("prepare_create_tag", new Dictionary<string, object?> { ["profileId"] = runtime.Fixture.Owner, ["name"] = name }));
            return result.GetProperty("planId").GetGuid();
        }
        var id = await Prepare("Web reviewed tag");
        await page.GotoAsync(runtime.Origin + "/mcp/review/" + id);
        await Expect(page.GetByText("Web reviewed tag", new() { Exact = false })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Approve and apply change" })).ToBeEnabledAsync();
        var capture = Path.Combine(FindRepositoryRoot(), "artifacts", "mcp-ui");
        Directory.CreateDirectory(capture);
        foreach (var width in new[] { 320, 375, 768, 1280 })
        {
            await page.SetViewportSizeAsync(width, 900);
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"));
        }
        await page.ScreenshotAsync(new() { Path = Path.Combine(capture, "review-desktop.png"), FullPage = true });
        await page.SetViewportSizeAsync(375, 900);
        await Expect(page.Locator("html")).ToHaveCSSAsync("font-size", "16px");
        await Expect(page.Locator("body")).ToHaveCSSAsync("font-size", "15px");
        await page.EvaluateAsync("""
            () => {
                document.documentElement.style.setProperty('font-size', '32px', 'important');
                document.body.style.setProperty('font-size', '30px', 'important');
            }
            """);
        await Expect(page.Locator("html")).ToHaveCSSAsync("font-size", "32px");
        await Expect(page.Locator("body")).ToHaveCSSAsync("font-size", "30px");
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"));
        await page.ScreenshotAsync(new() { Path = Path.Combine(capture, "review-mobile-large-text.png"), FullPage = true });
        await page.GetByRole(AriaRole.Button, new() { Name = "Approve and apply change" }).FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Change approved.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var status = McpReadOnlyTests.Data(await client.CallToolAsync("get_operation_status", new Dictionary<string, object?> { ["operationId"] = id }, cancellationToken: deadline.Token));
            if (status.GetProperty("status").GetString() == "completed") break;
            await Task.Delay(20, deadline.Token);
        }
        await using (var db = await runtime.Fixture.Factory.CreateDbContextAsync()) Assert.Single(await db.Tags.Where(tag => tag.Name == "Web reviewed tag").ToArrayAsync());

        var decline = await Prepare("Web declined tag");
        await page.GotoAsync(runtime.Origin + "/mcp/review/" + decline);
        await page.GetByRole(AriaRole.Button, new() { Name = "Decline change" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("No operation was queued.");
        var stale = await Prepare("Stale web tag");
        await using (var db = await runtime.Fixture.Factory.CreateDbContextAsync())
        {
            var activity = await db.Activities.SingleAsync(item => item.Id == runtime.Fixture.Activity);
            activity.Title = "Changed while reviewing"; activity.MutationVersion++;
            await db.SaveChangesAsync();
        }
        await page.GotoAsync(runtime.Origin + "/mcp/review/" + stale);
        await page.GetByRole(AriaRole.Button, new() { Name = "Approve and apply change" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("data changed after preview");
        await using (var db = await runtime.Fixture.Factory.CreateDbContextAsync()) Assert.False(await db.Tags.AnyAsync(tag => tag.Name == "Stale web tag" || tag.Name == "Web declined tag"));
        Assert.Empty(errors);
    }
}
