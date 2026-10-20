using System.Text.Json;
using System.Data.Common;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Mcp;
using ActivityExplorer.Mcp.Configuration;
using ActivityExplorer.Mcp.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;

namespace ActivityExplorer.Tests;

public sealed class McpReadOnlyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Readers_preserve_independent_totals_and_reject_writes_in_both_journal_modes(bool wal)
    {
        await using var fixture = await McpFixture.CreateAsync(wal);
        using var host = fixture.BuildHost();
        await host.Services.GetRequiredService<ReadOnlyDatabaseValidator>().ValidateAsync();
        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), service => service.GetType().Namespace?.StartsWith("ActivityExplorer.Infrastructure", StringComparison.Ordinal) == true);
        Assert.Null(host.Services.GetService<IImportQueue>());
        Assert.Null(host.Services.GetService<IFileOperationCoordinator>());
        var factory = host.Services.GetRequiredService<IDbContextFactory<ExplorerDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE Activities SET Title = 'Forbidden'"));
        }
        var activities = host.Services.GetRequiredService<IActivityReadService>();
        var search = await activities.SearchAsync(new(OwnerId: fixture.Owner, PageSize: 1));
        Assert.Single(search.Items);
        Assert.Equal(2, search.Total);
        Assert.Equal(3000, search.Totals.DistanceMeters);
        Assert.Equal(900, search.Totals.MovingSeconds);
        var filtered = await activities.SearchAsync(new(OwnerId: fixture.Owner, Criteria: new(Search: "æBLE", TextMode: ActivityTextMode.Words)));
        Assert.Equal(fixture.Activity, Assert.Single(filtered.Items).Id);
        Assert.Null(await activities.GetDetailAsync(fixture.Owner, fixture.OtherActivity));
        var detail = await activities.GetDetailAsync(fixture.Owner, fixture.Activity);
        Assert.NotNull(detail);
        Assert.Equal(1000, detail.Summary.DistanceMeters);
        Assert.Equal(fixture.TagId, Assert.Single(detail.Summary.Tags).Id);
        var dashboard = await activities.GetBoundedDashboardAsync(new(OwnerId: fixture.Owner, Period: new(ReportingPreset.Custom, new(2026, 1, 1), new(2026, 1, 31))), 120);
        Assert.Equal(3000, dashboard.Totals.DistanceMeters);
        var calendar = await activities.GetTrainingCalendarAsync(new(OwnerId: fixture.Owner, Month: new(2026, 1, 1)));
        Assert.Equal(2, calendar.Totals.ActivityCount);
        Assert.Equal(3000, calendar.Totals.DistanceMeters);
        Assert.Single((await activities.ListFilterValuesAsync(fixture.Owner, "tag", null, new())).Items);
        Assert.Equal("Device A", Assert.Single((await activities.ListFilterValuesAsync(fixture.Owner, "device", null, new())).Items).Value);
        Assert.Equal("Bike", Assert.Single((await activities.ListFilterValuesAsync(fixture.Owner, "gear", null, new())).Items).Value);
        await using var writer = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(ImportStatus.Running, (await writer.ImportBatches.SingleAsync()).Status);
        Assert.Equal(FileOperationState.Pending, (await writer.FileOperations.SingleAsync()).State);
        Assert.Empty(System.IO.Directory.GetDirectories(fixture.DirectoryPath));
    }

    [Fact]
    public async Task Bounded_route_segment_and_record_readers_preserve_results_without_repairing_data()
    {
        await using var fixture = await McpFixture.CreateAsync();
        using var host = fixture.BuildHost();
        var segments = host.Services.GetRequiredService<ISegmentReadService>();
        Assert.Equal(fixture.Segment, Assert.Single((await segments.SearchAsync(fixture.Owner, "saved", SportKind.Cycling, "name-asc", new())).Items).Id);
        Assert.NotNull(await segments.GetAsync(fixture.Owner, fixture.Segment));
        Assert.Null(await segments.GetAsync(fixture.OtherOwner, fixture.Segment));
        var efforts = await segments.GetEffortsAsync(fixture.Owner, fixture.Segment, new(), new(1, 1));
        Assert.NotNull(efforts);
        Assert.Single(efforts.Items);
        Assert.Equal(2, efforts.Total);
        Assert.Equal(2, fixture.Efforts.Length);
        var comparison = await segments.CompareAsync(fixture.Owner, fixture.Segment, fixture.Efforts[0], fixture.Efforts[1]);
        Assert.NotNull(comparison);
        Assert.Equal(SegmentComparisonUnavailableReason.None, comparison.UnavailableReason);
        Assert.InRange(comparison.Samples.Count, 2, 100);
        Assert.Equal(59, comparison.DeltaSeconds, 6);
        Assert.Equal(comparison.DeltaSeconds, comparison.Samples[^1].DeltaSeconds, 6);
        Assert.Null(await segments.CompareAsync(fixture.OtherOwner, fixture.Segment, fixture.Efforts[0], fixture.Efforts[1]));
        var routes = host.Services.GetRequiredService<IRouteReadService>();
        Assert.Equal(fixture.Route, Assert.Single((await routes.SearchAsync(new(fixture.Owner), new ReadPage())).Items).Id);
        Assert.Equal(1000, (await routes.GetAsync(fixture.Owner, fixture.Route))!.Summary.DistanceMeters);
        Assert.Null(await routes.GetAsync(fixture.OtherOwner, fixture.Route));
        var statistics = host.Services.GetRequiredService<IStatisticsReadService>();
        var records = await statistics.ReadOverviewAsync(new(fixture.Owner, SportKind.Cycling));
        Assert.True(records.Available);
        Assert.Equal(2000, Assert.Single(records.Overview.Records, record => record.Kind == RecordKind.Distance).Value);
        var period = new ReportingDateSelection(ReportingPreset.Custom, new(2026, 1, 1), new(2026, 1, 31));
        var calculated = await statistics.ReadOverviewAsync(new(fixture.Owner, SportKind.Cycling, Period: period));
        Assert.True(calculated.Available);
        Assert.Equal("calculated", calculated.Source);
        var attempts = await statistics.GetAttemptsAsync(new(SportKind.Cycling, RecordKind.Distance, "Longest distance", fixture.Owner, Period: period));
        Assert.NotNull(attempts);
        Assert.Equal(2000, attempts.Attempts.Items[0].Value);
        await using (var writer = await fixture.Factory.CreateDbContextAsync()) await writer.StatisticSnapshots.ExecuteDeleteAsync();
        Assert.False((await statistics.ReadOverviewAsync(new(fixture.Owner, SportKind.Cycling))).Available);
        await using var check = await fixture.Factory.CreateDbContextAsync();
        Assert.Empty(await check.StatisticSnapshots.ToArrayAsync());
    }

    [Fact]
    public async Task Tool_policy_enforces_profiles_limits_timeout_cancellation_and_error_contracts()
    {
        await using var fixture = await McpFixture.CreateAsync();
        using var host = fixture.BuildHost("--Mcp:AllowedProfileIds:0", fixture.Owner.ToString(), "--Mcp:RequestTimeoutSeconds", "1", "--Mcp:MaxResponseBytes", "1024");
        var tools = ActivatorUtilities.CreateInstance<ActivityTools>(host.Services);
        var page = Data(await tools.ListProfiles(default));
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal("not_found", Error(await tools.GetActivity(fixture.OtherOwner, fixture.OtherActivity, default)));
        Assert.Equal("not_found", Error(await tools.GetActivity(fixture.Owner, fixture.OtherActivity, default)));
        Assert.Equal("invalid_argument", Error(await tools.SearchActivities(fixture.Owner, default, page: 0)));
        var policy = host.Services.GetRequiredService<ToolExecutionPolicy>();
        Assert.Equal("read_only", Error(await policy.RunAsync<object>("create_segment", fixture.Owner, (_, _) => throw new InvalidOperationException(), default)));
        Assert.Equal("response_too_large", Error(await policy.RunAsync<string>("get_activity", fixture.Owner, (_, _) => Task.FromResult<string?>(new string('x', 3000)), default)));
        Assert.Equal("timeout", Error(await policy.RunAsync<object>("get_activity", fixture.Owner, async (_, token) => { await Task.Delay(5000, token); return new(); }, default)));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.RunAsync<object>("get_activity", fixture.Owner, (_, _) => Task.FromResult<object?>(new()), cancellation.Token));
        Assert.False((await tools.GetServerStatus(default)).IsError);
    }

    [Theory]
    [InlineData("Mcp:AccessMode", "ReadWrite")]
    [InlineData("Mcp:AllowedProfileIds", "")]
    [InlineData("Mcp:Unknown", "1")]
    [InlineData("Mcp:MaxConcurrentCalls", "0")]
    [InlineData("Mcp:AllowedProfileIds:0", "00000000-0000-0000-0000-000000000000")]
    public void Invalid_configuration_fails_closed(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        Assert.Throws<ArgumentException>(() => McpOptions.Read(configuration));
    }

    [Fact]
    public async Task All_tool_adapters_return_typed_data_and_validate_semantic_limits()
    {
        await using var fixture = await McpFixture.CreateAsync();
        using var host = fixture.BuildHost();
        var activities = ActivatorUtilities.CreateInstance<ActivityTools>(host.Services);
        var reporting = ActivatorUtilities.CreateInstance<ReportingTools>(host.Services);
        var segments = ActivatorUtilities.CreateInstance<SegmentTools>(host.Services);
        var routes = ActivatorUtilities.CreateInstance<RouteTools>(host.Services);
        var dates = new Mcp.Models.DateSelection("custom", new(2026, 1, 1), new(2026, 1, 31));
        var results = new[]
        {
            await activities.GetServerStatus(default), await activities.ListProfiles(default),
            await activities.ListActivityFilterValues(fixture.Owner, "tag", default),
            await activities.SearchActivities(fixture.Owner, default), await activities.GetActivity(fixture.Owner, fixture.Activity, default),
            await reporting.GetDashboard(fixture.Owner, default, dates: dates),
            await reporting.GetTrainingCalendar(fixture.Owner, default, month: new(2026, 1, 1)),
            await reporting.GetRecords(fixture.Owner, SportKind.Cycling, default),
            await reporting.GetRecordAttempts(fixture.Owner, SportKind.Cycling, RecordKind.Distance, "Longest distance", default),
            await segments.SearchSegments(fixture.Owner, default), await segments.GetSegment(fixture.Owner, fixture.Segment, default),
            await segments.ListSegmentEfforts(fixture.Owner, fixture.Segment, default),
            await segments.CompareSegmentEfforts(fixture.Owner, fixture.Segment, fixture.Efforts[0], fixture.Efforts[1], default),
            await routes.SearchRoutes(fixture.Owner, default), await routes.GetRoute(fixture.Owner, fixture.Route, default)
        };
        Assert.All(results, result => Assert.Equal(JsonValueKind.Object, Data(result).ValueKind));
        Assert.Equal(3000, Data(results[3]).GetProperty("totals").GetProperty("distanceMeters").GetDouble());
        Assert.Equal("invalid_argument", Error(await activities.ListActivityFilterValues(fixture.Owner, "unknown", default)));
        Assert.Equal("invalid_argument", Error(await activities.SearchActivities(fixture.Owner, default, sort: "unknown")));
        Assert.Equal("invalid_argument", Error(await reporting.GetRecords(fixture.Owner, SportKind.Cycling, default, dates: new("unknown"))));
        Assert.Equal("invalid_argument", Error(await reporting.GetRecordAttempts(fixture.Owner, SportKind.Cycling, RecordKind.Distance, "unknown", default)));
        Assert.Equal("invalid_argument", Error(await reporting.GetDashboard(fixture.Owner, default, dates: new("custom", new(2000, 1, 1), new(2026, 1, 1)))));
        Assert.Equal("invalid_argument", Error(await segments.CompareSegmentEfforts(fixture.Owner, fixture.Segment, fixture.Efforts[0], fixture.Efforts[0], default)));
        Assert.Equal("invalid_argument", Error(await routes.SearchRoutes(fixture.Owner, default, sort: "unknown")));
    }

    [Fact]
    public async Task Live_web_writes_become_visible_only_after_commit_and_schema_validation_never_upgrades()
    {
        await using var fixture = await McpFixture.CreateAsync();
        using var host = fixture.BuildHost();
        var reader = host.Services.GetRequiredService<IActivityReadService>();
        await using (var writer = await fixture.Factory.CreateDbContextAsync())
        {
            await using var transaction = await writer.Database.BeginTransactionAsync();
            await writer.Activities.Where(activity => activity.Id == fixture.Activity).ExecuteUpdateAsync(setters => setters.SetProperty(activity => activity.DistanceMeters, 4000));
            Assert.Equal(3000, (await reader.SearchAsync(new(OwnerId: fixture.Owner))).Totals.DistanceMeters);
            await transaction.CommitAsync();
        }
        Assert.Equal(6000, (await reader.SearchAsync(new(OwnerId: fixture.Owner))).Totals.DistanceMeters);
        await using (var writer = await fixture.Factory.CreateDbContextAsync())
        {
            await writer.Database.ExecuteSqlRawAsync("ALTER TABLE Owners ADD COLUMN FutureColumn TEXT");
        }
        var validator = host.Services.GetRequiredService<ReadOnlyDatabaseValidator>();
        await validator.ValidateAsync();
        await using (var writer = await fixture.Factory.CreateDbContextAsync())
        {
            await writer.Database.ExecuteSqlRawAsync("ALTER TABLE Activities RENAME COLUMN Title TO IncompatibleTitle");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => validator.ValidateAsync());
        await using var check = await fixture.Factory.CreateDbContextAsync();
        var columns = await check.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Activities')").ToArrayAsync();
        Assert.Contains("IncompatibleTitle", columns);
        Assert.DoesNotContain("Title", columns);
    }

    [Fact]
    public async Task Summary_queries_exclude_track_and_geometry_payloads_and_transferred_records_do_not_leak()
    {
        await using var fixture = await McpFixture.CreateAsync();
        var commands = new ReadCommandCapture();
        var reader = new ActivityReadService(fixture.ReadFactory(commands));
        Assert.Equal(2, (await reader.SearchAsync(new(OwnerId: fixture.Owner))).Total);
        Assert.NotNull(await reader.GetDetailAsync(fixture.Owner, fixture.Activity));
        Assert.NotEmpty(commands.Commands);
        Assert.DoesNotContain(commands.Commands, command => command.Contains("CompressedPayload", StringComparison.Ordinal) ||
            command.Contains("GeometryWkb", StringComparison.Ordinal) || command.Contains("ActivityStreams", StringComparison.Ordinal));
        using var host = fixture.BuildHost();
        await using (var writer = await fixture.Factory.CreateDbContextAsync())
        {
            await writer.Activities.Where(activity => activity.OwnerId == fixture.Owner)
                .ExecuteUpdateAsync(setters => setters.SetProperty(activity => activity.OwnerId, fixture.OtherOwner));
        }
        var statistics = host.Services.GetRequiredService<IStatisticsReadService>();
        Assert.Empty(await statistics.GetRecordsAsync(fixture.Owner));
        Assert.False((await statistics.ReadOverviewAsync(new(fixture.Owner, SportKind.Cycling))).Available);
        var segments = host.Services.GetRequiredService<ISegmentReadService>();
        Assert.Empty((await segments.GetEffortsAsync(fixture.Owner, fixture.Segment, new(), new()))!.Items);
        Assert.Equal(0, (await segments.GetAsync(fixture.Owner, fixture.Segment))!.Summary.EffortCount);
    }

    private sealed class ReadCommandCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    internal static JsonElement Data(CallToolResult result)
    {
        Assert.False(result.IsError);
        Assert.NotNull(result.StructuredContent);
        return result.StructuredContent.Value.GetProperty("data");
    }

    internal static string Error(CallToolResult result)
    {
        Assert.True(result.IsError);
        return result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString()!;
    }
}
