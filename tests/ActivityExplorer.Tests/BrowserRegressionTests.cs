using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed class BrowserRegressionTests
{
    private static readonly string[] MapEndpoints = ["activities", "routes", "segments"];

    [Fact]
    public async Task Blank_maps_mobile_imports_and_navigation_are_browser_safe()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS"),
                "1",
                StringComparison.Ordinal))
            return;

        var root = FindRepositoryRoot();
        var webAssembly = FindWebAssembly(root);
        var dataRoot = TestSupport.NewDirectory();
        var port = ReservePort();
        var origin = $"http://127.0.0.1:{port}";
        var output = new StringBuilder();
        using var process = StartApplication(webAssembly, dataRoot, origin, output);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 360, Height = 800 }
            });
            var page = await context.NewPageAsync();
            var externalRequests = new List<string>();
            var mapResponses = new List<(string Url, int Status)>();
            page.Request += (sender, request) =>
            {
                if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && !IPAddress.TryParse(uri.Host, out var address)
                    && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                    externalRequests.Add(request.Url);
            };
            page.Response += (_, response) =>
            {
                if (response.Url.Contains("/internal/map/", StringComparison.Ordinal))
                    mapResponses.Add((response.Url, response.Status));
            };

            await page.SetViewportSizeAsync(3000, 900);
            await page.GotoAsync(origin + "/map", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            var responseDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < responseDeadline && MapEndpoints.Any(endpoint =>
                       !mapResponses.Any(response => response.Url.Contains($"/internal/map/{endpoint}", StringComparison.Ordinal))))
                await page.WaitForTimeoutAsync(100);
            Assert.Empty(externalRequests);
            Assert.Equal(0, await page.Locator("text=OpenFreeMap receives").CountAsync());
            Assert.DoesNotContain(mapResponses, response => response.Status >= 400);
            foreach (var endpoint in MapEndpoints)
                Assert.Contains(mapResponses, response => response.Url.Contains($"/internal/map/{endpoint}", StringComparison.Ordinal) && response.Status == 200);
            Assert.DoesNotContain("BadHttpRequestException", output.ToString(), StringComparison.Ordinal);

            foreach (var width in new[] { 320, 360, 375, 768, 1121, 1280, 1920 })
            {
                await AssertNoHorizontalOverflowAsync(page, origin + "/activities", width);
                await AssertNoHorizontalOverflowAsync(page, origin + "/imports", width);
                await AssertNoHorizontalOverflowAsync(page, origin + "/records", width);
                await AssertNoHorizontalOverflowAsync(page, origin + "/routes", width);
            }

            await page.SetViewportSizeAsync(1280, 900);
            await page.GotoAsync(origin + "/records?scope=outdoor", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator("#record-scope")).ToHaveValueAsync("outdoor");
            Assert.Equal(1, await page.Locator("link[href='ActivityExplorer.Web.styles.css']").CountAsync());
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator("#record-scope")).ToHaveValueAsync("outdoor");
            await page.GotoAsync(origin + "/records?scope=invalid", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page).ToHaveURLAsync(origin + "/records");
            await Assertions.Expect(page.Locator("#record-scope")).ToHaveValueAsync("all");

            await page.GotoAsync(origin + "/imports", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            var importHistory = page.Locator("details.import-history");
            await Assertions.Expect(importHistory).Not.ToHaveAttributeAsync("open", "");
            await importHistory.Locator("summary").ClickAsync();
            await Assertions.Expect(importHistory).ToHaveAttributeAsync("open", "");
            await importHistory.Locator("summary").ClickAsync();
            await Assertions.Expect(importHistory).Not.ToHaveAttributeAsync("open", "");
            await Assertions.Expect(page.GetByText("DI_Connect-fitness-Uploaded-Files", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("DI-Connect-Uploaded-Files", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();

            await page.SetViewportSizeAsync(360, 800);
            var menu = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Toggle navigation" });
            await menu.FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            var closedMenuTookFocus = await page.EvaluateAsync<bool>("() => document.activeElement?.closest('.sidebar') !== null");
            Assert.False(closedMenuTookFocus, "Closed mobile navigation must not retain off-screen keyboard focus.");
            await menu.FocusAsync();

            await page.WaitForTimeoutAsync(500);
            await menu.ClickAsync();
            await Assertions.Expect(menu).ToHaveAttributeAsync("aria-expanded", "true");
            var close = page.Locator(".nav-close");
            await close.ClickAsync();
            await Assertions.Expect(menu).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(menu).ToHaveAttributeAsync("aria-expanded", "false");
            await menu.ClickAsync();
            await Assertions.Expect(menu).ToHaveAttributeAsync("aria-expanded", "true");
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(menu).ToBeFocusedAsync();



            await page.GotoAsync(origin + "/activities?q=morning&sport=cycling&from=2026-01-02&power=yes&device=edge&sort=distance-desc&page=4", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByPlaceholder("Title or notes")).ToHaveValueAsync("morning");
            await Assertions.Expect(page.GetByLabel("Sport")).ToHaveValueAsync("Cycling");
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByPlaceholder("Title or notes")).ToHaveValueAsync("morning");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Reset", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(origin + "/activities");

            await page.GotoAsync(origin + "/activities?sport=invalid&sort=invalid&page=-4", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByLabel("Sport")).ToHaveValueAsync("");
            await Assertions.Expect(page.GetByLabel("Sort")).ToHaveValueAsync("start-desc");

            var missingId = Guid.NewGuid();
            await page.GotoAsync(origin + $"/activities/{missingId}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Activity not found" })).ToBeVisibleAsync();

            await page.GotoAsync(origin + "/profiles", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.WaitForTimeoutAsync(500);
            var profileName = page.GetByPlaceholder("e.g. Peter");
            await profileName.FillAsync("Browser athlete");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create profile" }).ClickAsync();
            await Assertions.Expect(page.GetByText("Profile created.", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
            await page.GotoAsync(origin + "/segments", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Create from a file" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Import as local segment" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("Provider IDs, leaderboards, popularity, and other-athlete data are discarded.", new PageGetByTextOptions { Exact = false })).ToBeVisibleAsync();
            foreach (var width in new[] { 375, 768, 1280, 1920 })
                await AssertNoHorizontalOverflowAsync(page, origin + "/segments", width);

            await page.GotoAsync(origin + "/profiles", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Delete", Exact = true }).ClickAsync();
            var permanentDelete = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Permanently delete" });
            await Assertions.Expect(permanentDelete).ToBeDisabledAsync();
            await page.GetByLabel("Delete confirmation").FillAsync("DELETE Browser athlete");
            await Assertions.Expect(permanentDelete).ToBeEnabledAsync();

            await page.EmulateMediaAsync(new PageEmulateMediaOptions { ReducedMotion = ReducedMotion.Reduce });
            var transitionDuration = await page.Locator(".entity-card").First.EvaluateAsync<string>("element => getComputedStyle(element).transitionDuration");
            Assert.True(transitionDuration is "0.01ms" or "1e-05s" or "0s", $"Unexpected reduced-motion transition: {transitionDuration}");
            Assert.DoesNotContain("Microsoft.EntityFrameworkCore.Query[10102]", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    [Fact]
    public async Task Route_segment_creator_links_distance_elevation_map_direction_and_creation()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS"),
                "1",
                StringComparison.Ordinal))
            return;

        var root = FindRepositoryRoot();
        var webAssembly = FindWebAssembly(root);
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedRouteCreatorDataAsync(dataRoot);
        var port = ReservePort();
        var origin = $"http://127.0.0.1:{port}";
        var output = new StringBuilder();
        using var process = StartApplication(webAssembly, dataRoot, origin, output);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 1000 }
            });
            var page = await context.NewPageAsync();
            var browserErrors = new List<string>();
            var externalRequests = new List<string>();
            page.PageError += (_, error) => browserErrors.Add(error);
            page.Console += (_, message) =>
            {
                if (message.Type == "error") browserErrors.Add(message.Text);
            };
            page.Request += (_, request) =>
            {
                if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && !IPAddress.TryParse(uri.Host, out IPAddress? parsedAddress)
                    && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                    externalRequests.Add(request.Url);
            };

            var routeUrl = origin + $"/routes/{seed.RouteId}";
            await page.GotoAsync(routeUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Create a segment from this route" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Slider, new PageGetByRoleOptions { Name = "Segment start position" })).ToHaveAttributeAsync("aria-valuetext", new Regex("0 m.+40 m", RegexOptions.IgnoreCase));
            await Assertions.Expect(page.GetByRole(AriaRole.Slider, new PageGetByRoleOptions { Name = "Segment end position" })).ToHaveAttributeAsync("aria-valuetext", new Regex("km.+m", RegexOptions.IgnoreCase));
            await Assertions.Expect(page.Locator(".segment-profile-chart")).ToHaveAttributeAsync("aria-label", new Regex("Elevation profile", RegexOptions.IgnoreCase));
            await Assertions.Expect(page.GetByLabel("Name", new PageGetByLabelOptions { Exact = true })).ToHaveValueAsync("Browser elevation route");
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);

            foreach (var width in new[] { 375, 768, 1121, 1280, 1920 })
                await AssertNoHorizontalOverflowAsync(page, routeUrl, width);

            await page.SetViewportSizeAsync(1280, 1000);
            await page.GotoAsync(routeUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            var initialDistance = await page.Locator(".segment-range-summary").InnerTextAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Move segment start forward" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Move segment end backward" }).ClickAsync();
            await Assertions.Expect(page.Locator(".segment-range-summary")).Not.ToHaveTextAsync(initialDistance);

            var stats = page.Locator(".segment-selection-stats");
            var beforeReverse = await stats.InnerTextAsync();
            var ascent = stats.Locator("dd").Nth(1);
            var ascentBeforeReverse = await ascent.InnerTextAsync();
            await page.GetByLabel("Reverse direction after trimming").CheckAsync();
            await Assertions.Expect(ascent).Not.ToHaveTextAsync(ascentBeforeReverse);
            var afterReverse = await stats.InnerTextAsync();
            Assert.NotEqual(beforeReverse, afterReverse);

            await page.GetByLabel("Name").FillAsync("Browser route segment");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create segment", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"^{Regex.Escape(origin)}/segments/[0-9a-f-]+$", RegexOptions.IgnoreCase));

            var definitionProfile = page.Locator(".segment-grade-profile");
            var definitionSvg = definitionProfile.Locator(".segment-grade-chart");
            await Assertions.Expect(definitionSvg).ToBeVisibleAsync();
            await Assertions.Expect(definitionProfile).ToHaveAttributeAsync("data-profile-bound", "true");
            await Assertions.Expect(definitionProfile.Locator(".chart-heading small")).ToContainTextAsync("100% coverage");
            await Assertions.Expect(definitionProfile.Locator(".chart-heading small")).ToContainTextAsync("Local grade");
            await Assertions.Expect(definitionProfile.Locator(".chart-empty")).ToHaveCountAsync(0);
            await Assertions.Expect(definitionProfile.Locator(".segment-grade-legend li")).ToHaveCountAsync(6);
            await Assertions.Expect(definitionProfile.Locator(".segment-grade-line")).Not.ToHaveCountAsync(0);
            await Assertions.Expect(definitionSvg).ToHaveAttributeAsync("aria-label", new Regex("Colors identify", RegexOptions.IgnoreCase));
            var definitionSamples = definitionProfile.Locator("[data-profile-sample]");
            await Assertions.Expect(definitionSamples).ToHaveCountAsync(seed.Points.Count - 2);
            var firstDefinitionDistance = double.Parse(
                (await definitionSamples.First.GetAttributeAsync("data-distance"))!,
                CultureInfo.InvariantCulture);
            var lastDefinitionDistance = double.Parse(
                (await definitionSamples.Last.GetAttributeAsync("data-distance"))!,
                CultureInfo.InvariantCulture);
            Assert.Equal(0, firstDefinitionDistance);

            foreach (var width in new[] { 375, 768, 1121, 1280, 1920 })
                await AssertNoHorizontalOverflowAsync(page, page.Url, width);

            await page.SetViewportSizeAsync(1280, 1000);
            await definitionProfile.GetByText("Inspect exact elevation and grade", new LocatorGetByTextOptions { Exact = true }).ClickAsync();
            var definitionInspector = page.GetByRole(AriaRole.Slider,
                new PageGetByRoleOptions { Name = "Segment elevation profile sample", Exact = true });
            var definitionMap = page.Locator(".detail-map");
            await definitionInspector.FocusAsync();
            await Assertions.Expect(definitionMap).ToHaveAttributeAsync("data-inspection-index", "0");
            var inspectionRevision = int.Parse(
                (await definitionMap.GetAttributeAsync("data-inspection-revision"))!,
                CultureInfo.InvariantCulture);
            await page.Keyboard.PressAsync("ArrowRight");
            await Assertions.Expect(definitionInspector).ToHaveAttributeAsync("aria-valuetext", new Regex("local grade.+%", RegexOptions.IgnoreCase));
            await Assertions.Expect(definitionMap).ToHaveAttributeAsync("data-inspection-index", "1");
            Assert.Equal(
                inspectionRevision + 1,
                int.Parse(
                    (await definitionMap.GetAttributeAsync("data-inspection-revision"))!,
                    CultureInfo.InvariantCulture));
            await definitionInspector.BlurAsync();
            await Assertions.Expect(definitionMap).Not.ToHaveAttributeAsync("data-inspection-index", new Regex(@"\d+"));

            var plotBounds = await definitionSvg.BoundingBoxAsync();
            Assert.NotNull(plotBounds);
            await page.Mouse.MoveAsync(
                (float)(plotBounds.X + plotBounds.Width * 0.65),
                (float)(plotBounds.Y + plotBounds.Height * 0.5));
            await Assertions.Expect(definitionProfile.Locator(".segment-grade-tooltip")).ToContainTextAsync("local grade");
            await Assertions.Expect(definitionMap).ToHaveAttributeAsync("data-inspection-index", new Regex(@"\d+"));
            await page.Mouse.MoveAsync(
                (float)(plotBounds.X + plotBounds.Width + 25),
                (float)(plotBounds.Y + plotBounds.Height + 25));
            await Assertions.Expect(definitionMap).Not.ToHaveAttributeAsync("data-inspection-index", new Regex(@"\d+"));

            var options = new DbContextOptionsBuilder<ExplorerDbContext>()
                .UseSqlite($"Data Source={Path.Combine(dataRoot, "activity-explorer.db")}")
                .Options;
            await using (var db = new ExplorerDbContext(options))
            {
                var segment = await db.Segments.AsNoTracking().SingleAsync(value => value.Name == "Browser route segment");
                var saved = GeometryCodec.FromWkb(segment.GeometryWkb);
                Assert.Equal(SegmentSourceKind.Route, segment.SourceKind);
                Assert.Equal("Browser elevation route", segment.SourceName);
                Assert.Equal(seed.Points.Count - 2, saved.Count);
                Assert.Equal(seed.Points[^2].Longitude, saved[0].Longitude);
                Assert.InRange(Math.Abs(lastDefinitionDistance - segment.DistanceMeters), 0, 0.001);
            }

            await page.GotoAsync(origin + $"/routes/{seed.MissingElevationRouteId}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByText("Elevation not recorded", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create segment", Exact = true })).ToBeEnabledAsync();

            Assert.Empty(externalRequests);
            Assert.DoesNotContain(browserErrors, error => !IsKnownHeadlessMapLibreError(error));
            Assert.DoesNotContain("Unhandled exception", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    [Fact]
    public async Task Activity_segment_creator_hides_source_indices_and_persists_the_visual_selection()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS"),
                "1",
                StringComparison.Ordinal))
            return;

        var root = FindRepositoryRoot();
        var webAssembly = FindWebAssembly(root);
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedActivityCreatorDataAsync(dataRoot);
        var port = ReservePort();
        var origin = $"http://127.0.0.1:{port}";
        var output = new StringBuilder();
        using var process = StartApplication(webAssembly, dataRoot, origin, output);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 1000 }
            });
            var page = await context.NewPageAsync();
            var browserErrors = new List<string>();
            var externalRequests = new List<string>();
            page.PageError += (_, error) => browserErrors.Add(error);
            page.Console += (_, message) =>
            {
                if (message.Type == "error") browserErrors.Add(message.Text);
            };
            page.Request += (_, request) =>
            {
                if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && !IPAddress.TryParse(uri.Host, out IPAddress? parsedAddress)
                    && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                    externalRequests.Add(request.Url);
            };

            var activityUrl = origin + $"/activities/{seed.ActivityId}";
            var creatorUrl = activityUrl + "/segments/new";
            await page.GotoAsync(activityUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            var createLink = page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Create segment", Exact = true });
            await Assertions.Expect(createLink).ToHaveAttributeAsync("href", $"/activities/{seed.ActivityId}/segments/new");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Create local segment" })).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByLabel("Start point", new PageGetByLabelOptions { Exact = true })).ToHaveCountAsync(0);
            await createLink.ClickAsync();

            await Assertions.Expect(page).ToHaveURLAsync(creatorUrl);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Create a segment", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Create a segment from this activity" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Back to activity", Exact = true })).ToHaveAttributeAsync("href", $"/activities/{seed.ActivityId}");
            await Assertions.Expect(page.GetByLabel("Name", new PageGetByLabelOptions { Exact = true })).ToHaveValueAsync(string.Empty);
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create segment", Exact = true })).ToBeDisabledAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByRole(AriaRole.Slider, new PageGetByRoleOptions { Name = "Segment start position" })).ToHaveAttributeAsync("aria-valuetext", new Regex("0 m.+10 m", RegexOptions.IgnoreCase));
            await Assertions.Expect(page.GetByRole(AriaRole.Region, new PageGetByRoleOptions { Name = "Activity map with selected segment" })).ToBeVisibleAsync();

            foreach (var width in new[] { 375, 768, 1121, 1280, 1920 })
                await AssertNoHorizontalOverflowAsync(page, creatorUrl, width);

            await page.SetViewportSizeAsync(1280, 1000);
            await page.GotoAsync(creatorUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            var startSlider = page.GetByRole(AriaRole.Slider, new PageGetByRoleOptions { Name = "Segment start position" });
            var initialStart = await startSlider.GetAttributeAsync("aria-valuetext");
            await startSlider.FocusAsync();
            await page.Keyboard.PressAsync("ArrowRight");
            await Assertions.Expect(startSlider).Not.ToHaveAttributeAsync("aria-valuetext", initialStart!);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Move segment end backward" }).ClickAsync();

            var stats = page.Locator(".segment-selection-stats");
            var beforeReverse = await stats.InnerTextAsync();
            var ascent = stats.Locator("dd").Nth(1);
            var ascentBeforeReverse = await ascent.InnerTextAsync();
            await page.GetByLabel("Reverse direction after trimming").CheckAsync();
            await Assertions.Expect(ascent).Not.ToHaveTextAsync(ascentBeforeReverse);
            var afterReverse = await stats.InnerTextAsync();
            Assert.NotEqual(beforeReverse, afterReverse);

            await page.GetByLabel("Name", new PageGetByLabelOptions { Exact = true }).FillAsync("Browser activity segment");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create segment", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"^{Regex.Escape(origin)}/segments/[0-9a-f-]+$", RegexOptions.IgnoreCase));

            var options = new DbContextOptionsBuilder<ExplorerDbContext>()
                .UseSqlite($"Data Source={Path.Combine(dataRoot, "activity-explorer.db")}")
                .Options;
            await using (var db = new ExplorerDbContext(options))
            {
                var segment = await db.Segments.AsNoTracking().SingleAsync(value => value.Name == "Browser activity segment");
                var saved = GeometryCodec.FromWkb(segment.GeometryWkb);
                Assert.Equal(SegmentSourceKind.Activity, segment.SourceKind);
                Assert.Equal(seed.ActivityId, segment.SourceActivityId);
                Assert.Equal("Browser activity creator", segment.SourceName);
                Assert.Equal(5, saved.Count);
                Assert.Equal(seed.Points[7].Longitude, saved[0].Longitude);
                Assert.Equal(seed.Points[2].Longitude, saved[^1].Longitude);
            }

            await page.GotoAsync(creatorUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByLabel("Name", new PageGetByLabelOptions { Exact = true }).FillAsync("Browser forward effort segment");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create segment", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"^{Regex.Escape(origin)}/segments/[0-9a-f-]+$", RegexOptions.IgnoreCase));
            var personalBest = page.Locator(".metric-card")
                .Filter(new LocatorFilterOptions { HasTextString = "Personal best" })
                .Locator("strong");
            await Assertions.Expect(personalBest).Not.ToHaveTextAsync("--");
            await Assertions.Expect(page.Locator(".effort-table tbody tr")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".effort-table th").Filter(new LocatorFilterOptions { HasTextString = "Segment avg speed" })).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".effort-table th").Filter(new LocatorFilterOptions { HasTextString = "Ascent" })).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Recorded effort diagnostics" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("Current calculation", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("Difference from saved:", new PageGetByTextOptions { Exact = false }).First).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "open activity", Exact = true }))
                .ToHaveAttributeAsync("href", $"/activities/{seed.ActivityId}");
            await page.Locator(".compact-select select").SelectOptionAsync("speed");
            await Assertions.Expect(page.Locator(".effort-table tbody tr")).ToHaveCountAsync(1);

            var effortCharts = page.Locator(".synchronized-charts");
            await Assertions.Expect(effortCharts).ToHaveAttributeAsync("data-charts-bound", "true");
            await Assertions.Expect(effortCharts.Locator(".time-series-chart[data-unit='km/h'] .chart-heading small"))
                .ToContainTextAsync("Segment elapsed average");
            await Assertions.Expect(effortCharts.Locator(".time-series-chart[data-unit='bpm'] .chart-heading small"))
                .ToContainTextAsync("Time-weighted average");
            var renderedEffortCharts = effortCharts.Locator(".time-series-chart:has(.chart-plot)");
            Assert.Equal(4, await renderedEffortCharts.CountAsync());
            foreach (var chart in await renderedEffortCharts.AllAsync())
            {
                await AssertChartAxesAsync(chart, expectedXAxisTicks: 3);
                await AssertChartSampleMetadataAsync(chart);
            }
            await AssertPointerInspectionAsync(page, effortCharts, expectedCharts: 4);
            await AssertIndependentInspectorAndTableAsync(renderedEffortCharts.First);
            var elapsedAxis = page.GetByRole(AriaRole.Button,
                new PageGetByRoleOptions { Name = "Elapsed time", Exact = true });
            var distanceAxis = page.GetByRole(AriaRole.Button,
                new PageGetByRoleOptions { Name = "Distance", Exact = true });
            await Assertions.Expect(elapsedAxis).ToHaveAttributeAsync("aria-pressed", "true");
            await Assertions.Expect(distanceAxis).ToHaveAttributeAsync("aria-pressed", "false");
            await distanceAxis.ClickAsync();
            await Assertions.Expect(effortCharts).ToHaveAttributeAsync("data-axis-kind", "distance");
            await Assertions.Expect(effortCharts).ToHaveAttributeAsync("data-charts-bound", "true");
            await Assertions.Expect(elapsedAxis).ToHaveAttributeAsync("aria-pressed", "false");
            await Assertions.Expect(distanceAxis).ToHaveAttributeAsync("aria-pressed", "true");
            await Assertions.Expect(effortCharts.Locator(".chart-sample").First).ToHaveAttributeAsync("data-axis", "0");
            await AssertPointerInspectionAsync(page, effortCharts, expectedCharts: 4, expectedAxisSuffix: "m");
            foreach (var width in new[] { 375, 768, 1121, 1280, 1920 })
            {
                await page.SetViewportSizeAsync(width, 1000);
                await AssertNoDocumentOverflowAsync(page, $"Selected-effort charts at {width} CSS pixels");
                foreach (var chart in await effortCharts.Locator(".time-series-chart:has(.chart-plot)").AllAsync())
                    await AssertAxisLabelsFitAsync(chart);
            }
            await page.SetViewportSizeAsync(1280, 1000);

            Guid originalEffortId;
            await using (var db = new ExplorerDbContext(options))
            {
                var segment = await db.Segments.AsNoTracking().SingleAsync(value => value.Name == "Browser forward effort segment");
                var effort = await db.SegmentEfforts.SingleAsync(value => value.SegmentId == segment.Id);
                originalEffortId = effort.Id;
                Assert.Equal(SegmentEffortMetricVersions.Current, effort.MetricComputationVersion);
                effort.MetricComputationVersion = SegmentEffortMetricVersions.Legacy;
                await db.SaveChangesAsync();
            }
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByText("Some efforts use the legacy sample-average calculation.", new PageGetByTextOptions { Exact = false })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("Legacy calculation", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();

            var recompute = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Recompute efforts", Exact = true });
            await recompute.ClickAsync();
            await Assertions.Expect(recompute).ToBeEnabledAsync();
            await Assertions.Expect(page.Locator(".effort-table tbody tr")).ToHaveCountAsync(1);
            await Assertions.Expect(page.GetByText("Efforts recomputed with fixed-distance speed and timestamp-weighted sensor averages.", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("Some efforts use the legacy sample-average calculation.", new PageGetByTextOptions { Exact = false })).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByText("Current calculation", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
            await using (var db = new ExplorerDbContext(options))
            {
                var segment = await db.Segments.AsNoTracking().SingleAsync(value => value.Name == "Browser forward effort segment");
                var effort = await db.SegmentEfforts.AsNoTracking().SingleAsync(value => value.SegmentId == segment.Id);
                Assert.Equal(originalEffortId, effort.Id);
                Assert.Equal(0, effort.StartPointIndex);
                Assert.Equal(8, effort.EndPointIndex);
                Assert.Equal(1, effort.Rank);
                Assert.Equal(100, effort.CoveragePercent, 8);
                Assert.Equal(SegmentEffortMetricVersions.Current, effort.MetricComputationVersion);
            }

            await using (var db = new ExplorerDbContext(options))
            {
                var segment = await db.Segments.SingleAsync(value => value.Name == "Browser forward effort segment");
                segment.GeometryWkb = [0x01, 0x02, 0x03];
                await db.SaveChangesAsync();
            }
            await recompute.ClickAsync();
            await Assertions.Expect(page.GetByText("Efforts could not be recomputed. Existing values were kept; try again.", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
            await using (var db = new ExplorerDbContext(options))
            {
                Assert.Equal(originalEffortId, await db.SegmentEfforts
                    .Where(value => value.Id == originalEffortId)
                    .Select(value => value.Id)
                    .SingleAsync());
            }

            var missingElevationUrl = origin + $"/activities/{seed.MissingElevationActivityId}/segments/new";
            await page.GotoAsync(missingElevationUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByText("Elevation not recorded", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
            await page.GetByLabel("Name", new PageGetByLabelOptions { Exact = true }).FillAsync("No elevation segment");
            var createMissingElevation = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create segment", Exact = true });
            await Assertions.Expect(createMissingElevation).ToBeEnabledAsync();
            await createMissingElevation.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"^{Regex.Escape(origin)}/segments/[0-9a-f-]+$", RegexOptions.IgnoreCase));
            var missingDefinitionProfile = page.Locator(".segment-grade-profile");
            await Assertions.Expect(missingDefinitionProfile.Locator("svg")).ToHaveCountAsync(0);
            await Assertions.Expect(missingDefinitionProfile.Locator(".chart-empty")).ToHaveTextAsync("Not recorded in the source");

            var insufficientActivityUrl = origin + $"/activities/{seed.InsufficientGpsActivityId}";
            await page.GotoAsync(insufficientActivityUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            var singleSampleGroup = page.Locator(".synchronized-charts");
            await Assertions.Expect(singleSampleGroup).ToHaveAttributeAsync("data-charts-bound", "true");
            await Assertions.Expect(singleSampleGroup.Locator(".time-series-chart:has(.chart-plot)")).ToHaveCountAsync(2);
            await Assertions.Expect(singleSampleGroup.Locator(".time-series-chart:has(.chart-empty)")).ToHaveCountAsync(5);
            foreach (var chart in await singleSampleGroup.Locator(".time-series-chart:has(.chart-plot)").AllAsync())
            {
                await Assertions.Expect(chart.Locator(".chart-sample")).ToHaveCountAsync(1);
                await Assertions.Expect(chart.Locator(".chart-single-point")).ToHaveCountAsync(1);
                await Assertions.Expect(chart.Locator(".chart-single-point")).ToBeVisibleAsync();
                await AssertChartAxesAsync(chart, expectedXAxisTicks: 3);
                await AssertChartSampleMetadataAsync(chart);
            }
            var emptyChartsHaveNoAxes = await singleSampleGroup.EvaluateAsync<bool>("""
                group => Array.from(group.querySelectorAll('.time-series-chart:has(.chart-empty)'))
                    .every(chart => !chart.querySelector('.chart-plot, .chart-x-axis, .chart-y-axis, .chart-gridline'))
                """);
            Assert.True(emptyChartsHaveNoAxes, "Zero-sample charts must remain axis-free unavailable states.");
            await AssertIndependentInspectorAndTableAsync(
                singleSampleGroup.Locator(".time-series-chart:has(.chart-plot)").First);
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create segment", Exact = true })).ToBeDisabledAsync();
            await page.GotoAsync(insufficientActivityUrl + "/segments/new", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Segment unavailable" })).ToBeVisibleAsync();

            await page.GotoAsync(origin + $"/activities/{Guid.NewGuid()}/segments/new", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Activity not found" })).ToBeVisibleAsync();

            Assert.Empty(externalRequests);
            Assert.DoesNotContain(browserErrors, error => !IsKnownHeadlessMapLibreError(error));
            Assert.DoesNotContain("Unhandled exception", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    [Fact]
    public async Task Activity_navigation_inspection_deletion_and_card_spacing_are_browser_safe()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS"),
                "1",
                StringComparison.Ordinal))
            return;

        var root = FindRepositoryRoot();
        var webAssembly = FindWebAssembly(root);
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedActivityBrowserDataAsync(dataRoot);
        var port = ReservePort();
        var origin = $"http://127.0.0.1:{port}";
        var output = new StringBuilder();
        using var process = StartApplication(webAssembly, dataRoot, origin, output);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 1000 }
            });
            var page = await context.NewPageAsync();
            var browserErrors = new List<string>();
            page.PageError += (_, error) => browserErrors.Add(error);
            page.Console += (_, message) =>
            {
                if (message.Type == "error") browserErrors.Add(message.Text);
            };

            var detailUrl = origin + $"/activities/{seed.DetailActivityId}";
            await page.GotoAsync(detailUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Map", Exact = true }))
                .ToHaveAttributeAsync("href", $"/activities/{seed.DetailActivityId}#activity-map");
            var detailsJump = page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Details", Exact = true });
            await detailsJump.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(detailUrl + "#activity-details");
            await Assertions.Expect(page.Locator("#activity-details")).ToBeVisibleAsync();

            var exactValues = page.Locator("details.chart-accessible-values").First;
            await exactValues.Locator("summary").First.ClickAsync();
            var sampleSlider = exactValues.GetByRole(AriaRole.Slider);
            await sampleSlider.FocusAsync();
            await page.Keyboard.PressAsync("ArrowRight");
            await Assertions.Expect(exactValues.GetByText("Sample 2 of", new LocatorGetByTextOptions { Exact = false })).ToBeVisibleAsync();
            await Assertions.Expect(sampleSlider).ToHaveAttributeAsync("aria-valuetext", new Regex("elapsed time.+value", RegexOptions.IgnoreCase));

            foreach (var width in new[] { 375, 768, 1280, 1920 })
            {
                await page.SetViewportSizeAsync(width, 1000);
                var gap = await page.EvaluateAsync<double>("""
                    () => {
                        const details = document.querySelector('#activity-details');
                        const next = details?.nextElementSibling;
                        if (!details || !next) return -1;
                        return Math.round(next.getBoundingClientRect().top - details.getBoundingClientRect().bottom);
                    }
                    """);
                Assert.Equal(20, gap);
                var overflow = await page.EvaluateAsync<bool>(
                    "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
                Assert.False(overflow, $"Activity detail overflows horizontally at {width} CSS pixels.");
            }

            await page.SetViewportSizeAsync(1280, 1000);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Delete activity" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Delete Browser detail activity?" })).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Cancel", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Delete Browser detail activity?" })).ToHaveCountAsync(0);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Delete activity" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Permanently delete activity" }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/activities\\?deleted=1"));
            await Assertions.Expect(page.GetByText("Permanently deleted 1 activity.", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();

            var staleCheckbox = page.Locator(".activity-selection-row")
                .Filter(new LocatorFilterOptions { HasTextString = "Browser stale activity" })
                .GetByRole(AriaRole.Checkbox);
            await staleCheckbox.CheckAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Delete selected" }).ClickAsync();
            await DeleteBrowserActivityDirectlyAsync(dataRoot, seed.StaleActivityId);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Permanently delete activity" }).ClickAsync();
            await Assertions.Expect(page.GetByText(new Regex("Nothing was deleted.+no longer exist", RegexOptions.IgnoreCase))).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Cancel", Exact = true }).ClickAsync();
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });

            var selectedCheckbox = page.Locator(".activity-selection-row")
                .Filter(new LocatorFilterOptions { HasTextString = "Browser selected activity" })
                .GetByRole(AriaRole.Checkbox);
            await selectedCheckbox.CheckAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Delete selected" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Permanently delete activity" }).ClickAsync();
            await Assertions.Expect(page.GetByText("Permanently deleted 1 activity.", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();

            await page.GetByPlaceholder("Title or notes").FillAsync("Browser filtered");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Apply filters" }).ClickAsync();
            await Assertions.Expect(page.GetByText("2 activities", new PageGetByTextOptions { Exact = true }).First).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Delete all 2 matching" }).ClickAsync();
            await Assertions.Expect(page.GetByText(new Regex("exact snapshot contains 2 activities", RegexOptions.IgnoreCase))).ToBeVisibleAsync();
            await AddBrowserActivityAsync(dataRoot, seed.OwnerId, "Browser filtered imported later", 10);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Permanently delete 2 activities" }).ClickAsync();
            await Assertions.Expect(page.GetByText("Permanently deleted 2 activities.", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("Browser filtered imported later", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();

            Assert.DoesNotContain(browserErrors, error => !IsKnownHeadlessMapLibreError(error));
            Assert.DoesNotContain("Unhandled exception", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    [Fact]
    public async Task Dashboard_and_activity_charts_expose_axes_exact_values_and_synchronized_pointer_inspection()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS"),
                "1",
                StringComparison.Ordinal))
            return;

        var root = FindRepositoryRoot();
        var webAssembly = FindWebAssembly(root);
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedActivityBrowserDataAsync(dataRoot);
        var port = ReservePort();
        var origin = $"http://127.0.0.1:{port}";
        var output = new StringBuilder();
        using var process = StartApplication(webAssembly, dataRoot, origin, output);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                Locale = "da-DK",
                ViewportSize = new ViewportSize { Width = 1280, Height = 1000 }
            });
            var page = await context.NewPageAsync();
            var browserErrors = new List<string>();
            page.PageError += (_, error) => browserErrors.Add(error);
            page.Console += (_, message) =>
            {
                if (message.Type == "error") browserErrors.Add(message.Text);
            };

            await page.GotoAsync(origin, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            var spark = page.Locator(".spark-chart");
            await Assertions.Expect(spark).ToHaveAttributeAsync("data-charts-bound", "true");
            await AssertChartAxesAsync(spark, expectedXAxisTicks: 5);
            await Assertions.Expect(spark.Locator(".chart-sample")).ToHaveCountAsync(12);
            await AssertChartSampleMetadataAsync(spark);
            await AssertPointerInspectionAsync(page, spark, expectedCharts: 1);
            await AssertIndependentInspectorAndTableAsync(spark);

            foreach (var width in new[] { 375, 768, 1121, 1280, 1920 })
            {
                await page.SetViewportSizeAsync(width, 1000);
                await AssertNoDocumentOverflowAsync(page, $"Dashboard charts at {width} CSS pixels");
                await AssertAxisLabelsFitAsync(spark);
            }

            await page.SetViewportSizeAsync(1280, 1000);
            var detailUrl = origin + $"/activities/{seed.DetailActivityId}";
            await page.GotoAsync(detailUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            var charts = page.Locator(".synchronized-charts");
            await Assertions.Expect(charts).ToHaveAttributeAsync("data-charts-bound", "true");
            var elapsedButton = page.GetByRole(AriaRole.Button,
                new PageGetByRoleOptions { Name = "Elapsed time", Exact = true });
            var distanceButton = page.GetByRole(AriaRole.Button,
                new PageGetByRoleOptions { Name = "Distance", Exact = true });
            await Assertions.Expect(elapsedButton).ToHaveAttributeAsync("aria-pressed", "true");
            await Assertions.Expect(distanceButton).ToHaveAttributeAsync("aria-pressed", "false");

            var renderedCharts = charts.Locator(".time-series-chart:has(.chart-plot)");
            Assert.Equal(6, await renderedCharts.CountAsync());
            await Assertions.Expect(charts.Locator(".time-series-chart:has(.chart-empty)")).ToHaveCountAsync(1);
            foreach (var chart in await renderedCharts.AllAsync())
            {
                await AssertChartAxesAsync(chart, expectedXAxisTicks: 3);
                await AssertChartSampleMetadataAsync(chart);
            }
            await AssertPointerInspectionAsync(page, charts, expectedCharts: 6);
            await AssertIndependentInspectorAndTableAsync(renderedCharts.First);

            await distanceButton.ClickAsync();
            await Assertions.Expect(charts).ToHaveAttributeAsync("data-axis-kind", "distance");
            await Assertions.Expect(charts).ToHaveAttributeAsync("data-charts-bound", "true");
            await Assertions.Expect(elapsedButton).ToHaveAttributeAsync("aria-pressed", "false");
            await Assertions.Expect(distanceButton).ToHaveAttributeAsync("aria-pressed", "true");
            await Assertions.Expect(charts.Locator(".chart-sample").First)
                .ToHaveAttributeAsync("data-axis-label", new Regex("m$", RegexOptions.IgnoreCase));
            Assert.DoesNotContain(
                "km",
                (await charts.Locator(".chart-sample").First.GetAttributeAsync("data-axis-label")) ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            await AssertPointerInspectionAsync(page, charts, expectedCharts: 6, expectedAxisSuffix: "m");

            foreach (var width in new[] { 375, 768, 1121, 1280, 1920 })
            {
                await page.SetViewportSizeAsync(width, 1000);
                await AssertNoDocumentOverflowAsync(page, $"Activity charts at {width} CSS pixels");
                foreach (var chart in await charts.Locator(".time-series-chart:has(.chart-plot)").AllAsync())
                    await AssertAxisLabelsFitAsync(chart);
            }

            Assert.DoesNotContain(browserErrors, error => !IsKnownHeadlessMapLibreError(error));
            Assert.DoesNotContain("Unhandled exception", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }


    private static async Task AssertNoHorizontalOverflowAsync(IPage page, string url, int width)
    {
        await page.SetViewportSizeAsync(width, 800);
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var overflow = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
        Assert.False(overflow, $"The page overflows horizontally at {width} CSS pixels.");
    }

    private static async Task AssertChartAxesAsync(ILocator chart, int expectedXAxisTicks)
    {
        await Assertions.Expect(chart.Locator(".chart-plot")).ToBeVisibleAsync();
        await Assertions.Expect(chart.Locator(".chart-hit-area")).ToHaveCountAsync(1);
        await Assertions.Expect(chart.Locator(".chart-cursor")).ToHaveCountAsync(1);
        await Assertions.Expect(chart.Locator(".chart-marker")).ToHaveCountAsync(1);
        await Assertions.Expect(chart.Locator(".chart-tooltip")).ToHaveCountAsync(1);
        await Assertions.Expect(chart.Locator(".chart-inspector")).ToHaveCountAsync(1);
        await Assertions.Expect(chart.Locator(".chart-x-axis .chart-axis-value")).ToHaveCountAsync(expectedXAxisTicks);
        var yAxisTicks = chart.Locator(".chart-y-axis .chart-axis-value");
        Assert.InRange(await yAxisTicks.CountAsync(), 4, 5);
        Assert.Equal(await yAxisTicks.CountAsync(), await chart.Locator(".chart-gridline").CountAsync());

        var xLabels = (await chart.Locator(".chart-x-axis .chart-axis-value").AllTextContentsAsync())
            .Select(value => value.Trim())
            .ToArray();
        var yLabels = (await yAxisTicks.AllTextContentsAsync())
            .Select(value => value.Trim())
            .ToArray();
        Assert.DoesNotContain(xLabels, string.IsNullOrWhiteSpace);
        Assert.DoesNotContain(yLabels, string.IsNullOrWhiteSpace);
        Assert.Equal(xLabels.Length, xLabels.Distinct(StringComparer.CurrentCulture).Count());
        Assert.Equal(yLabels.Length, yLabels.Distinct(StringComparer.CurrentCulture).Count());
        await AssertAxisLabelsFitAsync(chart);
    }

    private static async Task AssertChartSampleMetadataAsync(ILocator chart)
    {
        var sample = chart.Locator(".chart-sample").First;
        await Assertions.Expect(sample).ToHaveAttributeAsync("data-axis", new Regex(@"^-?\d+(?:\.\d+)?$"));
        await Assertions.Expect(sample).ToHaveAttributeAsync("data-value", new Regex(@"^-?\d+(?:\.\d+)?$"));
        await Assertions.Expect(sample).ToHaveAttributeAsync("data-axis-label", new Regex(@"\S"));
        await Assertions.Expect(sample).ToHaveAttributeAsync("data-value-label", new Regex(@"\S"));
    }

    private static async Task AssertPointerInspectionAsync(
        IPage page,
        ILocator root,
        int expectedCharts,
        string? expectedAxisSuffix = null)
    {
        var sourcePlot = root.Locator(".chart-plot").First;
        await sourcePlot.ScrollIntoViewIfNeededAsync();
        var bounds = await sourcePlot.BoundingBoxAsync();
        Assert.NotNull(bounds);
        await page.Mouse.MoveAsync(
            (float)(bounds.X + bounds.Width * 0.62),
            (float)(bounds.Y + bounds.Height * 0.5));

        await Assertions.Expect(root.Locator(".chart-cursor.visible")).ToHaveCountAsync(expectedCharts);
        await Assertions.Expect(root.Locator(".chart-marker.visible")).ToHaveCountAsync(expectedCharts);
        await Assertions.Expect(root.Locator(".chart-tooltip.visible")).ToHaveCountAsync(expectedCharts);
        var inspection = await root.EvaluateAsync<string[]>("""
            root => {
                const charts = root.matches('.spark-chart:has(.chart-marker.visible)')
                    ? [root]
                    : Array.from(root.querySelectorAll('.time-series-chart:has(.chart-marker.visible)'));
                return charts.map(chart => {
                    const marker = chart.querySelector('.chart-marker.visible');
                    const markerX = Number(marker?.getAttribute('cx'));
                    const samples = Array.from(chart.querySelectorAll('.chart-sample'));
                    const sample = samples.reduce((best, candidate) => {
                        if (!best) return candidate;
                        return Math.abs(Number(candidate.getAttribute('cx')) - markerX) <
                            Math.abs(Number(best.getAttribute('cx')) - markerX) ? candidate : best;
                    }, null);
                    const tooltip = chart.querySelector('.chart-tooltip.visible');
                    const text = tooltip?.textContent ?? '';
                    const axis = sample?.dataset.axisLabel ?? '';
                    const value = sample?.dataset.valueLabel ?? '';
                    return [axis, value, text, marker?.getAttribute('cx') ?? ''].join('\u001f');
                });
            }
            """);
        Assert.Equal(expectedCharts, inspection.Length);
        var selected = inspection.Select(value => value.Split('\u001f')).ToArray();
        Assert.Single(selected.Select(value => value[0]).Distinct(StringComparer.CurrentCulture));
        Assert.All(selected, value =>
        {
            Assert.False(string.IsNullOrWhiteSpace(value[0]));
            Assert.False(string.IsNullOrWhiteSpace(value[1]));
            Assert.Contains(value[0], value[2], StringComparison.CurrentCulture);
            Assert.Contains(value[1], value[2], StringComparison.CurrentCulture);
            Assert.False(string.IsNullOrWhiteSpace(value[3]));
        });
        if (expectedAxisSuffix is not null)
            Assert.All(selected, value => Assert.EndsWith(expectedAxisSuffix, value[0], StringComparison.OrdinalIgnoreCase));

        var cursorPositions = await root.Locator(".chart-cursor.visible")
            .EvaluateAllAsync<string[]>("cursors => cursors.map(cursor => cursor.getAttribute('x1') ?? '')");
        Assert.Single(cursorPositions.Distinct(StringComparer.Ordinal));
        var tooltipsContained = await root.EvaluateAsync<bool>("""
            root => Array.from(root.querySelectorAll('.chart-tooltip.visible')).every(tooltip => {
                const card = tooltip.closest('.time-series-chart, .spark-chart');
                const outer = card?.getBoundingClientRect();
                const inner = tooltip.getBoundingClientRect();
                return outer && inner.left >= outer.left - 1 && inner.right <= outer.right + 1 &&
                    inner.top >= outer.top - 1 && inner.bottom <= outer.bottom + 1;
            })
            """);
        Assert.True(tooltipsContained, "Visible chart tooltips must remain inside their chart cards.");

        await page.Mouse.MoveAsync(1, 1);
        await Assertions.Expect(root.Locator(".chart-cursor.visible")).ToHaveCountAsync(0);
        await Assertions.Expect(root.Locator(".chart-marker.visible")).ToHaveCountAsync(0);
        await Assertions.Expect(root.Locator(".chart-tooltip.visible")).ToHaveCountAsync(0);
    }

    private static async Task AssertIndependentInspectorAndTableAsync(ILocator chart)
    {
        var exactValues = chart.Locator("details.chart-accessible-values");
        await exactValues.Locator("summary").First.ClickAsync();
        var inspector = exactValues.Locator(".chart-inspector");
        var sampleCount = await chart.Locator(".chart-sample").CountAsync();
        Assert.True(sampleCount > 0);
        var initialValue = await inspector.GetAttributeAsync("aria-valuetext");
        await inspector.FocusAsync();
        if (sampleCount > 1)
        {
            await inspector.PressAsync("ArrowRight");
            await Assertions.Expect(inspector).Not.ToHaveAttributeAsync("aria-valuetext", initialValue ?? string.Empty);
        }
        else
        {
            await Assertions.Expect(inspector).ToHaveAttributeAsync("aria-valuetext", new Regex(@"\S"));
            await Assertions.Expect(exactValues.GetByText("Sample 1 of 1", new LocatorGetByTextOptions { Exact = true }))
                .ToBeVisibleAsync();
        }
        await Assertions.Expect(chart.Locator(".chart-marker.visible")).ToHaveCountAsync(1);
        await Assertions.Expect(chart.Locator(".chart-tooltip.visible")).ToHaveCountAsync(0);

        await exactValues.Locator("summary").Last.ClickAsync();
        await Assertions.Expect(chart.Locator(".chart-data-table tbody tr")).ToHaveCountAsync(sampleCount);
        await inspector.PressAsync("Escape");
        await Assertions.Expect(chart.Locator(".chart-marker.visible")).ToHaveCountAsync(0);
    }

    private static async Task AssertAxisLabelsFitAsync(ILocator chart)
    {
        var labelsFit = await chart.EvaluateAsync<bool>("""
            chart => {
                const outer = chart.getBoundingClientRect();
                const axes = Array.from(chart.querySelectorAll('.chart-x-axis, .chart-y-axis'));
                return axes.every(axis => {
                    const labels = Array.from(axis.querySelectorAll('.chart-axis-value'))
                        .filter(label => label.getClientRects().length > 0);
                    const inside = labels.every(label => {
                        const rect = label.getBoundingClientRect();
                        return rect.left >= outer.left - 1 && rect.right <= outer.right + 1 &&
                            rect.top >= outer.top - 1 && rect.bottom <= outer.bottom + 1;
                    });
                    const nonOverlapping = labels.every((label, index) => labels.slice(index + 1).every(other => {
                        const first = label.getBoundingClientRect();
                        const second = other.getBoundingClientRect();
                        return first.right <= second.left + 0.5 || second.right <= first.left + 0.5 ||
                            first.bottom <= second.top + 0.5 || second.bottom <= first.top + 0.5;
                    }));
                    return inside && nonOverlapping;
                });
            }
            """);
        Assert.True(labelsFit, "Visible chart axis labels must fit without overlapping.");
    }

    private static async Task AssertNoDocumentOverflowAsync(IPage page, string context)
    {
        var overflow = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
        Assert.False(overflow, $"{context} overflows horizontally.");
    }

    private static bool IsKnownHeadlessMapLibreError(string message) =>
        message.Contains("Could not compile fragment shader", StringComparison.Ordinal) ||
        message.Contains("Style is not done loading", StringComparison.Ordinal);

    private static async Task<BrowserActivitySeed> SeedActivityBrowserDataAsync(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        var options = new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(dataRoot, "activity-explorer.db")}")
            .Options;
        await using var db = new ExplorerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var owner = new OwnerProfile { DisplayName = "Browser athlete" };
        db.Owners.Add(owner);
        var detail = BrowserActivity(owner.Id, "Browser detail activity", SportKind.Cycling, 0);
        var stale = BrowserActivity(owner.Id, "Browser stale activity", SportKind.Walking, 1);
        var selected = BrowserActivity(owner.Id, "Browser selected activity", SportKind.Running, 2);
        var filteredOne = BrowserActivity(owner.Id, "Browser filtered one", SportKind.Cycling, 3);
        var filteredTwo = BrowserActivity(owner.Id, "Browser filtered two", SportKind.Cycling, 4);
        var keep = BrowserActivity(owner.Id, "Browser keep activity", SportKind.Walking, 5);
        db.Activities.AddRange(detail, stale, selected, filteredOne, filteredTwo, keep);
        await db.SaveChangesAsync();
        return new BrowserActivitySeed(owner.Id, detail.Id, stale.Id);
    }

    private static async Task<RouteCreatorSeed> SeedRouteCreatorDataAsync(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        var options = new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(dataRoot, "activity-explorer.db")}")
            .Options;
        await using var db = new ExplorerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var owner = new OwnerProfile { DisplayName = "Route creator athlete" };
        var points = Enumerable.Range(0, 14)
            .Select(index => new TrackPoint(
                null,
                55 + index * 0.001,
                12 + index * 0.0015,
                null,
                40 + index * 4 + Math.Sin(index / 2d) * 12,
                null, null, null, null, null))
            .ToArray();
        var missingElevationPoints = Enumerable.Range(0, 5)
            .Select(index => new TrackPoint(null, 56 + index * 0.001, 11 + index * 0.001, null, null, null, null, null, null, null))
            .ToArray();
        var route = BrowserRoute(owner.Id, "Browser elevation route", points);
        var missingElevationRoute = BrowserRoute(owner.Id, "Browser route without elevation", missingElevationPoints);
        db.AddRange(owner, route, missingElevationRoute);
        await db.SaveChangesAsync();
        return new RouteCreatorSeed(route.Id, missingElevationRoute.Id, points);
    }

    private static async Task<ActivityCreatorSeed> SeedActivityCreatorDataAsync(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        var options = new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(dataRoot, "activity-explorer.db")}")
            .Options;
        await using var db = new ExplorerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var owner = new OwnerProfile { DisplayName = "Activity creator athlete" };
        var start = new DateTimeOffset(2026, 8, 5, 7, 0, 0, TimeSpan.Zero);
        var points = Enumerable.Range(0, 9)
            .Select(index => new TrackPoint(
                start.AddSeconds(index),
                index is 1 or 4 ? null : 55 + (index - (index > 4 ? 2 : index > 1 ? 1 : 0)) * 0.001,
                index is 1 or 4 ? null : 12 + (index - (index > 4 ? 2 : index > 1 ? 1 : 0)) * 0.001,
                index * 120d,
                10 + index * 7,
                5, 130, 80, null, null))
            .ToArray();
        var missingElevationPoints = Enumerable.Range(0, 5)
            .Select(index => new TrackPoint(start.AddMinutes(1).AddSeconds(index), 56 + index * 0.001, 11 + index * 0.001, null, null, 4, 125, 78, null, null))
            .ToArray();
        var insufficientPoints = new[]
        {
            new TrackPoint(start.AddMinutes(2), 57, 10, null, 20, 3, null, null, null, null),
            new TrackPoint(start.AddMinutes(2).AddSeconds(2), null, null, null, null, null, null, null, null, null)
        };
        var activity = BrowserActivity(owner.Id, "Browser activity creator", SportKind.Cycling, points);
        var missingElevation = BrowserActivity(owner.Id, "Browser activity without elevation", SportKind.Running, missingElevationPoints);
        var insufficient = BrowserActivity(owner.Id, "Browser activity with one GPS point", SportKind.Walking, insufficientPoints);
        db.AddRange(owner, activity, missingElevation, insufficient);
        await db.SaveChangesAsync();
        return new ActivityCreatorSeed(activity.Id, missingElevation.Id, insufficient.Id, points);
    }

    private static Route BrowserRoute(Guid ownerId, string name, IReadOnlyList<TrackPoint> points)
    {
        var bounds = GeometryCodec.Bounds(points);
        var metrics = new TrackPathAnalysis(points).Slice(0, points.Count - 1);
        return new Route
        {
            OwnerId = ownerId,
            Name = name,
            Description = "Synthetic browser route",
            Sport = SportKind.Cycling,
            DistanceMeters = metrics.DistanceMeters,
            ElevationGainMeters = metrics.ElevationGainMeters ?? 0,
            GeometryWkb = GeometryCodec.ToWkb(points)!,
            MinLatitude = bounds.MinLat!.Value,
            MinLongitude = bounds.MinLon!.Value,
            MaxLatitude = bounds.MaxLat!.Value,
            MaxLongitude = bounds.MaxLon!.Value
        };
    }

    private static async Task AddBrowserActivityAsync(string dataRoot, Guid ownerId, string title, int dayOffset)
    {
        var options = new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(dataRoot, "activity-explorer.db")}")
            .Options;
        await using var db = new ExplorerDbContext(options);
        db.Activities.Add(BrowserActivity(ownerId, title, SportKind.Cycling, dayOffset));
        await db.SaveChangesAsync();
    }

    private static async Task DeleteBrowserActivityDirectlyAsync(string dataRoot, Guid activityId)
    {
        var options = new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(dataRoot, "activity-explorer.db")}")
            .Options;
        await using var db = new ExplorerDbContext(options);
        await db.Activities.Where(activity => activity.Id == activityId).ExecuteDeleteAsync();
    }

    private static ActivityExplorer.Core.Domain.Activity BrowserActivity(Guid ownerId, string title, SportKind sport, int dayOffset)
    {
        var points = TestSupport.Track(20)
            .Select(point => point with { Timestamp = point.Timestamp?.AddDays(dayOffset) })
            .ToArray();
        return BrowserActivity(ownerId, title, sport, points);
    }

    private static ActivityExplorer.Core.Domain.Activity BrowserActivity(
        Guid ownerId,
        string title,
        SportKind sport,
        IReadOnlyList<TrackPoint> points)
    {
        var positioned = points.Where(point => point.Latitude.HasValue && point.Longitude.HasValue).ToArray();
        var bounds = GeometryCodec.Bounds(positioned);
        var metrics = positioned.Length >= 2
            ? new TrackPathAnalysis(positioned).Slice(0, positioned.Length - 1)
            : new TrackPathSliceMetrics(0, null, null, null, null, null, positioned.Length);
        return new ActivityExplorer.Core.Domain.Activity
        {
            OwnerId = ownerId,
            Title = title,
            Sport = sport,
            StartTimeUtc = points[0].Timestamp ?? DateTimeOffset.UtcNow,
            NaturalFingerprint = Guid.NewGuid().ToString("N"),
            DistanceMeters = metrics.DistanceMeters,
            MovingTimeSeconds = Math.Max(0, points.Count - 1),
            ElapsedTimeSeconds = Math.Max(0, points.Count - 1),
            ElevationGainMeters = metrics.ElevationGainMeters ?? 0,
            AverageSpeedMetersPerSecond = 4,
            AverageHeartRate = 132,
            AverageCadence = 85,
            HasGps = positioned.Length > 0,
            HasPower = true,
            MinLatitude = bounds.MinLat,
            MinLongitude = bounds.MinLon,
            MaxLatitude = bounds.MaxLat,
            MaxLongitude = bounds.MaxLon,
            GeometryWkb = GeometryCodec.ToWkb(positioned),
            SimplifiedGeometryWkb = GeometryCodec.ToWkb(positioned, 0.00001),
            Stream = new ActivityStream
            {
                OwnerId = ownerId,
                CompressedPayload = TrackCodec.Encode(points),
                PointCount = points.Count
            }
        };
    }

    private sealed record BrowserActivitySeed(Guid OwnerId, Guid DetailActivityId, Guid StaleActivityId);
    private sealed record RouteCreatorSeed(Guid RouteId, Guid MissingElevationRouteId, IReadOnlyList<TrackPoint> Points);
    private sealed record ActivityCreatorSeed(
        Guid ActivityId,
        Guid MissingElevationActivityId,
        Guid InsufficientGpsActivityId,
        IReadOnlyList<TrackPoint> Points);

    private static Process StartApplication(string assembly, string dataRoot, string origin, StringBuilder output)
    {
        var start = new ProcessStartInfo("dotnet", $"\"{assembly}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment["Urls"] = origin;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ACTIVITY_EXPLORER_DATA"] = dataRoot;
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Activity Explorer.");
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) output.AppendLine(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) output.AppendLine(args.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitUntilReadyAsync(string origin, Process process, StringBuilder output)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited) throw new InvalidOperationException($"The browser test host exited early.{Environment.NewLine}{output}");
            try
            {
                using var response = await client.GetAsync(origin + "/");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"The browser test host did not become ready.{Environment.NewLine}{output}");
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task DeleteDirectoryAsync(string path)
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                await Task.Delay(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                await Task.Delay(100);
            }
        }
    }

    private static string FindWebAssembly(string root)
    {
        var projectReferenceCopy = Path.Combine(AppContext.BaseDirectory, "ActivityExplorer.Web.dll");
        if (File.Exists(projectReferenceCopy)) return projectReferenceCopy;

        var repositoryBuild = Path.Combine(
            root, "src", "ActivityExplorer.Web", "bin", "Release", "net10.0", "ActivityExplorer.Web.dll");
        Assert.True(
            File.Exists(repositoryBuild),
            $"Build the Release web project before browser tests: {repositoryBuild}");
        return repositoryBuild;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ActivityExplorer.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
