using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    [Fact]
    public async Task Progress_preview_fixture_is_created_only_when_explicitly_requested()
    {
        var requestedPath = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_PROGRESS_PREVIEW_DATA");
        if (string.IsNullOrWhiteSpace(requestedPath)) return;
        var path = Path.GetFullPath(requestedPath);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException("The progress preview fixture requires an empty directory.");
        Directory.CreateDirectory(path);
        var seed = await SeedProgressBrowserDataAsync(path);
        await File.WriteAllTextAsync(Path.Combine(path, "synthetic-fixture.json"), JsonSerializer.Serialize(seed));
        TestContext.Current.TestOutputHelper!.WriteLine($"Synthetic progress preview data: {path}");
    }

    [Fact]
    public async Task Dashboard_and_calendar_preserve_reporting_state_totals_and_accessible_layouts()
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
            var navigation = new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle };
            await page.GotoAsync(origin, navigation);
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true })).ToHaveValueAsync("year-to-date");
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Trend grouping", Exact = true })).ToHaveValueAsync("monthly");
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Trend metric", Exact = true })).ToHaveValueAsync("distance");
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Compare with", Exact = true })).ToHaveValueAsync("prior-year");

            const string custom = "/?period=custom&from=2026-01-02&to=2026-01-02";
            await page.GotoAsync(origin + custom, navigation);
            await AssertProgressTotalsAsync(page, "31", $"{473d:N1} km", "33:00:00", "380 m", "1");
            await Assertions.Expect(page.Locator(".progress-totals .metric-card small").First).ToContainTextAsync("percentage unavailable (zero baseline)");
            await Assertions.Expect(page.Locator(".spark-chart .chart-sample")).ToHaveCountAsync(1);
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true }).SelectOptionAsync("all-time");
            await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]period=all-time(?:&|$)"));
            await AssertProgressTotalsAsync(page, "32", $"{480d:N1} km", "35:00:00", "450 m", "2");
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true })).ToHaveValueAsync("all-time");
            await page.GoBackAsync();
            await AssertProgressTotalsAsync(page, "31", $"{473d:N1} km", "33:00:00", "380 m", "1");
            await page.GoForwardAsync();
            await AssertProgressTotalsAsync(page, "32", $"{480d:N1} km", "35:00:00", "450 m", "2");
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true }).SelectOptionAsync("custom");
            await page.GetByLabel("From", new() { Exact = true }).FillAsync("");
            await page.GetByLabel("To", new() { Exact = true }).FillAsync("");
            await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]period=custom(?:&|$)"));
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true })).ToHaveValueAsync("custom");
            await Assertions.Expect(page.GetByLabel("From", new() { Exact = true })).ToHaveValueAsync("");
            await Assertions.Expect(page.GetByLabel("To", new() { Exact = true })).ToHaveValueAsync("");
            await AssertProgressTotalsAsync(page, "32", $"{480d:N1} km", "35:00:00", "450 m", "2");

            await page.GotoAsync(origin + custom, navigation);
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Trend grouping", Exact = true }).SelectOptionAsync("weekly");
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]group=weekly(?:&|$)"));
            foreach (var metric in new[] { "distance", "activities", "moving", "ascent", "active-days" })
            {
                await page.GetByRole(AriaRole.Combobox, new() { Name = "Trend metric", Exact = true }).SelectOptionAsync(metric);
                await Assertions.Expect(page).ToHaveURLAsync(new Regex($"[?&]metric={metric}(?:&|$)"));
                await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Trend metric", Exact = true })).ToHaveValueAsync(metric);
                await Assertions.Expect(page.Locator(".spark-chart .chart-sample")).ToHaveCountAsync(1);
                await AssertChartSampleMetadataAsync(page.Locator(".spark-chart"));
            }
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Trend grouping", Exact = true })).ToHaveValueAsync("weekly");
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Trend metric", Exact = true })).ToHaveValueAsync("active-days");
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Compare with", Exact = true }).SelectOptionAsync("previous-period");
            await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".progress-totals .metric-card small").First).ToContainTextAsync("Compared with 1");
            await Assertions.Expect(page.Locator(".progress-totals .metric-card small").First).ToContainTextAsync("+30");
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Sport", Exact = true }).SelectOptionAsync("running");
            await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters", Exact = true }).ClickAsync();
            await AssertProgressTotalsAsync(page, "0", "0 m", "0:00", "0 m", "0");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "No activities in this period", Exact = true })).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Sport", Exact = true }).SelectOptionAsync("cycling");
            await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]sport=cycling(?:&|$)"));
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Training calendar", Exact = true })).ToHaveAttributeAsync("href", "/calendar?month=2026-01&sport=cycling");
            await page.GetByLabel("Profile selector").EvaluateAsync("""
                (select, owners) => {
                    for (const owner of owners) {
                        select.value = owner;
                        select.dispatchEvent(new Event('change', { bubbles: true }));
                    }
                }
                """, new[] { seed.EmptyOwner.ToString(), seed.EastOwner.ToString(), seed.WestOwner.ToString() });
            await AssertProgressTotalsAsync(page, "1", $"{8d:N1} km", "3:00:00", "80 m", "1");
            await page.GetByLabel("Profile selector").SelectOptionAsync("");
            await AssertProgressTotalsAsync(page, "31", $"{473d:N1} km", "33:00:00", "380 m", "1");
            await AssertProgressResponsiveAsync(page, "dashboard");
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true }).FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            await page.Keyboard.PressAsync("Shift+Tab");
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true })).ToBeFocusedAsync();
            await AssertProgressEnlargedTextAsync(page, "Dashboard");

            await AssertProgressCalendarAsync(page, origin, seed);

            await page.GotoAsync(origin + custom, navigation);
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.EastOwner.ToString());
            await AssertProgressTotalsAsync(page, "30", $"{465d:N1} km", "30:00:00", "300 m", "1");
            await AddReportingLateImportAsync(dataRoot, seed.EastOwner);
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh dashboard", Exact = true }).ClickAsync();
            await AssertProgressTotalsAsync(page, "31", $"{470d:N1} km", "31:00:00", "310 m", "1");
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
                await db.Owners.Where(owner => owner.Id == seed.EastOwner)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(owner => owner.TimeZoneId, "Unavailable/Example"));
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh dashboard", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Alert).First).ToContainTextAsync("timezone");
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
                await db.Owners.Where(owner => owner.Id == seed.EastOwner)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(owner => owner.TimeZoneId, "Europe/Copenhagen"));
            await page.GetByRole(AriaRole.Button, new() { Name = "Try again", Exact = true }).ClickAsync();
            await AssertProgressTotalsAsync(page, "31", $"{470d:N1} km", "31:00:00", "310 m", "1");
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
    public async Task Benchmark_progress_shows_complete_owner_history_and_preserves_period_view_and_retry()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedProgressBrowserDataAsync(dataRoot);
        var origin = $"http://127.0.0.1:{ReservePort()}";
        var output = new StringBuilder();
        using var process = StartApplication(FindWebAssembly(FindRepositoryRoot()), dataRoot, origin, output);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync(new() { HasTouch = true, ViewportSize = new() { Width = 1280, Height = 1000 } });
            var errors = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            var navigation = new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle };
            var attempts = $"/records/attempts?sport=cycling&kind=distance&key=Longest%20distance&owner={seed.ProgressOwner}";
            await page.GotoAsync(origin + attempts, navigation);
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "View", Exact = true })).ToHaveValueAsync("strongest");
            await Assertions.Expect(page.Locator(".attempt-table tbody tr")).ToHaveCountAsync(50);
            await page.GetByRole(AriaRole.Combobox, new() { Name = "View", Exact = true }).SelectOptionAsync("progress");
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]view=progress(?:&|$)"));
            await Assertions.Expect(page.Locator(".benchmark-owner-series")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".benchmark-progress-chart .chart-sample")).ToHaveCountAsync(61);
            await Assertions.Expect(page.Locator(".history-table tbody tr")).ToHaveCountAsync(61);
            await Assertions.Expect(page.Locator(".history-table tbody tr").First).ToContainTextAsync("Progress ride 01");
            await Assertions.Expect(page.Locator(".history-table tbody tr").Last).ToContainTextAsync("Progress ride 61");
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "View", Exact = true })).ToHaveValueAsync("progress");
            await Assertions.Expect(page.Locator(".benchmark-progress-chart .chart-sample")).ToHaveCountAsync(61);
            await page.GoBackAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "View", Exact = true })).ToHaveValueAsync("strongest");
            await Assertions.Expect(page.Locator(".attempt-table tbody tr")).ToHaveCountAsync(50);
            await page.GoForwardAsync();
            await Assertions.Expect(page.Locator(".benchmark-progress-chart .chart-sample")).ToHaveCountAsync(61);
            await AssertProgressResponsiveAsync(page, "benchmark-progress");
            await AssertProgressEnlargedTextAsync(page, "Benchmark progress");

            var chart = page.Locator(".benchmark-progress-chart");
            await Assertions.Expect(chart).ToHaveAttributeAsync("data-charts-bound", "true");
            await AssertPointerInspectionAsync(page, chart, expectedCharts: 1);
            var plot = await chart.Locator(".chart-plot").BoundingBoxAsync();
            Assert.NotNull(plot);
            await page.Touchscreen.TapAsync(plot.X + plot.Width * 0.6f, plot.Y + plot.Height * 0.5f);
            await Assertions.Expect(chart.Locator(".chart-tooltip.visible")).ToContainTextAsync("Progress ride");
            await Assertions.Expect(chart.Locator(".chart-tooltip.visible")).ToContainTextAsync("best so far");
            await chart.GetByRole(AriaRole.Slider).FocusAsync();
            var originalInspection = await chart.Locator(".chart-exact-output").TextContentAsync();
            await chart.GetByRole(AriaRole.Slider).PressAsync("ArrowRight");
            await Assertions.Expect(chart.Locator(".chart-exact-output")).Not.ToHaveTextAsync(originalInspection ?? "");
            await page.GetByText("View chronological attempts", new() { Exact = true }).ClickAsync();
            await page.Locator(".history-table tbody tr").Last.Locator("th a").ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(origin + $"/activities/{seed.LastActivity}");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Progress ride 61", Exact = true })).ToBeVisibleAsync();
            await page.GoBackAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator(".benchmark-progress-chart .chart-sample")).ToHaveCountAsync(61);
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true }).SelectOptionAsync("custom");
            await Assertions.Expect(page.Locator(".attempts-filters")).ToContainTextAsync("From");
            await page.GetByLabel("From", new() { Exact = true }).FillAsync("2026-03-10");
            await page.GetByLabel("To", new() { Exact = true }).FillAsync("2026-03-20");
            await page.GetByRole(AriaRole.Button, new() { Name = "Apply period", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".benchmark-progress-chart .chart-sample")).ToHaveCountAsync(11);
            await Assertions.Expect(page.Locator(".history-table tbody tr").First).ToContainTextAsync("Progress ride 10");
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByLabel("From", new() { Exact = true })).ToHaveValueAsync("2026-03-10");
            await Assertions.Expect(page.GetByLabel("To", new() { Exact = true })).ToHaveValueAsync("2026-03-20");
            await Assertions.Expect(page.Locator(".history-table tbody tr")).ToHaveCountAsync(11);

            await page.GotoAsync(origin + "/records/attempts?sport=cycling&kind=distance&key=Longest%20distance&view=progress&from=2026-03-01&to=2026-05-01", navigation);
            await Assertions.Expect(page.Locator(".benchmark-owner-series")).ToHaveCountAsync(2);
            await Assertions.Expect(page.Locator(".benchmark-progress-chart").First).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".history-table tbody tr")).ToHaveCountAsync(62);
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.EmptyOwner.ToString());
            await Assertions.Expect(page.Locator(".empty-state")).ToContainTextAsync("No matching attempts");
            await page.GotoAsync(origin + attempts + "&view=progress&scope=indoor", navigation);
            await Assertions.Expect(page.Locator(".history-table tbody tr")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".history-table tbody tr")).ToContainTextAsync("Progress ride 61");

            byte[] originalPayload;
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                var stream = await db.ActivityStreams.SingleAsync(item => item.ActivityId == seed.LastActivity);
                originalPayload = stream.CompressedPayload;
                stream.CompressedPayload = [0, 1, 2];
                await db.SaveChangesAsync();
            }
            await page.GotoAsync(origin + $"/records/attempts?sport=cycling&kind=distanceeffort&key=5%20km&view=progress&scope=indoor&owner={seed.ProgressOwner}", navigation);
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Attempts could not be loaded");
            await Assertions.Expect(page.Locator(".benchmark-progress-chart")).ToHaveCountAsync(0);
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                (await db.ActivityStreams.SingleAsync(item => item.ActivityId == seed.LastActivity)).CompressedPayload = originalPayload;
                await db.SaveChangesAsync();
            }
            await page.GetByRole(AriaRole.Button, new() { Name = "Try again", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".empty-state")).ToContainTextAsync("No matching attempts");
            await AssertPendingProgressCancellationAsync(page, origin, dataRoot, seed);
            Assert.Empty(errors);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    private static async Task AssertPendingProgressCancellationAsync(IPage page, string origin, string dataRoot, ProgressBrowserSeed seed)
    {
        await page.GotoAsync(origin + $"/records/attempts?sport=cycling&kind=powercurve&key=5%20s&view=progress&owner={seed.ProgressOwner}&period=custom&from=2026-02-01&to=2026-02-28",
            new() { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page.Locator(".empty-state")).ToContainTextAsync("No matching attempts");

        // Only the cancellation scenario needs large streams; preview and ordinary history fixtures stay small.
        var start = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        byte[] payload;
        using (var buffer = new MemoryStream())
        {
            using (var compressed = new BrotliStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
                JsonSerializer.Serialize(compressed, Enumerable.Range(0, 200_001).Select(index => new TrackPoint(
                    start.AddSeconds(index), null, null, index * 10d, null, null, null, null, 200 + index % 19, null)),
                    JsonSerializerOptions.Web);
            payload = buffer.ToArray();
        }
        var quickPayload = TrackCodec.Encode(Enumerable.Range(0, 20).Select(index => new TrackPoint(
            start.AddDays(1).AddSeconds(index), null, null, index * 10d, null, null, null, null, 250, null)).ToArray());
        using var preparationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
        {
            await db.ActivityStreams.Where(stream => stream.OwnerId == seed.ProgressOwner).ExecuteUpdateAsync(setters => setters
                .SetProperty(stream => stream.CompressedPayload, payload).SetProperty(stream => stream.PointCount, 200_001), preparationDeadline.Token);
            await db.ActivityStreams.Where(stream => stream.OwnerId == seed.SecondProgressOwner).ExecuteUpdateAsync(setters => setters
                .SetProperty(stream => stream.CompressedPayload, quickPayload).SetProperty(stream => stream.PointCount, 20), preparationDeadline.Token);
        }

        await page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true }).SelectOptionAsync("all-time");
        await page.GetByRole(AriaRole.Button, new() { Name = "Apply period", Exact = true }).ClickAsync();
        var cancel = page.GetByRole(AriaRole.Button, new() { Name = "Cancel calculation", Exact = true });
        await Assertions.Expect(cancel).ToBeVisibleAsync();
        await cancel.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Calculation cancelled", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".benchmark-progress-chart")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".history-table")).ToHaveCountAsync(0);
        await Assertions.Expect(cancel).ToHaveCountAsync(0);

        await page.GetByRole(AriaRole.Button, new() { Name = "Try again", Exact = true }).ClickAsync();
        await Assertions.Expect(cancel).ToBeVisibleAsync();
        await page.GetByLabel("Profile selector").EvaluateAsync("""
            (select, owners) => {
                for (const owner of owners) {
                    select.value = owner;
                    select.dispatchEvent(new Event('change', { bubbles: true }));
                }
            }
            """, new[] { seed.EmptyOwner.ToString(), seed.ProgressOwner.ToString(), seed.SecondProgressOwner.ToString() });
        await Assertions.Expect(page.GetByLabel("Profile selector")).ToHaveValueAsync(seed.SecondProgressOwner.ToString());
        await Assertions.Expect(page.Locator(".benchmark-owner-series")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".benchmark-owner-series")).ToHaveAttributeAsync("data-owner", seed.SecondProgressOwner.ToString());
        await Assertions.Expect(page.Locator(".benchmark-progress-chart .chart-sample")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".history-table tbody tr")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".history-table tbody tr")).ToContainTextAsync("Second owner progress ride");
        await Assertions.Expect(cancel).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
    }

    private static async Task AssertProgressCalendarAsync(IPage page, string origin, ReportingBrowserSeed seed)
    {
        await page.GotoAsync(origin + "/calendar?month=2026-01&sport=cycling", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Training calendar", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByLabel("Month", new() { Exact = true })).ToHaveValueAsync("2026-01");
        await Assertions.Expect(page.GetByLabel("Calendar month totals")).ToContainTextAsync("32");
        var grid = page.Locator(".calendar-grid");
        await Assertions.Expect(grid).ToBeVisibleAsync();
        await Assertions.Expect(grid.Locator("a[data-date]")).ToHaveCountAsync(31);
        await Assertions.Expect(grid.Locator("[data-date='2026-01-02']")).ToContainTextAsync("31");
        await Assertions.Expect(grid.Locator(".calendar-week-total").First).ToContainTextAsync("32");
        await Assertions.Expect(grid.Locator(".calendar-week-total").First).ToContainTextAsync("Partial week");
        await grid.Locator("a[data-date='2026-01-02']").ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/activities\\?.*from=2026-01-02.*to=2026-01-02.*sport=cycling"));
        await AssertReportingTotalsAsync(page, "31", $"{473d:N1} km", "33:00:00", "380 m");
        await page.GoBackAsync();
        await Assertions.Expect(page.GetByLabel("Month", new() { Exact = true })).ToHaveValueAsync("2026-01");
        await page.GetByRole(AriaRole.Button, new() { Name = "Next month", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Month", new() { Exact = true })).ToHaveValueAsync("2026-02");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Next month", Exact = true })).ToBeFocusedAsync();
        await Assertions.Expect(page.GetByLabel("Calendar month totals")).ToContainTextAsync("0");
        await page.GoBackAsync();
        await Assertions.Expect(page.GetByLabel("Month", new() { Exact = true })).ToHaveValueAsync("2026-01");
        await page.GoForwardAsync();
        await Assertions.Expect(page.GetByLabel("Month", new() { Exact = true })).ToHaveValueAsync("2026-02");
        await page.GetByRole(AriaRole.Button, new() { Name = "Previous month", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Month", new() { Exact = true })).ToHaveValueAsync("2026-01");
        await page.GetByRole(AriaRole.Combobox, new() { Name = "View", Exact = true }).SelectOptionAsync("list");
        await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters", Exact = true }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]view=list(?:&|$)"));
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "View", Exact = true })).ToHaveValueAsync("list");
        await Assertions.Expect(page.Locator(".calendar-list")).ToBeVisibleAsync();
        await AssertProgressResponsiveAsync(page, "calendar-list");
        await AssertProgressEnlargedTextAsync(page, "Calendar list");
        await page.GotoAsync(origin + "/calendar?month=2026-01&sport=cycling", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await AssertProgressResponsiveAsync(page, "calendar");
        await page.GetByLabel("Profile selector").SelectOptionAsync(seed.EmptyOwner.ToString());
        await Assertions.Expect(page.GetByLabel("Month", new() { Exact = true })).ToHaveValueAsync("2026-01");
        await Assertions.Expect(page.GetByLabel("Calendar month totals")).ToContainTextAsync("0");
        await page.GetByLabel("Profile selector").SelectOptionAsync("");
        await Assertions.Expect(page.GetByLabel("Calendar month totals")).ToContainTextAsync("32");
        await page.GotoAsync(origin + "/calendar?month=invalid", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("month");
    }

    private static async Task AssertProgressTotalsAsync(IPage page, params string[] values)
    {
        await Assertions.Expect(page.Locator(".progress-totals .metric-label"))
            .ToHaveTextAsync(["Activities", "Distance", "Moving time", "Ascent", "Active days"]);
        await Assertions.Expect(page.Locator(".progress-totals .metric-card > strong")).ToHaveTextAsync(values);
    }

    private static async Task AssertProgressResponsiveAsync(IPage page, string name)
    {
        var captures = Path.Combine(FindRepositoryRoot(), "artifacts", "progress-ui");
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
    }

    private static async Task AssertProgressEnlargedTextAsync(IPage page, string name)
    {
        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        await page.EvaluateAsync("document.documentElement.style.fontSize='200%'; document.body.style.fontSize='30px'");
        await AssertNoDocumentOverflowAsync(page, $"{name} with enlarged text");
        await page.EvaluateAsync("document.documentElement.style.removeProperty('font-size'); document.body.style.removeProperty('font-size')");
    }

    private static async Task<ProgressBrowserSeed> SeedProgressBrowserDataAsync(string dataRoot)
    {
        var reporting = await SeedReportingBrowserDataAsync(dataRoot);
        await using var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot));
        var owner = new OwnerProfile { DisplayName = "Progress athlete", TimeZoneId = "Europe/Copenhagen" };
        var secondOwner = new OwnerProfile { DisplayName = "Second progress athlete", TimeZoneId = "America/Los_Angeles" };
        db.AddRange(owner, secondOwner);
        Guid lastActivity = default;
        for (var index = 0; index < 61; index++)
        {
            var activity = ReportingBrowserActivity(owner.Id, $"Progress ride {index + 1:00}",
                new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero).AddDays(index),
                (index == 30 ? 20 : index + 1) * 1000, 3600, 10);
            activity.IsIndoor = index == 60;
            db.Add(activity);
            lastActivity = activity.Id;
        }
        db.Add(ReportingBrowserActivity(secondOwner.Id, "Second owner progress ride", new(2026, 3, 2, 12, 0, 0, TimeSpan.Zero), 100000, 3600, 10));
        await db.SaveChangesAsync();
        return new(reporting.EastOwner, reporting.WestOwner, reporting.EmptyOwner, owner.Id, secondOwner.Id, lastActivity);
    }

    private sealed record ProgressBrowserSeed(Guid EastOwner, Guid WestOwner, Guid EmptyOwner, Guid ProgressOwner, Guid SecondProgressOwner, Guid LastActivity);
}
