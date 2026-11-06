using System.Text;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    [Fact]
    public async Task Route_upload_reports_unconfirmed_responses_without_retrying()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("<input id='route-file' type='file'>");
        await page.AddScriptTagAsync(new() { Content = await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "src/ActivityExplorer.Web/wwwroot/js/imports.js")) });
        await page.Locator("input").SetInputFilesAsync(new FilePayload { Name = "route.gpx", MimeType = "application/gpx+xml", Buffer = Encoding.UTF8.GetBytes("synthetic") });
        var results = await page.EvaluateAsync<string[]>("""
            async () => {
                const outcomes = [
                    () => new Response('<!DOCTYPE html>', { status: 500 }),
                    () => { throw new Error('connection lost'); },
                    () => new Response('{', { status: 200 }),
                    () => new Response(JSON.stringify({ error: 'Invalid route' }), { status: 400 }),
                    () => new Response(JSON.stringify({ id: '00000000-0000-0000-0000-000000000001', fileCleanupPending: true }))
                ];
                let calls = 0;
                window.fetch = async url => {
                    if (url === '/internal/antiforgery/token') return new Response(JSON.stringify({ token: 'synthetic' }));
                    return outcomes[calls++]();
                };
                const messages = [];
                for (let i = 0; i < outcomes.length; i++) {
                    try {
                        const result = await activityExplorerImports.uploadRoute('route-file', 'owner', 'Running', 'Route');
                        messages.push(result.id + ':' + result.fileCleanupPending);
                    } catch (error) { messages.push(error.message); }
                }
                messages.push(String(calls));
                return messages;
            }
            """);
        Assert.All(results.Take(3), message => Assert.Contains("Check saved routes before trying again", message));
        Assert.Equal("Invalid route", results[3]);
        Assert.Equal("00000000-0000-0000-0000-000000000001:true", results[4]);
        Assert.Equal("5", results[5]);
    }
}
