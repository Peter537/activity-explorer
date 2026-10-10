using System.Globalization;
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
    public async Task Goals_preview_fixture_is_created_only_when_explicitly_requested()
    {
        var requestedPath = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_GOALS_PREVIEW_DATA");
        if (string.IsNullOrWhiteSpace(requestedPath)) return;
        var path = Path.GetFullPath(requestedPath);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException("The goals preview fixture requires an empty directory.");
        Directory.CreateDirectory(path);
        var seed = await SeedGoalsBrowserDataAsync(path);
        await File.WriteAllTextAsync(Path.Combine(path, "synthetic-fixture.json"), JsonSerializer.Serialize(seed));
        TestContext.Current.TestOutputHelper!.WriteLine($"Synthetic goals preview data: {path}");
    }

    [Fact]
    public async Task Goals_create_all_metrics_and_preserve_current_and_scheduled_edits_with_stale_recovery()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedGoalsBrowserDataAsync(dataRoot);
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
            await page.GotoAsync(origin + "/goals", navigation);
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.Owner.ToString());
            var metrics = new[]
            {
                (Label: "Distance", Metric: GoalMetric.Distance, Target: "12.5", Canonical: 12500d, Repeats: "Weekly"),
                (Label: "Moving time", Metric: GoalMetric.MovingTime, Target: "2.5", Canonical: 9000d, Repeats: "Monthly"),
                (Label: "Ascent", Metric: GoalMetric.Ascent, Target: "350", Canonical: 350d, Repeats: "Yearly"),
                (Label: "Activities", Metric: GoalMetric.ActivityCount, Target: "4", Canonical: 4d, Repeats: "One-off"),
                (Label: "Active days", Metric: GoalMetric.ActiveDays, Target: "3", Canonical: 3d, Repeats: "One-off")
            };
            foreach (var metric in metrics)
            {
                await page.GotoAsync(origin + "/goals/new", navigation);
                if (metric.Metric == GoalMetric.Distance)
                    await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Goal owner", Exact = true })).ToHaveValueAsync("");
                await page.GetByRole(AriaRole.Combobox, new() { Name = "Goal owner", Exact = true }).SelectOptionAsync(seed.Owner.ToString());
                await page.GetByLabel("Goal name", new() { Exact = true }).FillAsync($"Created {metric.Label}");
                await page.GetByRole(AriaRole.Combobox, new() { Name = "Metric", Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = metric.Label });
                await page.GetByRole(AriaRole.Combobox, new() { Name = "Sport", Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Cycling" });
                await page.GetByRole(AriaRole.Combobox, new() { Name = "Repeats", Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = metric.Repeats });
                await page.GetByLabel("Start date", new() { Exact = true }).FillAsync(GoalBrowserDate(seed.Today));
                if (metric.Repeats == "One-off")
                    await page.GetByLabel("End date", new() { Exact = true }).FillAsync(GoalBrowserDate(seed.Today.AddDays(6)));
                await page.GetByLabel("Target", new() { Exact = true }).FillAsync(metric.Target);
                if (metric.Metric == GoalMetric.Distance)
                {
                    await page.GetByLabel("Target", new() { Exact = true }).FillAsync("0");
                    await page.GetByRole(AriaRole.Button, new() { Name = "Create goal", Exact = true }).ClickAsync();
                    await Assertions.Expect(page.GetByRole(AriaRole.Alert).First).ToContainTextAsync("greater than zero");
                    await Assertions.Expect(page.GetByLabel("Goal name", new() { Exact = true })).ToHaveValueAsync("Created Distance");
                    await page.GetByLabel("Target", new() { Exact = true }).FillAsync(metric.Target);
                    await page.GetByLabel("Goal name", new() { Exact = true }).FocusAsync();
                    await page.Keyboard.PressAsync("Tab");
                    await page.Keyboard.PressAsync("Shift+Tab");
                    await Assertions.Expect(page.GetByLabel("Goal name", new() { Exact = true })).ToBeFocusedAsync();
                    await AssertGoalsResponsiveAsync(page, "new-goal");
                }
                await page.GetByRole(AriaRole.Button, new() { Name = "Create goal", Exact = true }).ClickAsync();
                await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Created {metric.Label}", Exact = true })).ToBeVisibleAsync();
                var createdId = Guid.Parse(new Uri(page.Url).AbsolutePath.Split('/')[2]);
                var detail = await GoalBrowserService(dataRoot).GetDetailAsync(createdId);
                Assert.NotNull(detail);
                Assert.Equal(seed.Owner, detail.Goal.OwnerId);
                Assert.Equal(metric.Metric, detail.Goal.Schedule.Metric);
                Assert.Equal(metric.Canonical, detail.Goal.Definition.Target);
                Assert.Equal(SportKind.Cycling, detail.Goal.Definition.Sport);
            }

            await AssertUpcomingGoalEditingAndArchiveAsync(page, origin, dataRoot, seed);
            await page.GotoAsync(origin + $"/goals/{seed.WeeklyGoal}", navigation);
            await page.GetByRole(AriaRole.Button, new() { Name = "Edit next editions", Exact = true }).ClickAsync();
            await page.GetByLabel("Goal name", new() { Exact = true }).FillAsync("Scheduled weekly riding");
            await page.GetByLabel("Target", new() { Exact = true }).FillAsync("95");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save goal", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Edit this edition", Exact = true })).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Edit this edition", Exact = true }).ClickAsync();
            await page.GetByLabel("Goal name", new() { Exact = true }).FillAsync("This week's riding");
            await page.GetByLabel("Target", new() { Exact = true }).FillAsync("45");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save goal", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "This week's riding", Exact = true })).ToBeVisibleAsync();
            var service = GoalBrowserService(dataRoot);
            var saved = await service.GetDetailAsync(seed.WeeklyGoal);
            Assert.NotNull(saved);
            Assert.Equal(45000, saved.Edition!.Definition.Target);
            Assert.Equal(95000, saved.NextDefinition!.Target);
            Assert.Equal("Scheduled weekly riding", saved.NextDefinition.Name);
            await page.GetByRole(AriaRole.Button, new() { Name = "Edit this edition", Exact = true }).ClickAsync();
            await page.GetByLabel("Goal name", new() { Exact = true }).FillAsync("Retain this unsaved name");
            await service.EditAsync(new(seed.Owner, seed.WeeklyGoal, saved.Goal.MutationVersion,
                saved.EditableEdition!.Key, GoalEditScope.ThisEdition, new("Concurrent edit", SportKind.Cycling, 50000), saved.EditableEdition.State));
            await page.GetByRole(AriaRole.Button, new() { Name = "Save goal", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Alert).First).ToContainTextAsync(new Regex("changed|reload|stale", RegexOptions.IgnoreCase));
            await Assertions.Expect(page.GetByLabel("Goal name", new() { Exact = true })).ToHaveValueAsync("Retain this unsaved name");
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Concurrent edit", Exact = true })).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Archive goal", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Cancel archive", Exact = true }).ClickAsync();
            Assert.False((await service.GetDetailAsync(seed.WeeklyGoal))!.Goal.Archived);
            await page.GetByRole(AriaRole.Button, new() { Name = "Archive goal", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Stop future editions", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Archive goal", Exact = true })).ToHaveCountAsync(0);
            var archived = await service.GetDetailAsync(seed.WeeklyGoal);
            Assert.True(archived!.Goal.Archived);
            Assert.NotNull(archived.Edition);
            Assert.Null(archived.NextEditionStart);
            await page.GotoAsync(origin + "/goals", navigation);
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Goal view", Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Archived" });
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Concurrent edit", Exact = true })).ToBeVisibleAsync();
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
    public async Task Goals_history_contributors_profiles_dashboard_and_retry_preserve_truth_and_navigation()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var dataRoot = TestSupport.NewDirectory();
        var seed = await SeedGoalsBrowserDataAsync(dataRoot);
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
            await page.GotoAsync(origin + "/goals", navigation);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Goals", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Western outings", Exact = true })).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Goal view", Exact = true }).SelectOptionAsync("all");
            await Assertions.Expect(page).ToHaveURLAsync(origin + "/goals?view=all");
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Earlier distance block", Exact = true })).ToBeVisibleAsync();
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Goal view", Exact = true })).ToHaveValueAsync("all");
            await page.GoBackAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Goal view", Exact = true })).ToHaveValueAsync("current");
            await page.GoForwardAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Goal view", Exact = true })).ToHaveValueAsync("all");
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Goal view", Exact = true }).SelectOptionAsync("current");
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.Owner.ToString());
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Western outings", Exact = true })).ToHaveCountAsync(0);
            await AssertGoalsResponsiveAsync(page, "goals");
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.EmptyOwner.ToString());
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Weekly riding", Exact = true })).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "New goal", Exact = true })).ToBeVisibleAsync();
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.Owner.ToString());
            await page.GotoAsync(origin + $"/goals/{seed.ActiveDaysGoal}", navigation);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Everyday movement", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Contributing activities", Exact = true })).ToBeVisibleAsync();
            var contributions = page.GetByRole(AriaRole.Region, new() { Name = "Contributing activities", Exact = true });
            await Assertions.Expect(contributions.GetByRole(AriaRole.Heading, new() { Level = 3 })).ToHaveCountAsync(20);
            await Assertions.Expect(contributions.GetByRole(AriaRole.Link, new() { Name = "Same-day recovery walk", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(contributions.GetByText("1 active day", new() { Exact = true })).ToHaveCountAsync(20);
            await Assertions.Expect(page.Locator(".goal-amounts strong").First).ToHaveTextAsync("38 days");
            await Assertions.Expect(page.Locator("progress").First).ToHaveAttributeAsync("value", "100");
            await Assertions.Expect(page.Locator("progress").First).ToHaveAttributeAsync("aria-label", new Regex("108[.,]57 percent"));
            await AssertGoalsResponsiveAsync(page, "goal-contributors");
            await page.GetByRole(AriaRole.Link, new() { Name = "Next activities", Exact = true }).TapAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]contributionPage=2(?:&|$)"));
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Goal ride 02", Exact = true })).ToBeVisibleAsync();
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Goal ride 02", Exact = true })).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "Goal ride 02", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Goal ride 02", Exact = true })).ToBeVisibleAsync();
            await page.GoBackAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]contributionPage=2(?:&|$)"));
            await page.GoForwardAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Goal ride 02", Exact = true })).ToBeVisibleAsync();
            var detail = await GoalBrowserService(dataRoot).GetDetailAsync(seed.WeeklyGoal, historyPage: 2);
            Assert.NotNull(detail);
            Assert.True(detail.HistoryCount > GoalDetail.PageSize);
            var historical = detail.History[^1];
            await page.GotoAsync(origin + $"/goals/{seed.WeeklyGoal}", navigation);
            await page.GetByRole(AriaRole.Link, new() { Name = "Next editions", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]historyPage=2(?:&|$)"));
            await page.Locator(".goal-history tbody tr").Last.GetByRole(AriaRole.Link).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"[?&]edition={GoalBrowserDate(historical.Key)}(?:&|$)"));
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Edition history", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".goal-status span").First).ToContainTextAsync("Ended");
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Edit this edition", Exact = true })).ToHaveCountAsync(0);
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]historyPage=2(?:&|$)"));
            await AssertGoalsResponsiveAsync(page, "goal-history");

            await page.GotoAsync(origin + "/?period=custom&from=2001-01-01&to=2001-01-01&sport=rowing", navigation);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Current goals", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Weekly riding", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Region, new() { Name = "Current goals", Exact = true }).Locator("article")).ToHaveCountAsync(6);
            await AssertGoalsResponsiveAsync(page, "dashboard-goals");
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.SecondOwner.ToString());
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Western outings", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Weekly riding", Exact = true })).ToHaveCountAsync(0);
            await page.GetByLabel("Profile selector").SelectOptionAsync(seed.Owner.ToString());
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
                await db.Owners.Where(owner => owner.Id == seed.Owner)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(owner => owner.TimeZoneId, "Unavailable/Goals"));
            await page.GotoAsync(origin + $"/goals/{seed.ActiveDaysGoal}", navigation);
            await Assertions.Expect(page.GetByRole(AriaRole.Alert).First).ToContainTextAsync("timezone");
            await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
                await db.Owners.Where(owner => owner.Id == seed.Owner)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(owner => owner.TimeZoneId, "UTC"));
            await page.GetByRole(AriaRole.Button, new() { Name = "Try again", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Everyday movement", Exact = true })).ToBeVisibleAsync();
            await page.GotoAsync(origin + $"/goals/{Guid.NewGuid()}", navigation);
            await Assertions.Expect(page.GetByText(new Regex("goal.*(unavailable|not found)", RegexOptions.IgnoreCase)).First).ToBeVisibleAsync();
            Assert.Empty(errors);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }

    private static async Task AssertUpcomingGoalEditingAndArchiveAsync(IPage page, string origin, string dataRoot, GoalsBrowserSeed seed)
    {
        var service = GoalBrowserService(dataRoot);
        var upcoming = await service.GetDetailAsync(seed.UpcomingGoal);
        Assert.NotNull(upcoming);
        Assert.Equal(GoalPeriodState.Upcoming, upcoming.EditableEdition!.State);
        var actualStart = upcoming.EditableEdition.Start.ToString("dd MMM yyyy", CultureInfo.CurrentCulture);
        await page.GotoAsync(origin + $"/goals/{seed.UpcomingGoal}", new() { WaitUntil = WaitUntilState.NetworkIdle });
        var editInitial = page.GetByRole(AriaRole.Button, new() { Name = "Edit initial definition", Exact = true });
        await editInitial.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.GetByRole(AriaRole.Region, new() { Name = "Edit goal", Exact = true }).Locator("p"))
            .ToContainTextAsync($"Applies from {actualStart} and to following editions.");
        await page.GetByLabel("Target", new() { Exact = true }).FillAsync("325");
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel edit", Exact = true }).ClickAsync();
        await Assertions.Expect(editInitial).ToBeFocusedAsync();
        Assert.Equal(300000, (await service.GetDetailAsync(seed.UpcomingGoal))!.NextDefinition!.Target);
        await editInitial.ClickAsync();
        await page.GetByLabel("Target", new() { Exact = true }).FillAsync("325");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save goal", Exact = true }).ClickAsync();
        await Assertions.Expect(editInitial).ToBeVisibleAsync();
        var saved = await service.GetDetailAsync(seed.UpcomingGoal);
        Assert.NotNull(saved);
        Assert.Equal(325000, saved.Edition!.Definition.Target);
        Assert.Equal(325000, saved.NextDefinition!.Target);

        var archive = page.GetByRole(AriaRole.Button, new() { Name = "Archive goal", Exact = true });
        await archive.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Region, new() { Name = "Archive goal confirmation", Exact = true }))
            .ToContainTextAsync("The first edition will not begin.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel archive", Exact = true }).ClickAsync();
        await Assertions.Expect(archive).ToBeFocusedAsync();
        await archive.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Stop future editions", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("This goal was archived before its first edition began.", new() { Exact = true })).ToBeVisibleAsync();
        var archived = await service.GetDetailAsync(seed.UpcomingGoal);
        Assert.True(archived!.Goal.Archived);
        Assert.Null(archived.Edition);
        Assert.Null(archived.NextDefinition);
        Assert.Equal(0, archived.HistoryCount);
        await Assertions.Expect(editInitial).ToHaveCountAsync(0);
    }

    private static async Task AssertGoalsResponsiveAsync(IPage page, string name)
    {
        var captures = Path.Combine(FindRepositoryRoot(), "artifacts", "goals-ui");
        Directory.CreateDirectory(captures);
        foreach (var width in new[] { 320, 360, 375, 768, 1121, 1280, 1920 })
        {
            await page.SetViewportSizeAsync(width, 1000);
            await page.Locator(".sidebar").EvaluateAsync("element => Promise.all(element.getAnimations().map(animation => animation.finished))");
            await AssertNoDocumentOverflowAsync(page, $"{name} at {width}px");
            if (width is 375 or 1280)
                await page.ScreenshotAsync(new() { Path = Path.Combine(captures, $"{name}-{width}.png"), FullPage = true });
        }
        await page.SetViewportSizeAsync(375, 1000);
        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        await page.EvaluateAsync("document.documentElement.style.fontSize='200%'; document.body.style.fontSize='30px'");
        await AssertNoDocumentOverflowAsync(page, $"{name} with enlarged text");
        await page.EvaluateAsync("document.documentElement.style.removeProperty('font-size'); document.body.style.removeProperty('font-size')");
        await page.SetViewportSizeAsync(1280, 1000);
    }

    private static async Task<GoalsBrowserSeed> SeedGoalsBrowserDataAsync(string dataRoot)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var owner = new OwnerProfile { DisplayName = "Goals athlete", TimeZoneId = "UTC" };
        var secondOwner = new OwnerProfile { DisplayName = "Western goals athlete", TimeZoneId = "America/Los_Angeles" };
        var emptyOwner = new OwnerProfile { DisplayName = "Empty goals athlete", TimeZoneId = "UTC" };
        await using (var db = new ExplorerDbContext(ReportingBrowserOptions(dataRoot)))
        {
            await db.Database.EnsureCreatedAsync();
            db.AddRange(owner, secondOwner, emptyOwner);
            for (var index = 0; index < 38; index++)
            {
                var start = new DateTimeOffset(today.AddDays(index - 36).ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
                db.Add(ReportingBrowserActivity(owner.Id, $"Goal ride {index + 1:00}", start, 12000, 3600, 120));
                if (index == 36)
                {
                    var walk = ReportingBrowserActivity(owner.Id, "Same-day recovery walk", start.AddHours(2), 2000, 600, 0);
                    walk.Sport = SportKind.Walking;
                    db.Add(walk);
                }
            }
            db.Add(ReportingBrowserActivity(secondOwner.Id, "Western goal ride",
                new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero), 25000, 3600, 200));
            await db.SaveChangesAsync();
        }
        var service = GoalBrowserService(dataRoot);
        var weekly = await service.CreateAsync(new(owner.Id, new(GoalMetric.Distance, GoalRecurrence.Weekly, today.AddDays(-175), null), new("Weekly riding", SportKind.Cycling, 100000)));
        var days = await service.CreateAsync(new(owner.Id, new(GoalMetric.ActiveDays, GoalRecurrence.Once, today.AddDays(-36), today.AddDays(7)), new("Everyday movement", null, 35)));
        await service.CreateAsync(new(owner.Id, new(GoalMetric.MovingTime, GoalRecurrence.Monthly, today.AddMonths(-3), null), new("Monthly movement", null, 90000)));
        await service.CreateAsync(new(owner.Id, new(GoalMetric.Ascent, GoalRecurrence.Yearly, today.AddYears(-1), null), new("Yearly climbing", SportKind.Cycling, 10000)));
        await service.CreateAsync(new(owner.Id, new(GoalMetric.ActivityCount, GoalRecurrence.Yearly, today.AddYears(-1), null), new("Annual outings", null, 100)));
        await service.CreateAsync(new(owner.Id, new(GoalMetric.Distance, GoalRecurrence.Once, today.AddDays(-10), today.AddDays(10)), new("A little further with a deliberately long goal name", null, 200000)));
        var upcoming = await service.CreateAsync(new(owner.Id, new(GoalMetric.Distance, GoalRecurrence.Monthly, today.AddMonths(1), null), new("Next month's distance", null, 300000)));
        await service.CreateAsync(new(owner.Id, new(GoalMetric.Distance, GoalRecurrence.Once, today.AddDays(-90), today.AddDays(-40)), new("Earlier distance block", null, 100000)));
        await service.CreateAsync(new(secondOwner.Id, new(GoalMetric.ActivityCount, GoalRecurrence.Monthly, today.AddMonths(-1), null), new("Western outings", null, 12)));
        return new(owner.Id, secondOwner.Id, emptyOwner.Id, weekly, days, upcoming, today);
    }

    private static GoalService GoalBrowserService(string dataRoot) => new(
        new GoalBrowserDbFactory(ReportingBrowserOptions(dataRoot)), new OwnerMutationLock(), TimeProvider.System);

    private static string GoalBrowserDate(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class GoalBrowserDbFactory(DbContextOptions<ExplorerDbContext> options) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);
    }

    private sealed record GoalsBrowserSeed(Guid Owner, Guid SecondOwner, Guid EmptyOwner, Guid WeeklyGoal, Guid ActiveDaysGoal, Guid UpcomingGoal, DateOnly Today);
}
