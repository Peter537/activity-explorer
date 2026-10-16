using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    [Fact]
    public async Task Library_preview_fixture_is_created_only_when_explicitly_requested()
    {
        var requestedPath = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_LIBRARY_PREVIEW_DATA");
        if (string.IsNullOrWhiteSpace(requestedPath)) return;
        var path = Path.GetFullPath(requestedPath);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException("The library preview fixture requires an empty directory.");
        Directory.CreateDirectory(path);
        var seed = await SeedLibraryBrowserDataAsync(path);
        await File.WriteAllTextAsync(Path.Combine(path, "synthetic-fixture.json"), JsonSerializer.Serialize(seed));
        TestContext.Current.TestOutputHelper!.WriteLine($"Synthetic library preview data: {path}");
    }

    [Fact]
    public async Task Library_finding_preserves_route_order_selected_tags_and_recorded_power_summaries()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedLibraryBrowserDataAsync(dataRoot);
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
            await page.GotoAsync(origin + "/routes?q=Canal&sport=cycling&sort=distance-desc", new() { WaitUntil = WaitUntilState.NetworkIdle });
            var routeFilters = page.GetByRole(AriaRole.Form, new() { Name = "Route filters", Exact = true });
            await Assertions.Expect(page.Locator(".entity-card h2")).ToHaveTextAsync(["Canal long", "Canal flat", "Canal short"]);
            await Assertions.Expect(page.GetByText("3 matching routes", new() { Exact = true })).ToBeVisibleAsync();
            await page.GetByLabel("Name", new() { Exact = true }).FillAsync("Unsaved creation name");
            await routeFilters.GetByRole(AriaRole.Combobox, new() { Name = "Route order", Exact = true }).SelectOptionAsync("elevation-asc");
            await routeFilters.GetByRole(AriaRole.Button, new() { Name = "Apply route filters", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".entity-card h2")).ToHaveTextAsync(["Canal flat", "Canal short", "Canal long"]);
            await Assertions.Expect(page.GetByLabel("Name", new() { Exact = true })).ToHaveValueAsync("Unsaved creation name");
            var elevationUrl = page.Url;
            await page.GoBackAsync();
            await Assertions.Expect(page.Locator(".entity-card h2")).ToHaveTextAsync(["Canal long", "Canal flat", "Canal short"]);
            await page.GoForwardAsync();
            await Assertions.Expect(page).ToHaveURLAsync(elevationUrl);
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(routeFilters.GetByRole(AriaRole.Combobox, new() { Name = "Route order", Exact = true })).ToHaveValueAsync("elevation-asc");
            await Assertions.Expect(page.Locator(".entity-card h2")).ToHaveTextAsync(["Canal flat", "Canal short", "Canal long"]);
            await routeFilters.GetByLabel("Route name", new() { Exact = true }).FillAsync("No such synthetic route");
            await routeFilters.GetByRole(AriaRole.Button, new() { Name = "Apply route filters", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".entity-card")).ToHaveCountAsync(0);
            await routeFilters.GetByRole(AriaRole.Button, new() { Name = "Reset route filters", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".entity-card")).ToHaveCountAsync(5);
            await AssertLibraryResponsiveAsync(page, "routes");

            await page.GotoAsync(origin + "/activities", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.OwnerId.ToString());
            var tags = page.GetByRole(AriaRole.Group, new() { Name = "Tags — match any", Exact = true });
            await tags.GetByRole(AriaRole.Checkbox, new() { Name = "Training", Exact = true }).CheckAsync();
            await tags.GetByLabel("Find tags", new() { Exact = true }).FillAsync("Commute");
            await Assertions.Expect(tags.GetByRole(AriaRole.Checkbox, new() { Name = "Training", Exact = true })).ToBeCheckedAsync();
            await Assertions.Expect(tags.GetByRole(AriaRole.Checkbox, new() { Name = "Commute", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(tags.GetByRole(AriaRole.Checkbox, new() { Name = "Travel", Exact = true })).ToHaveCountAsync(0);
            await ApplyReportingFiltersAsync(page);
            var saved = page.Locator(".saved-searches");
            await saved.Locator("summary").ClickAsync();
            await saved.GetByRole(AriaRole.Combobox, new() { Name = "Saved search", Exact = true }).SelectOptionAsync(seed.SelectedSavedSearch.ToString());
            await saved.GetByLabel("Find saved searches", new() { Exact = true }).FillAsync("Zulu");
            await Assertions.Expect(saved.GetByRole(AriaRole.Combobox, new() { Name = "Saved search", Exact = true })).ToHaveValueAsync(seed.SelectedSavedSearch.ToString());
            await Assertions.Expect(saved.GetByRole(AriaRole.Combobox, new() { Name = "Saved search", Exact = true }).Locator("option")).ToHaveCountAsync(3);
            await saved.GetByRole(AriaRole.Button, new() { Name = "Open search", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex(seed.TrainingTag.ToString()));
            await Assertions.Expect(tags.GetByRole(AriaRole.Checkbox, new() { Name = "Training", Exact = true })).ToBeCheckedAsync();
            var manager = page.Locator(".tag-manager");
            await manager.Locator("summary").ClickAsync();
            await manager.GetByRole(AriaRole.Button, new() { Name = "Rename tag Training", Exact = true }).ClickAsync();
            await manager.GetByLabel("Find managed tags", new() { Exact = true }).FillAsync("Commute");
            await Assertions.Expect(manager.GetByLabel("Tag name", new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(manager.Locator(".managed-tag")).ToHaveCountAsync(2);
            await manager.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            await Assertions.Expect(manager.Locator(".managed-tag")).ToHaveCountAsync(1);
            await Assertions.Expect(manager.Locator(".managed-tag")).ToContainTextAsync("Commute");
            await AssertLibraryResponsiveAsync(page, "organization-finding");

            await page.GotoAsync(origin + $"/activities/{seed.RecordedPowerActivity}", new() { WaitUntil = WaitUntilState.NetworkIdle });
            var recorded = page.Locator("article[aria-label='Recorded power and work']");
            await Assertions.Expect(recorded.Locator("dt")).ToHaveTextAsync(["Average power", "Maximum power", "Normalized power", "Work"]);
            await Assertions.Expect(recorded.Locator("dd")).ToHaveTextAsync(["210 W", "520 W", "235 W", $"{756.5:N1} kJ"]);
            await Assertions.Expect(recorded).ToContainTextAsync("Whole-activity summaries reported by the source");
            await page.GotoAsync(origin + $"/activities/{seed.ZeroPowerActivity}", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(recorded.Locator("dd")).ToHaveTextAsync(["0 W", "0 W", "0 W", $"{0d:N1} kJ"]);
            await page.GotoAsync(origin + $"/activities/{seed.PartialPowerActivity}", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(recorded.Locator("dd")).ToHaveTextAsync(["190 W", "--", "--", "--"]);
            await page.GotoAsync(origin + $"/activities/{seed.MissingPowerActivity}", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(recorded).ToHaveCountAsync(0);
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
    public async Task Library_finding_keeps_shared_ancestors_and_exact_repeated_pass_comparisons()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedLibraryBrowserDataAsync(dataRoot);
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
            await page.GotoAsync(origin + "/segments?q=Needle&sport=Running", new() { WaitUntil = WaitUntilState.NetworkIdle });
            var library = page.Locator(".segment-library");
            await Assertions.Expect(library.GetByRole(AriaRole.Link, new() { Name = "Needle turn", Exact = true })).ToHaveCountAsync(2);
            await Assertions.Expect(library.GetByRole(AriaRole.Link, new() { Name = "Needle turn", Exact = true }).First).ToBeVisibleAsync();
            await Assertions.Expect(library.GetByRole(AriaRole.Link, new() { Name = "Waterside stretch", Exact = true })).ToHaveCountAsync(2);
            await Assertions.Expect(library.GetByRole(AriaRole.Link, new() { Name = "One park lap", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(library.GetByRole(AriaRole.Link, new() { Name = "Two park laps", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(library.GetByText("Ancestor context", new() { Exact = true }).First).ToBeVisibleAsync();
            await Assertions.Expect(library.GetByRole(AriaRole.Link, new() { Name = "Needle cycling decoy", Exact = true })).ToHaveCountAsync(0);
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(library.GetByRole(AriaRole.Link, new() { Name = "Needle turn", Exact = true }).First).ToBeVisibleAsync();
            await AssertLibraryResponsiveAsync(page, "segment-finding");

            await page.GotoAsync(origin + $"/segments/{seed.OneLapSegment}/subsegments/new", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Button, new() { Name = "Use existing segment", Exact = true }).ClickAsync();
            var candidateSearch = page.GetByLabel("Search existing segments", new() { Exact = true });
            var candidates = page.GetByRole(AriaRole.Combobox, new() { Name = "Existing segment", Exact = true });
            await candidateSearch.FillAsync("Attachable");
            await Assertions.Expect(candidates.Locator("option")).ToHaveCountAsync(2);
            await candidates.SelectOptionAsync(seed.AttachmentCandidate.ToString());
            await Assertions.Expect(page.GetByRole(AriaRole.Region, new() { Name = "Parent and existing sub-segment preview", Exact = true })).ToBeVisibleAsync();
            await candidateSearch.FillAsync("No matching name");
            await Assertions.Expect(candidates).ToHaveValueAsync(seed.AttachmentCandidate.ToString());
            await Assertions.Expect(candidates.Locator("option")).ToHaveCountAsync(2);
            await page.GetByRole(AriaRole.Button, new() { Name = "Attach segment", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(origin + $"/segments/{seed.OneLapSegment}?section={seed.AttachmentCandidate}");
            await Assertions.Expect(page.Locator($"[data-subsegment-id='{seed.AttachmentCandidate}']")).ToBeVisibleAsync();

            var effortUrl = origin + $"/segments/{seed.OneLapSegment}?effort={seed.FirstPass}&effort-from=2026-06-01&effort-to=2026-06-01&effort-min=80&effort-max=80&effort-sort=date";
            await page.GotoAsync(effortUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
            var historyRows = page.Locator(".effort-table tbody tr");
            await Assertions.Expect(historyRows).ToHaveCountAsync(2);
            Assert.Equal(new[] { seed.SecondPass.ToString(), seed.FirstPass.ToString() },
                await historyRows.EvaluateAllAsync<string[]>("rows => rows.map(row => row.getAttribute('data-effort-id'))"));
            await page.Locator($"[data-effort-id='{seed.SecondPass}']").GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Use as baseline") }).ClickAsync();
            await page.Locator($"[data-effort-id='{seed.FirstPass}']").GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Use as comparison") }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Baseline", Exact = true })).ToHaveValueAsync(seed.SecondPass.ToString());
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Comparison", Exact = true })).ToHaveValueAsync(seed.FirstPass.ToString());
            await page.GetByLabel("Efforts from", new() { Exact = true }).FillAsync("2026-06-02");
            await page.GetByLabel("Efforts to", new() { Exact = true }).FillAsync("2026-06-02");
            await page.GetByLabel("Minimum elapsed (seconds)", new() { Exact = true }).FillAsync("160");
            await page.GetByLabel("Maximum elapsed (seconds)", new() { Exact = true }).FillAsync("160");
            await page.GetByRole(AriaRole.Button, new() { Name = "Apply effort filters", Exact = true }).ClickAsync();
            await Assertions.Expect(historyRows).ToHaveCountAsync(1);
            await Assertions.Expect(historyRows).ToHaveAttributeAsync("data-effort-id", seed.SlowFirstPass.ToString());
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Baseline", Exact = true })).ToHaveValueAsync(seed.SecondPass.ToString());
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Comparison", Exact = true })).ToHaveValueAsync(seed.FirstPass.ToString());
            await Assertions.Expect(page.Locator("[data-effort-selection-outside]")).ToHaveCountAsync(2);
            var filteredPairUrl = page.Url;
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Baseline", Exact = true })).ToHaveValueAsync(seed.SecondPass.ToString());
            await Assertions.Expect(page.Locator("[data-effort-selection-outside]")).ToHaveCountAsync(2);
            await page.GoBackAsync();
            await Assertions.Expect(historyRows).ToHaveCountAsync(2);
            await page.GoForwardAsync();
            await Assertions.Expect(page).ToHaveURLAsync(filteredPairUrl);
            await Assertions.Expect(historyRows).ToHaveCountAsync(1);
            await AssertLibraryResponsiveAsync(page, "effort-finding");
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

    private static async Task AssertLibraryResponsiveAsync(IPage page, string name)
    {
        var captures = Path.Combine(FindRepositoryRoot(), "artifacts", "library-ui");
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

    private static async Task<LibraryBrowserSeed> SeedLibraryBrowserDataAsync(string dataRoot)
    {
        var options = ReportingBrowserOptions(dataRoot);
        var factory = new SegmentBrowserDbFactory(options);
        var owner = new OwnerProfile { DisplayName = "Library preview athlete", TimeZoneId = "Europe/Copenhagen" };
        var start = new DateTimeOffset(2026, 6, 1, 21, 58, 0, TimeSpan.Zero);
        var loop = Enumerable.Range(0, 81).Select(index => new TrackPoint(start.AddSeconds(index),
            1 + 0.001 * Math.Sin(2 * Math.PI * index / 80), -30 + 0.001 * Math.Cos(2 * Math.PI * index / 80),
            null, 20 + 5 * Math.Sin(2 * Math.PI * index / 80), null, 140, 80, null, null)).ToArray();
        var laps = loop.Concat(loop.Skip(1)).Concat(loop.Skip(1))
            .Select((point, index) => point with { Timestamp = start.AddSeconds(index) }).ToArray();
        var first = BrowserActivity(owner.Id, "Repeated park laps", SportKind.Running, laps);
        var slow = BrowserActivity(owner.Id, "Slower repeated laps", SportKind.Running,
            laps.Select((point, index) => point with { Timestamp = start.AddDays(1).AddSeconds(index * 2) }).ToArray());
        var full = PowerSummary("Source summary without samples", 210, 520, 235, 756.5);
        var zero = PowerSummary("Recorded zero power", 0, 0, 0, 0);
        var partial = PowerSummary("Partially recorded power", 190, null, null, null);
        var missing = PowerSummary("Unrecorded power", null, null, null, null);
        var training = new Tag { OwnerId = owner.Id, Name = "Training", NormalizedName = "TRAINING" };
        var commute = new Tag { OwnerId = owner.Id, Name = "Commute", NormalizedName = "COMMUTE" };
        var travel = new Tag { OwnerId = owner.Id, Name = "Travel", NormalizedName = "TRAVEL" };
        first.Tags.Add(new ActivityTag { ActivityId = first.Id, TagId = training.Id });
        var alpha = Search("Alpha selected search", [new SavedTagReference(training.Id, training.Name)]);
        var zulu = Search("Zulu travel search", [new SavedTagReference(travel.Id, travel.Name)]);
        var hidden = Search("Middle hidden search", []);
        var routes = new[]
        {
            Route("Canal short", SportKind.Cycling, 8000, 80),
            Route("Canal flat", SportKind.Cycling, 16000, 0),
            Route("Canal long", SportKind.Cycling, 24000, 240),
            Route("Canal walk", SportKind.Walking, 3000, 30),
            Route("Meadow ride", SportKind.Cycling, 12000, 100)
        };
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.AddRange(owner, first, slow, full, zero, partial, missing, training, commute, travel, alpha, zulu, hidden);
            db.Routes.AddRange(routes);
            await db.SaveChangesAsync();
        }
        var service = new SegmentService(factory, new SegmentMatcher(), new OwnerMutationLock());
        var parent = await service.CreateFromActivityAsync(new(owner.Id, first.Id, "Two park laps", 0, 160));
        var oneLap = await service.CreateFromActivityAsync(new(owner.Id, first.Id, "One park lap", 0, 80));
        var child = await service.CreateSubsegmentAsync(new(parent, "Waterside stretch", 20, 40));
        var grandchild = await service.CreateSubsegmentAsync(new(child, "Needle turn", 5, 15));
        await service.AttachAsync(oneLap, child);
        var attachable = await service.CreateFromActivityAsync(new(owner.Id, first.Id, "Attachable rise", 45, 60));
        await service.CreateAsync(new CreateSegmentPathRequest(owner.Id, "Needle cycling decoy", SportKind.Cycling, loop));
        var detail = (await service.GetAsync(oneLap))!;
        var firstPasses = detail.Efforts.Where(effort => effort.ActivityId == first.Id).OrderBy(effort => effort.StartPointIndex).ToArray();
        var slowFirst = detail.Efforts.Where(effort => effort.ActivityId == slow.Id).MinBy(effort => effort.StartPointIndex)!;
        Assert.Equal(3, firstPasses.Length);
        return new(owner.Id, training.Id, alpha.Id, parent, oneLap, child, grandchild, attachable,
            firstPasses[0].Id, firstPasses[1].Id, firstPasses[2].Id, slowFirst.Id,
            full.Id, zero.Id, partial.Id, missing.Id);

        Activity PowerSummary(string title, double? average, double? maximum, double? normalized, double? work) => new()
        {
            OwnerId = owner.Id,
            Title = title,
            Sport = SportKind.Cycling,
            StartTimeUtc = start,
            NaturalFingerprint = Guid.NewGuid().ToString("N"),
            DistanceMeters = 12000,
            MovingTimeSeconds = 3600,
            ElapsedTimeSeconds = 3600,
            HasPower = false,
            AveragePowerWatts = average,
            MaxPowerWatts = maximum,
            NormalizedPowerWatts = normalized,
            Kilojoules = work
        };

        SavedSearch Search(string name, IReadOnlyList<SavedTagReference> tags) => new()
        {
            OwnerId = owner.Id,
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            CriteriaJson = JsonSerializer.Serialize(new SavedSearchCriteria(2, null, null, null, null,
                tags, "start-desc", new(ReportingPreset.AllTime), TextMode: ActivityTextMode.Words))
        };

        Route Route(string name, SportKind sport, double distance, double ascent)
        {
            var route = BrowserRoute(owner.Id, name, loop);
            route.Sport = sport;
            route.DistanceMeters = distance;
            route.ElevationGainMeters = ascent;
            return route;
        }
    }

    private sealed record LibraryBrowserSeed(Guid OwnerId, Guid TrainingTag, Guid SelectedSavedSearch,
        Guid TwoLapSegment, Guid OneLapSegment, Guid SharedChild, Guid Grandchild, Guid AttachmentCandidate,
        Guid FirstPass, Guid SecondPass, Guid ThirdPass, Guid SlowFirstPass,
        Guid RecordedPowerActivity, Guid ZeroPowerActivity, Guid PartialPowerActivity, Guid MissingPowerActivity);
}
