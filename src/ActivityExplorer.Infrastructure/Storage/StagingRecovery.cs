using ActivityExplorer.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ActivityExplorer.Infrastructure.Storage;

internal static class StagingRecovery
{
    internal static async Task ReconcileAsync(
        ExplorerDbContext db, AppDataPaths paths, ILogger logger, CancellationToken cancellationToken)
    {
        HashSet<string> protectedPaths;
        try
        {
            protectedPaths = await GetProtectedNamesAsync(db, paths, cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or NotSupportedException)
        {
            logger.LogWarning(exception, "Staging reconciliation was skipped because an active operation has an invalid path.");
            return;
        }

        var unidentified = 0;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(paths.StagingPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(directory);
                if (!Guid.TryParseExact(name, "N", out _) || protectedPaths.Contains(name)) continue;
                try
                {
                    if (!ManagedStagingDirectory.DeleteOwned(paths.StagingPath, directory)) unidentified++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    logger.LogWarning(exception, "Staging directory {StagingId} could not be reclaimed and was retained for recovery.", name);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Staging reconciliation could not enumerate the configured staging directory.");
        }

        if (unidentified > 0)
            logger.LogWarning("Retained {Count} staging directories without a valid ownership marker.", unidentified);
    }

    internal static async Task<HashSet<string>> GetProtectedNamesAsync(
        ExplorerDbContext db, AppDataPaths paths, CancellationToken cancellationToken)
    {
        var imports = await db.ImportBatches.AsNoTracking()
            .Where(x => x.Status == ImportStatus.Queued || x.Status == ImportStatus.Running || x.Status == ImportStatus.Interrupted ||
                        x.Status == ImportStatus.ReviewRequired)
            .Select(x => x.StagedPath)
            .ToListAsync(cancellationToken);
        var operations = await db.FileOperations.AsNoTracking()
            .Where(x => x.State == FileOperationState.Pending || x.State == FileOperationState.Prepared ||
                        x.State == FileOperationState.DatabaseCommitted)
            .Select(x => new { x.SourceRelativePath, x.TargetRelativePath })
            .ToListAsync(cancellationToken);
        var protectedPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var path in imports.Concat(operations.SelectMany(x => new[] { x.SourceRelativePath, x.TargetRelativePath })))
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var fullPath = Path.GetFullPath(path, paths.Root);
            var relative = Path.GetRelativePath(paths.StagingPath, fullPath);
            if (relative == ".")
                throw new InvalidDataException("An active operation references the staging root.");
            var firstComponent = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], 2)[0];
            if (Guid.TryParseExact(firstComponent, "N", out _)) protectedPaths.Add(firstComponent);
        }
        return protectedPaths;
    }
}
