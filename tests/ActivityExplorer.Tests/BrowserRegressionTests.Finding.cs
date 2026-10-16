using System.Globalization;
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
    public async Task Finding_preview_fixture_is_created_only_when_explicitly_requested()
    {
        var requestedPath = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_FINDING_PREVIEW_DATA");
        if (string.IsNullOrWhiteSpace(requestedPath)) return;
        var path = Path.GetFullPath(requestedPath);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException("The finding preview fixture requires an empty directory.");
        Directory.CreateDirectory(path);
        var seed = await SeedFindingBrowserDataAsync(path);
        await File.WriteAllTextAsync(Path.Combine(path, "synthetic-fixture.json"), JsonSerializer.Serialize(seed));
        TestContext.Current.TestOutputHelper!.WriteLine($"Synthetic import/goal finding preview data: {path}");
    }

    [Fact]
    public async Task Import_finding_searches_older_batches_and_preserves_linked_scope_and_hidden_upload_polling()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedFindingBrowserDataAsync(dataRoot);
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
            await page.GotoAsync(origin + "/imports", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.ImportOwner.ToString());
            var history = page.Locator("details.import-history");
            await history.Locator("summary").First.ClickAsync();
            await Assertions.Expect(history.Locator(".import-entry")).ToHaveCountAsync(10);
            await Assertions.Expect(history.GetByText("Archive target 01", new() { Exact = true })).ToHaveCountAsync(0);
            var filters = page.GetByRole(AriaRole.Form, new() { Name = "Import history filters", Exact = true });
            await filters.GetByLabel("Search imports", new() { Exact = true }).FillAsync("Archive");
            await filters.GetByRole(AriaRole.Combobox, new() { Name = "Status", Exact = true }).SelectOptionAsync(nameof(ImportStatus.CompletedWithWarnings));
            await filters.GetByRole(AriaRole.Combobox, new() { Name = "Warnings", Exact = true }).SelectOptionAsync("yes");
            await filters.GetByRole(AriaRole.Combobox, new() { Name = "Source", Exact = true }).SelectOptionAsync(nameof(SourceKind.Gpx));
            await filters.GetByRole(AriaRole.Combobox, new() { Name = "Import type", Exact = true }).SelectOptionAsync(nameof(ImportBatchKind.FileImport));
            await filters.GetByLabel("Imported from", new() { Exact = true }).FillAsync("2026-01-02");
            await filters.GetByLabel("Imported to", new() { Exact = true }).FillAsync("2026-01-02");
            await filters.GetByRole(AriaRole.Button, new() { Name = "Apply history filters", Exact = true }).ClickAsync();
            await Assertions.Expect(history.Locator(".import-entry")).ToHaveCountAsync(10);
            await Assertions.Expect(history.Locator("summary").First.Locator("small")).ToHaveTextAsync("16 total");
            await history.GetByRole(AriaRole.Button, new() { Name = "Show 10 more", Exact = true }).ClickAsync();
            await Assertions.Expect(history.Locator(".import-entry")).ToHaveCountAsync(16);
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]limit=20(?:&|$)"));
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.SecondOwner.ToString());
            await Assertions.Expect(history.Locator(".import-entry")).ToHaveCountAsync(0);
            await Assertions.Expect(page).Not.ToHaveURLAsync(new Regex("[?&]limit=20(?:&|$)"));
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Profile selector" }).SelectOptionAsync(seed.ImportOwner.ToString());
            await Assertions.Expect(history.Locator(".import-entry")).ToHaveCountAsync(10);
            await history.GetByRole(AriaRole.Button, new() { Name = "Show 10 more", Exact = true }).ClickAsync();
            await Assertions.Expect(history.Locator(".import-entry")).ToHaveCountAsync(16);
            var target = history.Locator(".import-entry").Filter(new() { HasText = "Archive target 01" });
            await Assertions.Expect(target.Locator(".import-entry-heading small")).ToContainTextAsync(new DateTime(2026, 1, 2, 0, 30, 0).ToString("g", CultureInfo.CurrentCulture));
            await target.GetByRole(AriaRole.Link, new() { Name = "View linked activities", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".activity-totals dd").First).ToHaveTextAsync("2");
            Assert.Equal(seed.LinkedActivities.Order(), (await OrganizationVisibleIdsAsync(page)).Order());
            Assert.Contains($"importBatch={seed.LinkedBatch}", page.Url, StringComparison.Ordinal);
            Assert.Contains($"importOwner={seed.ImportOwner}", page.Url, StringComparison.Ordinal);
            await page.GetByRole(AriaRole.Button, new() { Name = "Select this page", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Delete selected", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".delete-confirmation")).ToBeVisibleAsync();
            await page.EvaluateAsync("url => Blazor.navigateTo(url)", $"/activities?importBatch={seed.SecondLinkedBatch}&importOwner={seed.ImportOwner}");
            await Assertions.Expect(page.Locator(".activity-totals dd").First).ToHaveTextAsync("1");
            await Assertions.Expect(page.Locator(".activity-row")).ToContainTextAsync("Unrelated ride");
            await Assertions.Expect(page.Locator(".activity-selector input:checked")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".delete-confirmation")).ToHaveCountAsync(0);
            await page.GoBackAsync();
            await Assertions.Expect(page.Locator(".activity-totals dd").First).ToHaveTextAsync("2");
            await Assertions.Expect(page.Locator(".activity-selector input:checked")).ToHaveCountAsync(0);
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Button, new() { Name = "Switch to Finding East", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".activity-totals dd").First).ToHaveTextAsync("2");
            await page.GoBackAsync();
            await Assertions.Expect(history.Locator(".import-entry")).ToHaveCountAsync(16);
            await target.GetByRole(AriaRole.Link, new() { Name = "View linked routes", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".entity-card h2")).ToHaveTextAsync(["Linked route"]);
            await Assertions.Expect(page.Locator(".entity-card")).ToHaveAttributeAsync("href", $"/routes/{seed.LinkedRoute}");
            await page.GoBackAsync();
            await Assertions.Expect(history.Locator(".import-entry")).ToHaveCountAsync(16);
            await filters.GetByLabel("Search imports", new() { Exact = true }).FillAsync("Archive target 01");
            await filters.GetByRole(AriaRole.Button, new() { Name = "Apply history filters", Exact = true }).ClickAsync();
            await Assertions.Expect(history.Locator(".import-entry")).ToHaveCountAsync(1);
            Assert.DoesNotContain("limit=20", page.Url, StringComparison.Ordinal);
            await page.GoBackAsync();
            await Assertions.Expect(history.Locator(".import-entry")).ToHaveCountAsync(16);
            await page.GoForwardAsync();
            await Assertions.Expect(history.Locator(".import-entry")).ToHaveCountAsync(1);

            var uploadPath = TestSupport.Write(dataRoot, "hidden-polling.gpx", TestSupport.Gpx(name: "Hidden polling recording"));
            await page.GetByLabel("Choose one or more files", new() { Exact = true }).SetInputFilesAsync(uploadPath);
            await page.GetByRole(AriaRole.Button, new() { Name = "Queue selected files", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByText("All selected files finished processing successfully.", new() { Exact = true }))
                .ToBeVisibleAsync(new() { Timeout = 30000 });
            await Assertions.Expect(history.Locator(".import-entry")).ToHaveCountAsync(1);
            await Assertions.Expect(target).ToBeVisibleAsync();
            await AssertGoalsResponsiveAsync(page, "import-history-finding");
            await filters.GetByRole(AriaRole.Button, new() { Name = "Reset history filters", Exact = true }).ClickAsync();
            await Assertions.Expect(history.Locator(".import-entry")).ToHaveCountAsync(10);
            Assert.Empty(errors);
        }
        catch { TestContext.Current.TestOutputHelper!.WriteLine(output.ToString()); throw; }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    [Fact]
    public async Task Goal_finding_distinguishes_progress_and_finds_historical_editions_without_substitution()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedFindingBrowserDataAsync(dataRoot);
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
            page.Console += (_, message) => { if (message.Type == "error") errors.Add(message.Text); };
            foreach (var invalid in new[] { "sport=invalid", "metric=invalid" })
            {
                await page.GotoAsync(origin + "/goals?" + invalid, new() { WaitUntil = WaitUntilState.NetworkIdle });
                await Assertions.Expect(page.GetByRole(AriaRole.Alert).First).ToBeVisibleAsync();
                await page.GetByLabel("Goal name", new() { Exact = true }).FillAsync("Recovered");
                await page.GetByRole(AriaRole.Button, new() { Name = "Apply goal filters", Exact = true }).ClickAsync();
                await Assertions.Expect(page.GetByRole(AriaRole.Alert).First).ToBeVisibleAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Reset goal filters", Exact = true }).ClickAsync();
                await Assertions.Expect(page.GetByLabel("Goal name", new() { Exact = true })).ToHaveValueAsync("");
                await Assertions.Expect(page.Locator(".goal-list article.panel").First).ToBeVisibleAsync();
            }
            await page.GotoAsync(origin + "/goals?view=all", new() { WaitUntil = WaitUntilState.NetworkIdle });
            var filters = page.GetByRole(AriaRole.Form, new() { Name = "Goal filters", Exact = true });
            await filters.GetByLabel("Goal name", new() { Exact = true }).FillAsync("Renamed endurance");
            await filters.GetByRole(AriaRole.Combobox, new() { Name = "Goal sport", Exact = true }).SelectOptionAsync(nameof(SportKind.Running));
            await filters.GetByRole(AriaRole.Combobox, new() { Name = "Goal metric", Exact = true }).SelectOptionAsync(nameof(GoalMetric.Distance));
            await filters.GetByRole(AriaRole.Combobox, new() { Name = "Goal progress", Exact = true }).SelectOptionAsync("unreached");
            await filters.GetByRole(AriaRole.Button, new() { Name = "Apply goal filters", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".goal-list article.panel")).ToHaveCountAsync(1);
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Renamed endurance", Exact = true })).ToBeVisibleAsync();
            await filters.GetByLabel("Goal name", new() { Exact = true }).FillAsync("Historic endurance");
            await filters.GetByRole(AriaRole.Button, new() { Name = "Apply goal filters", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".goal-list article.panel")).ToHaveCountAsync(0);
            await page.GoBackAsync();
            await Assertions.Expect(page.Locator(".goal-list article.panel")).ToHaveCountAsync(1);
            await page.GoForwardAsync();
            await Assertions.Expect(page.Locator(".goal-list article.panel")).ToHaveCountAsync(0);
            await page.GotoAsync(origin + "/goals?view=all&q=Reached&sport=Cycling&metric=ActivityCount&progress=reached", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator(".goal-list article.panel")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".goal-list .goal-status")).ToContainTextAsync("Target reached");
            await page.GotoAsync(origin + "/goals?view=archived&q=Archived%20before", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator(".goal-list article.panel")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".goal-list")).ToContainTextAsync("archived before its first edition began");
            await filters.GetByRole(AriaRole.Combobox, new() { Name = "Goal progress", Exact = true }).SelectOptionAsync("unreached");
            await filters.GetByRole(AriaRole.Button, new() { Name = "Apply goal filters", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".goal-list article.panel")).ToHaveCountAsync(0);
            await page.GotoAsync(origin + "/goals?q=Next%20month", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator(".goal-list article.panel")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".goal-list .goal-status")).ToContainTextAsync("Upcoming");
            await AssertGoalsResponsiveAsync(page, "goal-finding");

            await page.GotoAsync(origin + $"/goals/{seed.HistoryGoal}?historyPage=2&contributionPage=2", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByLabel("Edition date", new() { Exact = true }).FillAsync("2022-02-03");
            await page.GetByRole(AriaRole.Button, new() { Name = "Find edition", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]edition=2022-01-31(?:&|$)"));
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"[?&]historyPage={seed.ExpectedHistoryPage}(?:&|$)"));
            Assert.DoesNotContain("contributionPage=2", page.Url, StringComparison.Ordinal);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Historic endurance", Exact = true })).ToBeVisibleAsync();
            var history = page.GetByRole(AriaRole.Region, new() { Name = "Edition history", Exact = true });
            await Assertions.Expect(history.Locator("a[aria-current='true']")).ToHaveCountAsync(1);
            await Assertions.Expect(history.Locator("a[aria-current='true']")).ToHaveAttributeAsync("href", new Regex("edition=2022-01-31"));
            var selectedUrl = page.Url;
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page).ToHaveURLAsync(selectedUrl);
            await Assertions.Expect(history.Locator("a[aria-current='true']")).ToHaveCountAsync(1);
            await page.GetByRole(AriaRole.Button, new() { Name = "Current / latest edition", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Renamed endurance", Exact = true })).ToBeVisibleAsync();
            var currentUrl = page.Url;
            await page.GetByLabel("Edition date", new() { Exact = true }).FillAsync("2020-12-31");
            await page.GetByRole(AriaRole.Button, new() { Name = "Find edition", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Alert).First).ToBeVisibleAsync();
            Assert.Equal(currentUrl, page.Url);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Renamed endurance", Exact = true })).ToBeVisibleAsync();
            await AssertGoalsResponsiveAsync(page, "goal-edition-finding");
            Assert.Empty(errors);
        }
        catch { TestContext.Current.TestOutputHelper!.WriteLine(output.ToString()); throw; }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    private static async Task<FindingBrowserSeed> SeedFindingBrowserDataAsync(string dataRoot)
    {
        var goals = await SeedGoalsBrowserDataAsync(dataRoot);
        var owner = new OwnerProfile { DisplayName = "Finding East", TimeZoneId = "Europe/Copenhagen" };
        var imported = new List<ImportBatch>();
        for (var index = 0; index < 20; index++) imported.Add(Batch($"Recent import {index + 1:00}", new(2026, 2, index + 1, 12, 0, 0, TimeSpan.Zero)));
        for (var index = 0; index < 16; index++) imported.Add(Batch($"Archive target {index + 1:00}", new(2026, 1, 1, 23, 30 + index, 0, TimeSpan.Zero)));
        var linkedBatch = imported[20];
        var wrongStatus = Batch("Archive wrong status", linkedBatch.CreatedAtUtc); wrongStatus.Status = ImportStatus.Failed;
        var wrongWarnings = Batch("Archive no warning", linkedBatch.CreatedAtUtc); wrongWarnings.Warnings = 0;
        var wrongSource = Batch("Archive wrong source", linkedBatch.CreatedAtUtc); wrongSource.SourceKind = SourceKind.Fit;
        var wrongKind = Batch("Archive wrong kind", linkedBatch.CreatedAtUtc); wrongKind.Kind = ImportBatchKind.RouteImport;
        var wrongDate = Batch("Archive previous local date", new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        imported.AddRange([wrongStatus, wrongWarnings, wrongSource, wrongKind, wrongDate]);
        var first = ReportingBrowserActivity(owner.Id, "Linked ride one", linkedBatch.CreatedAtUtc, 12000, 1800, 100);
        var second = ReportingBrowserActivity(owner.Id, "Linked ride two", linkedBatch.CreatedAtUtc, 15000, 2000, 150);
        var unrelated = ReportingBrowserActivity(owner.Id, "Unrelated ride", linkedBatch.CreatedAtUtc, 5000, 900, 0);
        var transferred = ReportingBrowserActivity(goals.SecondOwner, "Transferred out of this import owner", linkedBatch.CreatedAtUtc, 5000, 900, 0);
        var route = BrowserRoute(owner.Id, "Linked route", TestSupport.Track(3));
        var unlinkedRoute = BrowserRoute(owner.Id, "Unrelated route", TestSupport.Track(3));
        await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
        {
            db.AddRange(owner, first, second, unrelated, transferred, route, unlinkedRoute);
            db.ImportBatches.AddRange(imported);
            db.SourceFiles.AddRange(Source(first.Id, null, owner.Id, 1), Source(first.Id, null, owner.Id, 2),
                Source(second.Id, null, owner.Id, 3), Source(null, route.Id, owner.Id, 4),
                Source(unrelated.Id, null, owner.Id, 5, imported[21].Id),
                Source(transferred.Id, null, goals.SecondOwner, 6));
            await db.SaveChangesAsync();
        }
        var service = GoalBrowserService(dataRoot);
        var historic = await service.CreateAsync(new(goals.Owner, new(GoalMetric.Distance, GoalRecurrence.Weekly, new(2021, 1, 4), null),
            new("Historic endurance", SportKind.Running, 1000)));
        var detail = (await service.GetDetailAsync(historic))!;
        await service.EditAsync(new(goals.Owner, historic, detail.Goal.MutationVersion, detail.EditableEdition!.Key,
            GoalEditScope.ThisEdition, new("Renamed endurance", SportKind.Running, 1000), detail.EditableEdition.State));
        await service.CreateAsync(new(goals.Owner, new(GoalMetric.ActivityCount, GoalRecurrence.Once, goals.Today.AddDays(-10), goals.Today.AddDays(10)),
            new("Reached outings", SportKind.Cycling, 1)));
        var archived = await service.CreateAsync(new(goals.Owner, new(GoalMetric.Distance, GoalRecurrence.Once, goals.Today.AddDays(10), goals.Today.AddDays(20)),
            new("Archived before beginning", null, 10000)));
        var upcoming = (await service.GetDetailAsync(archived))!;
        await service.ArchiveAsync(new(goals.Owner, archived, upcoming.Goal.MutationVersion, upcoming.EditableEdition!.Key, upcoming.EditableEdition.State));
        var currentMonday = goals.Today.AddDays(-(((int)goals.Today.DayOfWeek + 6) % 7));
        var expectedPage = (currentMonday.DayNumber - new DateOnly(2022, 1, 31).DayNumber) / 7 / GoalDetail.PageSize + 1;
        return new(owner.Id, goals.Owner, goals.SecondOwner, goals.EmptyOwner, linkedBatch.Id, imported[21].Id, [first.Id, second.Id], route.Id,
            historic, archived, expectedPage, goals.Today);

        ImportBatch Batch(string name, DateTimeOffset created) => new()
        {
            OwnerId = owner.Id,
            DisplayName = name,
            CreatedAtUtc = created,
            CompletedAtUtc = created.AddMinutes(1),
            SourceKind = SourceKind.Gpx,
            Kind = ImportBatchKind.FileImport,
            Status = ImportStatus.CompletedWithWarnings,
            Warnings = 2,
            FilesDiscovered = 1,
            Summary = "Fictional imported recording with warnings."
        };

        SourceFile Source(Guid? activity, Guid? path, Guid sourceOwner, int index, Guid? batch = null) => new()
        {
            OwnerId = sourceOwner,
            ImportBatchId = batch ?? linkedBatch.Id,
            ActivityId = activity,
            RouteId = path,
            SourceKind = SourceKind.Gpx,
            OriginalName = $"synthetic-linked-{index}.gpx",
            StoredPath = $"originals/synthetic-linked-{index}.gpx",
            Sha256 = index.ToString("x64", CultureInfo.InvariantCulture),
            Length = 100
        };
    }

    private sealed record FindingBrowserSeed(Guid ImportOwner, Guid GoalOwner, Guid SecondOwner, Guid EmptyOwner,
        Guid LinkedBatch, Guid SecondLinkedBatch, IReadOnlyList<Guid> LinkedActivities, Guid LinkedRoute, Guid HistoryGoal, Guid ArchivedBeforeStartGoal,
        int ExpectedHistoryPage, DateOnly Today);
}
