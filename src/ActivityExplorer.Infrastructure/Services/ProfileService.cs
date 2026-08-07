using System.Text.Json;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class ProfileService(
    IDbContextFactory<ExplorerDbContext> contextFactory,
    AppDataPaths paths,
    IFileOperationCoordinator fileOperations,
    IOwnerMutationLock ownerMutationLock) : IProfileService
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new() { WriteIndented = true };
    public async Task<IReadOnlyList<ProfileSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Owners.AsNoTracking().OrderBy(x => x.DisplayName)
            .Select(x => new ProfileSummary(x.Id, x.DisplayName,
                db.Activities.Count(a => a.OwnerId == x.Id),
                db.Activities.Where(a => a.OwnerId == x.Id).Sum(a => a.DistanceMeters),
                x.CreatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<Guid> CreateAsync(string displayName, CancellationToken cancellationToken = default)
    {
        var name = displayName.Trim();
        if (name.Length is < 1 or > 120) throw new ArgumentException("Profile name must contain 1 to 120 characters.", nameof(displayName));
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Owners.AnyAsync(x => x.DisplayName == name, cancellationToken))
            throw new InvalidOperationException("A profile with that name already exists.");
        var owner = new Core.Domain.OwnerProfile { DisplayName = name };
        db.Owners.Add(owner);
        await db.SaveChangesAsync(cancellationToken);
        paths.GetOwnerOriginalsPath(owner.Id);
        return owner.Id;
    }

    public async Task DeleteAsync(Guid ownerId, string confirmation, CancellationToken cancellationToken = default)
    {
        await using var ownerLock = await ownerMutationLock.AcquireAsync([ownerId], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var owner = await db.Owners.SingleOrDefaultAsync(x => x.Id == ownerId, cancellationToken)
            ?? throw new InvalidOperationException("Profile was not found.");
        if (!string.Equals(confirmation, $"DELETE {owner.DisplayName}", StringComparison.Ordinal))
            throw new InvalidOperationException($"Type DELETE {owner.DisplayName} exactly to confirm.");
        if (await db.ImportBatches.AnyAsync(x => x.OwnerId == ownerId &&
                (x.Status == Core.Domain.ImportStatus.Queued ||
                 x.Status == Core.Domain.ImportStatus.Running ||
                 x.Status == Core.Domain.ImportStatus.Interrupted), cancellationToken))
            throw new InvalidOperationException("Finish or remove interrupted imports before deleting this profile.");

        var quarantineOperation = await fileOperations.QuarantineOwnerAsync(ownerId, cancellationToken);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            db.SegmentEfforts.RemoveRange(db.SegmentEfforts.Where(x => x.OwnerId == ownerId));
            db.StatisticSnapshots.RemoveRange(db.StatisticSnapshots.Where(x => x.OwnerId == ownerId));
            db.Segments.RemoveRange(db.Segments.Where(x => x.OwnerId == ownerId));
            db.Routes.RemoveRange(db.Routes.Where(x => x.OwnerId == ownerId));
            db.ActivityLaps.RemoveRange(db.ActivityLaps.Where(x => x.OwnerId == ownerId));
            db.ActivityStreams.RemoveRange(db.ActivityStreams.Where(x => x.OwnerId == ownerId));
            db.SourceFiles.RemoveRange(db.SourceFiles.Where(x => x.OwnerId == ownerId));
            db.Activities.RemoveRange(db.Activities.Where(x => x.OwnerId == ownerId));
            db.ImportBatches.RemoveRange(db.ImportBatches.Where(x => x.OwnerId == ownerId));
            db.WatchedFolders.RemoveRange(db.WatchedFolders.Where(x => x.OwnerId == ownerId));
            db.Gears.RemoveRange(db.Gears.Where(x => x.OwnerId == ownerId));
            db.Owners.Remove(owner);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (quarantineOperation.HasValue)
                await fileOperations.RollbackAsync(quarantineOperation.Value, CancellationToken.None);
            throw;
        }

        if (quarantineOperation.HasValue)
        {
            try
            {
                await fileOperations.CommitAsync(quarantineOperation.Value, cancellationToken);
            }
            catch (Exception exception)
            {
                throw new IOException(
                    "The profile was removed, but encrypted/private file cleanup is pending and will be retried at startup.",
                    exception);
            }
        }
    }

    public async Task<ProfileExport> ExportAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var owner = await db.Owners.AsNoTracking().SingleOrDefaultAsync(x => x.Id == ownerId, cancellationToken)
            ?? throw new InvalidOperationException("Profile was not found.");
        var activities = await db.Activities.AsNoTracking().Where(x => x.OwnerId == ownerId)
            .OrderBy(x => x.StartTimeUtc)
            .Select(x => new
            {
                x.Id,
                x.Sport,
                x.Title,
                x.Description,
                x.StartTimeUtc,
                x.OriginalUtcOffset,
                x.DistanceMeters,
                x.MovingTimeSeconds,
                x.ElapsedTimeSeconds,
                x.ElevationGainMeters,
                x.Calories,
                x.AverageHeartRate,
                x.AverageCadence,
                x.AveragePowerWatts,
                x.DeviceName,
                x.GearName
            }).ToListAsync(cancellationToken);
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            productVersion = "0.1.0",
            exportedAtUtc = DateTimeOffset.UtcNow,
            profile = new { owner.Id, owner.DisplayName, owner.CreatedAtUtc },
            activities
        }, ExportJsonOptions);
        return new ProfileExport($"{SafeName(owner.DisplayName)}-activity-explorer.json", payload);
    }

    private static string SafeName(string value) =>
        string.Concat(value.Select(x => Path.GetInvalidFileNameChars().Contains(x) ? '-' : x)).Trim();
}
