using System.Data;
using System.Data.Common;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Activity = ActivityExplorer.Core.Domain.Activity;

namespace ActivityExplorer.Tests;

public sealed class DatabaseReadIndexTests
{
    private static readonly IReadOnlyDictionary<string, string[]> IndexColumns = new Dictionary<string, string[]>
    {
        ["IX_Activities_ScalarReadCover"] =
        [
            "OwnerId", "Sport", "StartTimeUtc", "Id", "Title", "Description", "DistanceMeters", "MovingTimeSeconds",
            "ElevationGainMeters", "IsIndoor", "HasGps", "HasPower", "AveragePowerWatts", "DeviceName", "GearName",
            "MutationVersion", "ExplorationInputVersion"
        ],
        ["IX_Activities_MapCandidates"] = ["OwnerId", "StartTimeUtc", "Id"],
        ["IX_Activities_PageKeys"] = ["OwnerId", "StartTimeUtc", "Id"],
        ["IX_Activities_ScopedKeysCover"] = ["OwnerId", "Id", "Sport", "StartTimeUtc", "Title", "ExplorationInputVersion"],
        ["IX_SourceFiles_ImportBatchOwnerTargets"] = ["ImportBatchId", "OwnerId", "ActivityId", "RouteId"],
        ["IX_ActivityExplorationIndexes_ReadCover"] = ["ActivityId", "InputVersion", "ComputationVersion", "IsLimited", "CellCount"],
        ["IX_Routes_LibraryCover"] = ["OwnerId", "Name", "Id", "Sport", "DistanceMeters", "ElevationGainMeters"]
    };

    [Fact]
    public async Task Fresh_model_and_additive_upgrade_provide_the_same_scalar_indexes_without_changing_rows()
    {
        var factory = await Database();
        await using var db = factory.CreateDbContext();
        foreach (var index in IndexColumns)
            Assert.Equal(index.Value, await ColumnsAsync(db, index.Key));
        var owner = new OwnerProfile { DisplayName = "Preserved index owner" };
        var batch = new ImportBatch { OwnerId = owner.Id, DisplayName = "Preserved import" };
        var activity = new Activity
        {
            OwnerId = owner.Id,
            Title = "Preserved activity",
            NaturalFingerprint = "read-index-upgrade",
            DistanceMeters = 5_900,
            GeometryWkb = [1, 2, 3],
            SimplifiedGeometryWkb = [4, 5, 6]
        };
        var source = new SourceFile { OwnerId = owner.Id, ImportBatchId = batch.Id, ActivityId = activity.Id, Sha256 = "preserved-source" };
        db.AddRange(owner, batch, activity, source);
        await db.SaveChangesAsync();
        var before = await db.Activities.AsNoTracking().SingleAsync();
        await db.Database.ExecuteSqlRawAsync("""
            DROP INDEX "IX_Activities_ScalarReadCover";
            DROP INDEX "IX_Activities_MapCandidates";
            DROP INDEX "IX_Activities_PageKeys";
            DROP INDEX "IX_Activities_ScopedKeysCover";
            DROP INDEX "IX_SourceFiles_ImportBatchOwnerTargets";
            DROP INDEX "IX_ActivityExplorationIndexes_ReadCover";
            DROP INDEX "IX_Routes_LibraryCover";
            """);
        await DatabaseInitializer.EnsureReadIndexesAsync(db);
        await DatabaseInitializer.EnsureReadIndexesAsync(db);
        foreach (var index in IndexColumns)
            Assert.Equal(index.Value, await ColumnsAsync(db, index.Key));
        var after = await db.Activities.AsNoTracking().SingleAsync();
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.OwnerId, after.OwnerId);
        Assert.Equal(before.Title, after.Title);
        Assert.Equal(before.DistanceMeters, after.DistanceMeters);
        Assert.Equal(before.GeometryWkb, after.GeometryWkb);
        Assert.Equal(before.SimplifiedGeometryWkb, after.SimplifiedGeometryWkb);
        var retained = await db.SourceFiles.AsNoTracking().SingleAsync();
        Assert.Equal(source.Id, retained.Id);
        Assert.Equal(activity.Id, retained.ActivityId);
        Assert.Null(retained.RouteId);
    }

    [Fact]
    public async Task Production_record_and_import_queries_use_covering_indexes_and_keep_current_owner_checks()
    {
        var capture = new ReadCapture();
        var factory = await Database(capture);
        var owner = new OwnerProfile { DisplayName = "Covered reader", TimeZoneId = "UTC" };
        var other = new OwnerProfile { DisplayName = "Other covered reader", TimeZoneId = "UTC" };
        var batch = new ImportBatch { OwnerId = owner.Id, DisplayName = "Covered import" };
        var rows = Enumerable.Range(0, 80).Select(index => new Activity
        {
            OwnerId = owner.Id,
            Title = $"Covered activity {index}",
            NaturalFingerprint = $"covered-{index}",
            Sport = SportKind.Cycling,
            StartTimeUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddDays(index),
            IsIndoor = index % 2 == 0,
            DistanceMeters = 5_900 + index,
            MovingTimeSeconds = 600,
            ElevationGainMeters = 50,
            GeometryWkb = new byte[8_192]
        }).ToArray();
        var foreign = new Activity { OwnerId = other.Id, Title = "Foreign target", NaturalFingerprint = "covered-foreign" };
        await using (var db = factory.CreateDbContext())
        {
            db.AddRange(owner, other, batch, foreign);
            db.Activities.AddRange(rows);
            db.SourceFiles.AddRange(Source(rows[0].Id), Source(rows[0].Id), Source(foreign.Id));
            await db.SaveChangesAsync();
        }
        capture.Reads.Clear();
        var statistics = new StatisticsReadService(factory);
        var history = (await statistics.GetHistoryAsync(new(SportKind.Cycling, RecordKind.Distance, "Longest distance", owner.Id, RecordScope.Indoor)))!;
        Assert.Equal(40, Assert.Single(history.Series).Points.Count);
        var summaryRead = capture.Reads.Single(read => read.Sql.Contains("\"DistanceMeters\"", StringComparison.Ordinal));
        await using var verification = factory.CreateDbContext();
        var summaryPlan = await ExplainAsync(verification, summaryRead);
        Assert.Contains(summaryPlan, detail => detail.Contains("USING COVERING INDEX IX_Activities_ScalarReadCover", StringComparison.Ordinal));
        capture.Reads.Clear();
        var imports = new ImportHistoryService(factory);
        var result = Assert.Single((await imports.ListAsync(owner.Id)).Items);
        Assert.Equal(1, result.LinkedActivityCount);
        var importRead = capture.Reads.Single(read => read.Sql.Contains("SELECT DISTINCT", StringComparison.Ordinal));
        var importPlan = await ExplainAsync(verification, importRead);
        Assert.Contains(importPlan, detail => detail.Contains("USING COVERING INDEX IX_SourceFiles_ImportBatchOwnerTargets", StringComparison.Ordinal));
        Assert.Contains(importPlan, detail => detail.Contains("USING COVERING INDEX IX_Activities_ScopedKeysCover", StringComparison.Ordinal));
        await verification.Activities.Where(activity => activity.Id == rows[0].Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(activity => activity.OwnerId, other.Id));
        Assert.Equal(0, Assert.Single((await imports.ListAsync(owner.Id)).Items).LinkedActivityCount);

        SourceFile Source(Guid activityId) => new()
        {
            OwnerId = owner.Id,
            ImportBatchId = batch.Id,
            ActivityId = activityId,
            OriginalName = "covered.fit",
            Sha256 = Guid.NewGuid().ToString("N")
        };
    }

    [Fact]
    public async Task Activity_page_reads_the_selected_keys_without_changing_stored_guid_casing()
    {
        var factory = await Database();
        var owner = new OwnerProfile { DisplayName = "Page key owner", TimeZoneId = "UTC" };
        var older = new Activity
        {
            OwnerId = owner.Id,
            NaturalFingerprint = "older-page-key",
            Title = "Older",
            StartTimeUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var newer = new Activity
        {
            Id = Guid.Parse("abcdef12-1234-5678-9abc-123456789abc"),
            OwnerId = owner.Id,
            NaturalFingerprint = "newer-page-key",
            Title = "Newer",
            StartTimeUtc = older.StartTimeUtc.AddDays(1)
        };
        await using (var db = factory.CreateDbContext())
        {
            db.AddRange(owner, older, newer);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "Activities" SET "Id" = lower("Id") WHERE "Id" = {newer.Id}
                """);
        }

        var service = new ActivityReadService(factory);
        var filter = new ActivityFilter(OwnerId: owner.Id, PageSize: 1);
        var first = await service.SearchAsync(filter);
        Assert.Equal(2, first.Total);
        Assert.Equal(newer.Id, Assert.Single(first.Items).Id);
        Assert.Equal(older.Id, Assert.Single((await service.SearchAsync(filter with { Page = 2 })).Items).Id);
    }

    private static async Task<string[]> ColumnsAsync(ExplorerDbContext db, string index)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA index_info(\"{index}\")";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(2));
        return columns.ToArray();
    }

    private static async Task<string[]> ExplainAsync(ExplorerDbContext db, ReadCommand read)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + read.Sql;
        foreach (var value in read.Values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = value.Name;
            parameter.Value = value.Value;
            parameter.DbType = value.Type;
            command.Parameters.Add(parameter);
        }
        await using var reader = await command.ExecuteReaderAsync();
        var details = new List<string>();
        while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        return details.ToArray();
    }

    private static async Task<Factory> Database(ReadCapture? capture = null)
    {
        var path = Path.Combine(TestSupport.NewDirectory(), "read-indexes.db");
        var options = new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite($"Data Source={path}");
        if (capture is not null) options.AddInterceptors(capture);
        var factory = new Factory(options.Options);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        return factory;
    }

    private sealed record ReadCommand(string Sql, (string Name, object? Value, DbType Type)[] Values);
    private sealed class ReadCapture : DbCommandInterceptor
    {
        public List<ReadCommand> Reads { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Reads.Add(new(command.CommandText, command.Parameters.Cast<DbParameter>().Select(parameter => (parameter.ParameterName, parameter.Value, parameter.DbType)).ToArray()));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Factory(DbContextOptions<ExplorerDbContext> options) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);
        public Task<ExplorerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
