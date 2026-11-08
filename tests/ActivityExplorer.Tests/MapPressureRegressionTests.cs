using System.Data.Common;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ActivityExplorer.Tests;

public sealed class MapPressureRegressionTests
{
    [Fact]
    public async Task Map_reads_use_a_deferred_snapshot_and_only_requested_geometry()
    {
        var path = Path.Combine(TestSupport.NewDirectory(), "map-pressure.db");
        var cs = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 1, Pooling = false }.ToString();
        var capture = new MapCapture();
        var factory = new Factory(new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite(cs).AddInterceptors(capture).Options);
        var owner = new OwnerProfile { DisplayName = "Map owner", TimeZoneId = "UTC" };
        var points = TestSupport.Track(3);
        var geometry = GeometryCodec.ToWkb(points)!;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL");
            db.Owners.Add(owner);
            db.Activities.Add(new Activity
            {
                OwnerId = owner.Id,
                Title = "Map ride",
                Sport = SportKind.Cycling,
                StartTimeUtc = points[0].Timestamp!.Value,
                NaturalFingerprint = "map-pressure",
                HasGps = true,
                SimplifiedGeometryWkb = geometry,
                GeometryWkb = geometry
            });
            await db.SaveChangesAsync();
        }
        capture.Enabled = true;
        var pending = new MapFeatureService(factory).GetActivitiesAsync(new MapQuery(OwnerId: owner.Id));
        Exception? writerError = null;
        try
        {
            await capture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // A deferred read snapshot must not reserve SQLite's single writer while it is paused.
            await using var connection = new SqliteConnection(cs);
            await connection.OpenAsync();
            await using var writer = connection.BeginTransaction(deferred: false);
            await writer.RollbackAsync();
        }
        catch (Exception exception) { writerError = exception; }
        finally { capture.Resume.TrySetResult(); }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(writerError);
        Assert.Single(result.Features);
        var read = Assert.Single(capture.Commands, sql => sql.Contains("SimplifiedGeometryWkb", StringComparison.Ordinal) && sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase) && !sql.Contains("COUNT(", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("\"GeometryWkb\"", read, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Description\"", read, StringComparison.Ordinal);
        Assert.Contains("INDEXED BY \"IX_Activities_MapCandidates\"", read, StringComparison.Ordinal);
        capture.Enabled = false;
        await using (var db = factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("DROP INDEX \"IX_Activities_MapCandidates\"");
        var legacy = await new MapFeatureService(factory).GetActivitiesAsync(new MapQuery(OwnerId: owner.Id));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(result), System.Text.Json.JsonSerializer.Serialize(legacy));
    }

    private sealed class Factory(DbContextOptions<ExplorerDbContext> options) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);
        public Task<ExplorerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class MapCapture : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Commands { get; } = [];
        public bool Enabled { get; set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                Commands.Add(command.CommandText);
                if (command.CommandText.Contains("\"Activities\"", StringComparison.Ordinal) && Entered.TrySetResult())
                    await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
