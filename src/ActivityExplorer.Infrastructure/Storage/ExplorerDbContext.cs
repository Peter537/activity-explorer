using ActivityExplorer.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ActivityExplorer.Infrastructure.Storage;

public sealed class ExplorerDbContext(DbContextOptions<ExplorerDbContext> options) : DbContext(options)
{
    public DbSet<OwnerProfile> Owners => Set<OwnerProfile>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<SourceFile> SourceFiles => Set<SourceFile>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<ActivityLap> ActivityLaps => Set<ActivityLap>();
    public DbSet<ActivityStream> ActivityStreams => Set<ActivityStream>();
    public DbSet<ActivityMetric> ActivityMetrics => Set<ActivityMetric>();
    public DbSet<Gear> Gears => Set<Gear>();
    public DbSet<Route> Routes => Set<Route>();
    public DbSet<Segment> Segments => Set<Segment>();
    public DbSet<SegmentEffort> SegmentEfforts => Set<SegmentEffort>();
    public DbSet<StatisticSnapshot> StatisticSnapshots => Set<StatisticSnapshot>();
    public DbSet<WatchedFolder> WatchedFolders => Set<WatchedFolder>();
    public DbSet<ApplicationSetting> ApplicationSettings => Set<ApplicationSetting>();
    public DbSet<FileOperationJournal> FileOperations => Set<FileOperationJournal>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OwnerProfile>().HasIndex(x => x.DisplayName);
        modelBuilder.Entity<ImportBatch>().HasIndex(x => new { x.OwnerId, x.CreatedAtUtc });
        modelBuilder.Entity<SourceFile>().HasIndex(x => new { x.OwnerId, x.Provider, x.Sha256 }).IsUnique();
        modelBuilder.Entity<SourceFile>().HasIndex(x => new { x.OwnerId, x.Provider, x.ExternalId });
        modelBuilder.Entity<SourceFile>().HasIndex(x => x.RouteId).IsUnique().HasFilter("\"RouteId\" IS NOT NULL");
        modelBuilder.Entity<Activity>().HasIndex(x => new { x.OwnerId, x.NaturalFingerprint }).IsUnique();
        modelBuilder.Entity<Activity>().HasIndex(x => new { x.OwnerId, x.StartTimeUtc });
        modelBuilder.Entity<Activity>().HasIndex(x => new { x.OwnerId, x.Sport, x.StartTimeUtc });
        modelBuilder.Entity<Activity>().HasIndex(x => new { x.OwnerId, x.GarminId })
            .IsUnique()
            .HasFilter("\"GarminId\" IS NOT NULL");
        modelBuilder.Entity<Activity>().HasIndex(x => new { x.OwnerId, x.StravaId })
            .IsUnique()
            .HasFilter("\"StravaId\" IS NOT NULL");
        modelBuilder.Entity<ActivityStream>().HasKey(x => x.ActivityId);
        modelBuilder.Entity<Activity>()
            .HasOne(x => x.Stream)
            .WithOne(x => x.Activity)
            .HasForeignKey<ActivityStream>(x => x.ActivityId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Activity>()
            .HasMany(x => x.Laps)
            .WithOne(x => x.Activity)
            .HasForeignKey(x => x.ActivityId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Activity>()
            .HasMany(x => x.SourceFiles)
            .WithOne(x => x.Activity)
            .HasForeignKey(x => x.ActivityId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Route>()
            .HasMany(x => x.SourceFiles)
            .WithOne(x => x.Route)
            .HasForeignKey(x => x.RouteId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Route>()
            .HasOne<Activity>()
            .WithMany()
            .HasForeignKey(x => x.SourceActivityId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Segment>()
            .HasOne<Activity>()
            .WithMany()
            .HasForeignKey(x => x.SourceActivityId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Route>().HasIndex(x => new { x.OwnerId, x.MinLongitude, x.MaxLongitude, x.MinLatitude, x.MaxLatitude });
        modelBuilder.Entity<FileOperationJournal>().HasIndex(x => new { x.State, x.CreatedAtUtc });
        modelBuilder.Entity<Activity>()
            .HasMany(x => x.Metrics)
            .WithOne(x => x.Activity)
            .HasForeignKey(x => x.ActivityId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ActivityMetric>().HasIndex(x => new { x.ActivityId, x.Key, x.Origin }).IsUnique();
        modelBuilder.Entity<SegmentEffort>().HasIndex(x => new { x.SegmentId, x.ActivityId, x.StartPointIndex }).IsUnique();
        modelBuilder.Entity<SegmentEffort>().HasIndex(x => new { x.SegmentId, x.ElapsedSeconds });
        modelBuilder.Entity<StatisticSnapshot>().HasIndex(x => new { x.OwnerId, x.Scope, x.Sport, x.Kind, x.Key }).IsUnique();
        modelBuilder.Entity<WatchedFolder>().HasIndex(x => new { x.OwnerId, x.Path }).IsUnique();

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties().Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(new ValueConverter<DateTimeOffset, long>(
                        value => value.UtcDateTime.Ticks,
                        value => new DateTimeOffset(value, TimeSpan.Zero)));
                }
                else
                {
                    property.SetValueConverter(new ValueConverter<DateTimeOffset?, long?>(
                        value => value.HasValue ? value.Value.UtcDateTime.Ticks : null,
                        value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null));
                }
                property.SetColumnType("INTEGER");
            }
        }
    }
}
