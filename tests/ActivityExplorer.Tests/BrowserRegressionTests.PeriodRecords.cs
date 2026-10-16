using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    [Fact]
    public async Task Period_records_preview_fixture_is_created_only_when_explicitly_requested()
    {
        var requestedPath = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_PERIOD_RECORDS_PREVIEW_DATA");
        if (string.IsNullOrWhiteSpace(requestedPath)) return;
        var path = Path.GetFullPath(requestedPath);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException("The period-record preview fixture requires an empty directory.");
        Directory.CreateDirectory(path);
        var seed = await SeedPeriodRecordsBrowserDataAsync(path);
        await File.WriteAllTextAsync(Path.Combine(path, "synthetic-fixture.json"), JsonSerializer.Serialize(seed));
        TestContext.Current.TestOutputHelper!.WriteLine($"Synthetic period-record preview data: {path}");
    }

    [Fact]
    public async Task Period_records_calculate_period_winners_and_keep_attempts_scope_and_local_dates()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedPeriodRecordsBrowserDataAsync(dataRoot);
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
            await page.GotoAsync(origin + "/records?sport=cycling&scope=outdoor", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.Owner.ToString());
            await AssertPeriodRecordActivityAsync(page, RecordKind.Distance, "Longest distance", seed.LifetimeWinner);
            await AssertPeriodRecordActivityAsync(page, RecordKind.PowerCurve, "5 s", seed.LifetimeWinner);

            await SetRecordBrowserPeriodAsync(page, "2026-01-02", "2026-01-05");
            var periodUrl = page.Url;
            Assert.Contains("period=custom", periodUrl, StringComparison.Ordinal);
            Assert.Contains("from=2026-01-02", periodUrl, StringComparison.Ordinal);
            Assert.Contains("to=2026-01-05", periodUrl, StringComparison.Ordinal);
            foreach (var (kind, key) in PeriodBrowserBenchmarks)
                await AssertPeriodRecordActivityAsync(page, kind, key, seed.PeriodWinner);
            await AssertPeriodRecordActivityAsync(page, RecordKind.Duration, "Longest moving time", seed.MissingStream);
            await Assertions.Expect(PeriodBrowserRecordRow(page, RecordKind.PowerCurve, "5 s").Locator("td[data-label='Result']"))
                .ToHaveTextAsync($"{300:N0} W");
            await Assertions.Expect(PeriodBrowserRecordRow(page, RecordKind.Distance, "Longest distance").Locator("td[data-label='Date']"))
                .ToHaveTextAsync(new DateTime(2026, 1, 2).ToString("d", System.Globalization.CultureInfo.CurrentCulture));

            // Every category opens the same selected period, including its tied second-best activity.
            foreach (var (kind, key) in PeriodBrowserBenchmarks)
            {
                await PeriodBrowserRecordRow(page, kind, key).Locator(".benchmark-link").ClickAsync();
                await Assertions.Expect(page.Locator(".attempt-table tbody tr")).ToHaveCountAsync(kind == RecordKind.Distance ? 4 : 3);
                await Assertions.Expect(page.Locator(".attempt-table tbody tr").First).ToContainTextAsync("Period boundary winner");
                await Assertions.Expect(page.Locator(".attempt-table tbody tr").Nth(1)).ToContainTextAsync("Period tied later ride");
                await Assertions.Expect(page.Locator(".attempt-table")).Not.ToContainTextAsync("Lifetime outside-period winner");
                await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Back to Records", Exact = true }))
                    .ToHaveAttributeAsync("href", periodUrl[origin.Length..]);
                await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true })).ToHaveValueAsync("custom");
                await page.GetByRole(AriaRole.Link, new() { Name = "Back to Records", Exact = true }).ClickAsync();
                await AssertPeriodRecordActivityAsync(page, kind, key, seed.PeriodWinner);
            }

            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByLabel("From", new() { Exact = true })).ToHaveValueAsync("2026-01-02");
            await Assertions.Expect(page.GetByLabel("To", new() { Exact = true })).ToHaveValueAsync("2026-01-05");
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.Owner.ToString());
            await AssertPeriodRecordActivityAsync(page, RecordKind.PowerCurve, "5 s", seed.PeriodWinner);
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Record scope" }).SelectOptionAsync("indoor");
            await AssertPeriodRecordActivityAsync(page, RecordKind.Distance, "Longest distance", seed.IndoorWinner);
            await AssertPeriodRecordActivityAsync(page, RecordKind.TimedDistanceEffort, "5 min", seed.IndoorWinner);
            await AssertPeriodRecordActivityAsync(page, RecordKind.PowerCurve, "5 s", seed.IndoorWinner);
            await Assertions.Expect(PeriodBrowserRecordRow(page, RecordKind.DistanceEffort, "5 km")).ToHaveCountAsync(0);
            Assert.Contains("period=custom", page.Url, StringComparison.Ordinal);
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Record scope" }).SelectOptionAsync("outdoor");
            await AssertPeriodRecordActivityAsync(page, RecordKind.PowerCurve, "5 s", seed.PeriodWinner);
            await page.GoBackAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Record scope" })).ToHaveValueAsync("indoor");
            await AssertPeriodRecordActivityAsync(page, RecordKind.PowerCurve, "5 s", seed.IndoorWinner);
            await page.GetByRole(AriaRole.Link, new() { Name = "Rowing", Exact = true }).ClickAsync();
            await AssertPeriodRecordActivityAsync(page, RecordKind.DistanceEffort, "100 m", seed.RowingWinner);
            await AssertPeriodRecordActivityAsync(page, RecordKind.TimedDistanceEffort, "1 min", seed.RowingWinner);
            await Assertions.Expect(page.Locator(".records-panel")).Not.ToContainTextAsync("Most elevation gain");
            Assert.Contains("period=custom", page.Url, StringComparison.Ordinal);
            await AssertGoalsResponsiveAsync(page, "period-records");

            // The same UTC start belongs to Jan 2 in Copenhagen and Jan 1 in Los Angeles.
            await page.GotoAsync(origin + "/records?sport=cycling&scope=outdoor&period=custom&from=2026-01-02&to=2026-01-02",
                new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(PeriodBrowserRecordRow(page, RecordKind.Distance, "Longest distance")).ToHaveCountAsync(2);
            Assert.Equal(new[] { seed.PeriodWinner, seed.SecondOwnerPeriod }.Order(),
                (await PeriodBrowserRecordRow(page, RecordKind.Distance, "Longest distance").Locator(".activity-link")
                    .EvaluateAllAsync<string[]>("links => links.map(link => link.getAttribute('href').split('/').pop())")).Select(Guid.Parse).Order());
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.SecondOwner.ToString());
            await AssertPeriodRecordActivityAsync(page, RecordKind.Distance, "Longest distance", seed.SecondOwnerPeriod);
            await SetRecordBrowserPeriodAsync(page, "2026-01-01", "2026-01-01");
            await AssertPeriodRecordActivityAsync(page, RecordKind.Distance, "Longest distance", seed.SecondOwnerBoundary);
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.Owner.ToString());
            await Assertions.Expect(page.Locator(".empty-state")).ToContainTextAsync("No Cycling records");
            await Assertions.Expect(page.Locator(".records-table")).ToHaveCountAsync(0);
            await page.GetByRole(AriaRole.Link, new() { Name = "View all time", Exact = true }).ClickAsync();
            await AssertPeriodRecordActivityAsync(page, RecordKind.Distance, "Longest distance", seed.LifetimeWinner);
            Assert.DoesNotContain("period=", page.Url, StringComparison.Ordinal);
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
    public async Task Period_records_refresh_relative_ranges_recover_errors_and_cancel_without_stale_results()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedPeriodRecordsBrowserDataAsync(dataRoot);
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
            await page.GotoAsync(origin + "/records?sport=cycling&scope=outdoor&period=today", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.Owner.ToString());
            await AssertPeriodRecordActivityAsync(page, RecordKind.Distance, "Longest distance", seed.TodayActivity);
            var today = PeriodBrowserActivity(seed.Owner, "Newly imported today record", SportKind.Cycling,
                seed.TodayStart.AddHours(1), 3, 110);
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                db.Add(today);
                await db.SaveChangesAsync();
            }
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh records", Exact = true }).ClickAsync();
            await AssertPeriodRecordActivityAsync(page, RecordKind.Distance, "Longest distance", today.Id);
            Assert.Contains("period=today", page.Url, StringComparison.Ordinal);
            Assert.DoesNotContain("from=", page.Url, StringComparison.Ordinal);
            Assert.DoesNotContain("to=", page.Url, StringComparison.Ordinal);
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await AssertPeriodRecordActivityAsync(page, RecordKind.Distance, "Longest distance", today.Id);
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.Owner.ToString());

            // Changing profile restores the applied URL period instead of applying an unsubmitted draft.
            var draft = page.GetByRole(AriaRole.Form, new() { Name = "Record period", Exact = true });
            await draft.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true }).SelectOptionAsync("custom");
            await draft.GetByLabel("From", new() { Exact = true }).FillAsync("2026-01-02");
            await draft.GetByLabel("To", new() { Exact = true }).FillAsync("2026-01-05");
            await draft.GetByLabel("To", new() { Exact = true }).PressAsync("Tab");
            await Assertions.Expect(page.Locator(".records-table")).ToHaveCountAsync(0);
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.SecondOwner.ToString());
            await Assertions.Expect(draft.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true })).ToHaveValueAsync("today");
            Assert.Contains("period=today", page.Url, StringComparison.Ordinal);
            Assert.DoesNotContain("from=", page.Url, StringComparison.Ordinal);
            await Assertions.Expect(page.Locator(".empty-state")).ToContainTextAsync("No Cycling records");
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.Owner.ToString());
            await AssertPeriodRecordActivityAsync(page, RecordKind.Distance, "Longest distance", today.Id);

            await page.GotoAsync(origin + "/records?sport=cycling&scope=outdoor&period=unknown", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Alert).First).ToContainTextAsync("valid reporting period");
            await Assertions.Expect(page.Locator(".records-table")).ToHaveCountAsync(0);
            var form = page.GetByRole(AriaRole.Form, new() { Name = "Record period", Exact = true });
            await form.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true }).SelectOptionAsync("custom");
            await form.GetByLabel("From", new() { Exact = true }).FillAsync("2026-01-05");
            await form.GetByLabel("To", new() { Exact = true }).FillAsync("2026-01-02");
            await form.GetByLabel("To", new() { Exact = true }).PressAsync("Tab");
            await Assertions.Expect(page.GetByRole(AriaRole.Alert).First).ToContainTextAsync("From date cannot be after");
            await Assertions.Expect(form.GetByRole(AriaRole.Button, new() { Name = "Apply record period", Exact = true })).ToBeDisabledAsync();
            await Assertions.Expect(page.Locator(".records-table")).ToHaveCountAsync(0);
            await SetRecordBrowserPeriodAsync(page, "2026-01-02", "2026-01-05");
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.Owner.ToString());
            await AssertPeriodRecordActivityAsync(page, RecordKind.PowerCurve, "5 s", seed.PeriodWinner);

            byte[] originalPayload;
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
            {
                var stream = await db.ActivityStreams.SingleAsync(item => item.ActivityId == seed.PeriodWinner);
                originalPayload = stream.CompressedPayload;
                stream.CompressedPayload = [0, 1, 2];
                await db.SaveChangesAsync();
            }
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh records", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Records could not be loaded");
            await Assertions.Expect(page.Locator(".records-table")).ToHaveCountAsync(0);
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
                await db.ActivityStreams.Where(item => item.ActivityId == seed.PeriodWinner)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CompressedPayload, originalPayload));
            await page.GetByRole(AriaRole.Button, new() { Name = "Try again", Exact = true }).ClickAsync();
            await AssertPeriodRecordActivityAsync(page, RecordKind.PowerCurve, "5 s", seed.PeriodWinner);
            await SetRecordBrowserPeriodAsync(page, "2050-01-01", "2050-01-31");
            await Assertions.Expect(page.Locator(".empty-state")).ToContainTextAsync("No Cycling records");
            await Assertions.Expect(page.Locator(".records-table")).ToHaveCountAsync(0);
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.EmptyOwner.ToString());
            await Assertions.Expect(page.Locator(".records-table")).ToHaveCountAsync(0);

            await AssertPeriodOverviewCancellationAsync(page, origin, dataRoot, seed);
            Assert.Empty(errors);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    private static readonly (RecordKind Kind, string Key)[] PeriodBrowserBenchmarks =
    [
        (RecordKind.Distance, "Longest distance"), (RecordKind.DistanceEffort, "5 km"),
        (RecordKind.TimedDistanceEffort, "5 min"), (RecordKind.PowerCurve, "5 s")
    ];

    private static ILocator PeriodBrowserRecordRow(IPage page, RecordKind kind, string key) => page.Locator(".records-table tbody tr")
        .Filter(new() { Has = page.Locator($".benchmark-link[href*='kind={kind.ToString().ToLowerInvariant()}&key={Uri.EscapeDataString(key)}']") });

    private static async Task AssertPeriodRecordActivityAsync(IPage page, RecordKind kind, string key, Guid activity)
    {
        var row = PeriodBrowserRecordRow(page, kind, key);
        await Assertions.Expect(row).ToHaveCountAsync(1);
        await Assertions.Expect(row.Locator(".activity-link")).ToHaveAttributeAsync("href", $"/activities/{activity}");
    }

    private static async Task SetRecordBrowserPeriodAsync(IPage page, string from, string to)
    {
        var form = page.GetByRole(AriaRole.Form, new() { Name = "Record period", Exact = true });
        await form.GetByRole(AriaRole.Combobox, new() { Name = "Period", Exact = true }).SelectOptionAsync("custom");
        await form.GetByLabel("From", new() { Exact = true }).FillAsync(from);
        await form.GetByLabel("To", new() { Exact = true }).FillAsync(to);
        await form.GetByLabel("To", new() { Exact = true }).PressAsync("Tab");
        await form.GetByRole(AriaRole.Button, new() { Name = "Apply record period", Exact = true }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new Regex($"[?&]from={from}&to={to}(?:&|$)"));
    }

    private static async Task AssertPeriodOverviewCancellationAsync(IPage page, string origin, string dataRoot, PeriodRecordsBrowserSeed seed)
    {
        // Large streams belong only to this cancellation scenario; ordinary and preview fixtures stay small.
        var start = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        byte[] payload;
        using (var buffer = new MemoryStream())
        {
            using (var compressed = new BrotliStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
                JsonSerializer.Serialize(compressed, Enumerable.Range(0, 200_001).Select(index => new TrackPoint(
                    start.AddSeconds(index), null, null, index * 10d, null, null, null, null, 200 + index % 19, null)),
                    JsonSerializerOptions.Web);
            payload = buffer.ToArray();
        }
        var quick = PeriodBrowserActivity(seed.SecondOwner, "Quick June second-owner record", SportKind.Cycling,
            start.AddDays(1), 10, 250, pointCount: 20, indoor: true);
        await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
        {
            for (var index = 0; index < 12; index++)
            {
                var activity = PeriodBrowserActivity(seed.Owner, $"Cancellation stress ride {index + 1:00}", SportKind.Cycling,
                    start, 10, 200, pointCount: 2, indoor: true);
                activity.Stream!.CompressedPayload = payload;
                activity.Stream.PointCount = 200_001;
                activity.DistanceMeters = 2_000_000;
                activity.MovingTimeSeconds = activity.ElapsedTimeSeconds = 200_000;
                db.Add(activity);
            }
            db.Add(quick);
            await db.SaveChangesAsync();
        }
        await page.GotoAsync(origin + "/records?sport=cycling&period=custom&from=2050-01-01&to=2050-01-31",
            new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.Owner.ToString());
        await SetRecordBrowserPeriodAsync(page, "2026-06-01", "2026-06-30");
        var cancel = page.GetByRole(AriaRole.Button, new() { Name = "Cancel calculation", Exact = true });
        await Assertions.Expect(cancel).ToBeVisibleAsync();
        await cancel.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Calculation cancelled", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".records-table")).ToHaveCountAsync(0);
        await Assertions.Expect(cancel).ToHaveCountAsync(0);
        await page.GetByRole(AriaRole.Button, new() { Name = "Try again", Exact = true }).ClickAsync();
        await Assertions.Expect(cancel).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).EvaluateAsync("""
            (select, owners) => {
                for (const owner of owners) {
                    select.value = owner;
                    select.dispatchEvent(new Event('change', { bubbles: true }));
                }
            }
            """, new[] { seed.EmptyOwner.ToString(), seed.Owner.ToString(), seed.SecondOwner.ToString() });
        await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" })).ToHaveValueAsync(seed.SecondOwner.ToString());
        await AssertPeriodRecordActivityAsync(page, RecordKind.Distance, "Longest distance", quick.Id);
        await AssertPeriodRecordActivityAsync(page, RecordKind.PowerCurve, "5 s", quick.Id);
        await Assertions.Expect(page.Locator(".records-table").Filter(new() { HasText = "Cancellation stress ride" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
    }

    private static async Task<PeriodRecordsBrowserSeed> SeedPeriodRecordsBrowserDataAsync(string dataRoot)
    {
        var owner = new OwnerProfile { DisplayName = "Period East", TimeZoneId = "Europe/Copenhagen" };
        var secondOwner = new OwnerProfile { DisplayName = "Period West", TimeZoneId = "America/Los_Angeles" };
        var emptyOwner = new OwnerProfile { DisplayName = "Empty period athlete", TimeZoneId = "UTC" };
        var boundary = new DateTimeOffset(2026, 1, 1, 23, 30, 0, TimeSpan.Zero);
        var lifetime = PeriodBrowserActivity(owner.Id, "Lifetime outside-period winner", SportKind.Cycling, boundary.AddMonths(-1), 20, 500);
        var winner = PeriodBrowserActivity(owner.Id, "Period boundary winner", SportKind.Cycling, boundary, 10, 300);
        var tie = PeriodBrowserActivity(owner.Id, "Period tied later ride", SportKind.Cycling, boundary.AddDays(1), 10, 300);
        var slower = PeriodBrowserActivity(owner.Id, "Period slower ride", SportKind.Cycling, boundary.AddDays(3), 6, 180);
        var indoor = PeriodBrowserActivity(owner.Id, "Period indoor recorded-distance ride", SportKind.Cycling, boundary.AddHours(14), 15, 400, indoor: true);
        var missing = ReportingBrowserActivity(owner.Id, "Period summary without a stream", boundary.AddDays(2), 7000, 1200, 50);
        missing.Stream = null;
        missing.HasPower = false;
        var rowing = PeriodBrowserActivity(owner.Id, "Period rowing recorded-distance winner", SportKind.Rowing, boundary.AddDays(2), 4, 240, indoor: true);
        var westBoundary = PeriodBrowserActivity(secondOwner.Id, "West Jan 1 boundary record", SportKind.Cycling, boundary, 30, 600);
        var westInside = PeriodBrowserActivity(secondOwner.Id, "West Jan 2 period record", SportKind.Cycling, boundary.AddHours(14), 8, 250);
        var todayDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById(owner.TimeZoneId)).DateTime);
        var todayStart = new DateTimeOffset(todayDate.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
        var today = PeriodBrowserActivity(owner.Id, "Today period record", SportKind.Cycling, todayStart, 2, 90);
        await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
        {
            await db.Database.EnsureCreatedAsync();
            db.AddRange(owner, secondOwner, emptyOwner, lifetime, winner, tie, slower, indoor, missing, rowing, westBoundary, westInside, today);
            await db.SaveChangesAsync();
        }
        var statistics = new StatisticsService(new AttemptBrowserDbFactory(ReportingBrowserOptions(dataRoot)));
        await statistics.RecomputeAsync(owner.Id);
        await statistics.RecomputeAsync(secondOwner.Id);
        return new(owner.Id, secondOwner.Id, emptyOwner.Id, lifetime.Id, winner.Id, tie.Id, indoor.Id, missing.Id,
            rowing.Id, westBoundary.Id, westInside.Id, today.Id, todayStart,
            "/records?sport=cycling&scope=outdoor&period=custom&from=2026-01-02&to=2026-01-05");
    }

    private static ActivityExplorer.Core.Domain.Activity PeriodBrowserActivity(Guid ownerId, string title, SportKind sport,
        DateTimeOffset start, double metersPerSecond, double power, int pointCount = 1001, bool indoor = false)
    {
        var points = Enumerable.Range(0, pointCount).Select(index => new TrackPoint(start.AddSeconds(index),
            indoor ? null : 0, indoor ? null : index * metersPerSecond * 0.000009,
            index * metersPerSecond, 10, metersPerSecond, 130, 80, power, 15)).ToArray();
        var activity = BrowserActivity(ownerId, title, sport, points);
        activity.IsIndoor = indoor;
        activity.DistanceMeters = (pointCount - 1) * metersPerSecond;
        activity.MovingTimeSeconds = activity.ElapsedTimeSeconds = pointCount - 1;
        activity.AverageSpeedMetersPerSecond = metersPerSecond;
        activity.ElevationGainMeters = sport == SportKind.Rowing ? 0 : 100;
        activity.AveragePowerWatts = activity.MaxPowerWatts = power;
        return activity;
    }

    private sealed record PeriodRecordsBrowserSeed(Guid Owner, Guid SecondOwner, Guid EmptyOwner, Guid LifetimeWinner,
        Guid PeriodWinner, Guid LaterTie, Guid IndoorWinner, Guid MissingStream, Guid RowingWinner,
        Guid SecondOwnerBoundary, Guid SecondOwnerPeriod, Guid TodayActivity, DateTimeOffset TodayStart, string PeriodUrl);
}
