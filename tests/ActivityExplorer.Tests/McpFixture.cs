using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using ActivityExplorer.Mcp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace ActivityExplorer.Tests;

internal sealed class McpFixture : IAsyncDisposable
{
    public string DirectoryPath { get; } = TestSupport.NewDirectory();
    public string DatabasePath => Path.Combine(DirectoryPath, "activity-explorer.db");
    public Guid Owner { get; } = Guid.NewGuid();
    public Guid OtherOwner { get; } = Guid.NewGuid();
    public Guid Activity { get; private set; }
    public Guid OtherActivity { get; private set; }
    public Guid Segment { get; private set; }
    public Guid Route { get; private set; }
    public Guid[] Efforts { get; private set; } = [];
    public Guid TagId { get; private set; }
    public IDbContextFactory<ExplorerDbContext> Factory => new FactoryImpl(new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite($"Data Source={DatabasePath};Pooling=False").Options);

    public IDbContextFactory<ExplorerDbContext> ReadFactory(DbCommandInterceptor interceptor) => new FactoryImpl(
        new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()).AddInterceptors(interceptor).Options);

    public static async Task<McpFixture> CreateAsync(bool wal = true)
    {
        var fixture = new McpFixture();
        try
        {
            await using var db = await fixture.Factory.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            if (!wal) await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=DELETE");
            db.Owners.AddRange(new OwnerProfile { Id = fixture.Owner, DisplayName = "MCP athlete", TimeZoneId = "Europe/Copenhagen" },
                new OwnerProfile { Id = fixture.OtherOwner, DisplayName = "Other athlete", TimeZoneId = "UTC" });
            var points = TestSupport.Track(60);
            var second = points.Select((point, index) => point with { Timestamp = points[0].Timestamp!.Value.AddDays(1).AddSeconds(index * 2) }).ToArray();
            var firstActivity = MakeActivity(fixture.Owner, "Æble ride", 1000, 300, points);
            firstActivity.DeviceName = "Device A";
            firstActivity.GearName = "Bike";
            var secondActivity = MakeActivity(fixture.Owner, "Second ride", 2000, 600, second);
            var otherActivity = MakeActivity(fixture.OtherOwner, "Other private ride", 9000, 900, points);
            fixture.Activity = firstActivity.Id;
            fixture.OtherActivity = otherActivity.Id;
            db.Activities.AddRange(firstActivity, secondActivity, otherActivity);
            var tag = new Tag { OwnerId = fixture.Owner, Name = "Training", NormalizedName = "TRAINING" };
            fixture.TagId = tag.Id;
            db.Tags.Add(tag);
            db.ActivityTags.Add(new ActivityTag { ActivityId = firstActivity.Id, TagId = tag.Id });
            var route = new Route
            {
                OwnerId = fixture.Owner,
                Name = "Saved ride",
                Description = "Synthetic route",
                Sport = SportKind.Cycling,
                DistanceMeters = 1000,
                ElevationGainMeters = 20,
                GeometryWkb = GeometryCodec.ToWkb(points)!
            };
            fixture.Route = route.Id;
            db.Routes.Add(route);
            db.ImportBatches.Add(new ImportBatch { OwnerId = fixture.Owner, Status = ImportStatus.Running, DisplayName = "Do not resume", StagedPath = "not-present.fit", SourceKind = SourceKind.Fit });
            db.FileOperations.Add(new FileOperationJournal { OwnerId = fixture.Owner, State = FileOperationState.Pending, Kind = FileOperationKind.Copy });
            await db.SaveChangesAsync();
            var segments = new SegmentService(fixture.Factory, new SegmentMatcher(), new OwnerMutationLock());
            fixture.Segment = await segments.CreateFromActivityAsync(new(fixture.Owner, firstActivity.Id, "Saved segment", 0, 59));
            fixture.Efforts = await db.SegmentEfforts.Where(effort => effort.SegmentId == fixture.Segment).OrderBy(effort => effort.ElapsedSeconds).Select(effort => effort.Id).ToArrayAsync();
            await new StatisticsService(fixture.Factory).RecomputeAsync(fixture.Owner);
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    private static Core.Domain.Activity MakeActivity(Guid owner, string title, double distance, double seconds, IReadOnlyList<TrackPoint> points)
    {
        var bounds = GeometryCodec.Bounds(points);
        return new()
        {
            OwnerId = owner,
            Title = title,
            Sport = SportKind.Cycling,
            StartTimeUtc = points[0].Timestamp!.Value,
            NaturalFingerprint = Guid.NewGuid().ToString("N"),
            DistanceMeters = distance,
            MovingTimeSeconds = seconds,
            ElapsedTimeSeconds = (points[^1].Timestamp!.Value - points[0].Timestamp!.Value).TotalSeconds,
            ElevationGainMeters = 20,
            HasGps = true,
            HasPower = true,
            GeometryWkb = GeometryCodec.ToWkb(points),
            SimplifiedGeometryWkb = GeometryCodec.ToWkb(points),
            MinLatitude = bounds.MinLat,
            MinLongitude = bounds.MinLon,
            MaxLatitude = bounds.MaxLat,
            MaxLongitude = bounds.MaxLon,
            Stream = new ActivityStream { OwnerId = owner, CompressedPayload = TrackCodec.Encode(points), PointCount = points.Count }
        };
    }

    public IHost BuildHost(params string[] args)
    {
        var previous = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_DATA");
        Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", DirectoryPath);
        try { return McpHost.Build(args); }
        finally { Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", previous); }
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        var expectedRoot = Path.GetFullPath(Path.Combine(System.IO.Directory.GetCurrentDirectory(), "TestResults", "runtime")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(DirectoryPath).StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The fixture path escaped the test runtime directory.");
        if (System.IO.Directory.Exists(DirectoryPath)) System.IO.Directory.Delete(DirectoryPath, recursive: true);
        return ValueTask.CompletedTask;
    }

    private sealed class FactoryImpl(DbContextOptions<ExplorerDbContext> options) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);
        public Task<ExplorerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ExplorerDbContext(options));
    }
}
