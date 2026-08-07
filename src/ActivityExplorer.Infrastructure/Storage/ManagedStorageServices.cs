using System.Collections.Concurrent;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ActivityExplorer.Infrastructure.Storage;

public static class ManagedPathGuard
{
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string ResolveUnder(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("A managed path is required.");
        var rootFull = Path.GetFullPath(root);
        var candidate = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(rootFull, path.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, PathComparison) && !string.Equals(candidate, rootFull, PathComparison))
            throw new InvalidDataException("The managed path escapes its configured data directory.");
        EnsureNoReparsePoints(rootFull, candidate);
        return candidate;
    }

    public static string RelativeTo(string root, string fullPath)
    {
        var resolved = ResolveUnder(root, fullPath);
        return Path.GetRelativePath(Path.GetFullPath(root), resolved).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static void EnsureNoReparsePoints(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".") return;
        var current = root;
        foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Managed paths cannot traverse symbolic links or reparse points.");
        }
    }
}

public sealed class OriginalStore(AppDataPaths paths) : IOriginalStore
{
    public string ResolveStoredPath(string storedPath)
    {
        var candidate = Path.IsPathRooted(storedPath)
            ? storedPath
            : Path.Combine(paths.Root, storedPath.Replace('/', Path.DirectorySeparatorChar));
        return ManagedPathGuard.ResolveUnder(paths.OriginalsPath, candidate);
    }

    public string ToStoredPath(string fullPath)
    {
        var resolved = ManagedPathGuard.ResolveUnder(paths.OriginalsPath, fullPath);
        return ManagedPathGuard.RelativeTo(paths.Root, resolved);
    }

    public string GetOriginalTarget(Guid ownerId, string sha256, string extension)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("A SHA-256 fingerprint is required.", nameof(sha256));
        var safeExtension = extension.ToLowerInvariant();
        if (safeExtension.Length > 12 || safeExtension.Any(character => !char.IsLetterOrDigit(character) && character != '.'))
            throw new ArgumentException("The source extension is invalid.", nameof(extension));
        return ManagedPathGuard.ResolveUnder(
            paths.GetOwnerOriginalsPath(ownerId),
            sha256.ToUpperInvariant() + safeExtension);
    }
}

public sealed class OwnerMutationLock : IOwnerMutationLock
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        IEnumerable<Guid> ownerIds,
        CancellationToken cancellationToken = default)
    {
        var acquired = new List<SemaphoreSlim>();
        try
        {
            foreach (var ownerId in ownerIds.Distinct().Order())
            {
                var gate = _locks.GetOrAdd(ownerId, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(cancellationToken);
                acquired.Add(gate);
            }
            return new Releaser(acquired);
        }
        catch
        {
            for (var index = acquired.Count - 1; index >= 0; index--) acquired[index].Release();
            throw;
        }
    }

    private sealed class Releaser(IReadOnlyList<SemaphoreSlim> gates) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            for (var index = gates.Count - 1; index >= 0; index--) gates[index].Release();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class FileOperationCoordinator(
    IDbContextFactory<ExplorerDbContext> contextFactory,
    AppDataPaths paths,
    IOriginalStore originals,
    ILogger<FileOperationCoordinator> logger) : IFileOperationCoordinator
{
    public async Task<PreparedFileOperation> PrepareCopyAsync(
        Guid ownerId,
        Guid? entityId,
        string sourcePath,
        string targetPath,
        string expectedSha256,
        bool deleteSourceOnCommit = false,
        CancellationToken cancellationToken = default)
    {
        var source = ManagedPathGuard.ResolveUnder(paths.Root, sourcePath);
        var target = ManagedPathGuard.ResolveUnder(paths.OriginalsPath, targetPath);
        if (!File.Exists(source)) throw new FileNotFoundException("The managed source file is unavailable.", source);

        var operation = new FileOperationJournal
        {
            Kind = FileOperationKind.Copy,
            OwnerId = ownerId,
            EntityId = entityId,
            SourceRelativePath = ManagedPathGuard.RelativeTo(paths.Root, source),
            TargetRelativePath = originals.ToStoredPath(target),
            DeleteSourceOnCommit = deleteSourceOnCommit
        };
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.FileOperations.Add(operation);
        await db.SaveChangesAsync(cancellationToken);

        var created = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target))
            {
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
                await input.CopyToAsync(output, cancellationToken);
                created = true;
            }
            var actualHash = await Fingerprint.Sha256Async(target, cancellationToken);
            if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The stored original failed SHA-256 verification.");
            operation.State = FileOperationState.Prepared;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return new PreparedFileOperation(operation.Id, operation.TargetRelativePath);
        }
        catch (Exception exception)
        {
            if (created && File.Exists(target)) File.Delete(target);
            operation.State = FileOperationState.Failed;
            operation.ErrorMessage = SafeMessage(exception.Message);
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<Guid?> QuarantineOwnerAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        var source = ManagedPathGuard.ResolveUnder(paths.OriginalsPath, Path.Combine(paths.OriginalsPath, ownerId.ToString("N")));
        if (!Directory.Exists(source)) return null;
        var operation = new FileOperationJournal
        {
            Kind = FileOperationKind.OwnerQuarantine,
            OwnerId = ownerId,
            SourceRelativePath = ManagedPathGuard.RelativeTo(paths.Root, source)
        };
        var target = ManagedPathGuard.ResolveUnder(paths.QuarantinePath, Path.Combine(paths.QuarantinePath, operation.Id.ToString("N")));
        operation.TargetRelativePath = ManagedPathGuard.RelativeTo(paths.Root, target);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.FileOperations.Add(operation);
        await db.SaveChangesAsync(cancellationToken);
        Directory.Move(source, target);
        operation.State = FileOperationState.Prepared;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return operation.Id;
    }

    public async Task<Guid?> QuarantineFileAsync(
        Guid ownerId,
        Guid? entityId,
        string storedPath,
        CancellationToken cancellationToken = default)
    {
        var source = originals.ResolveStoredPath(storedPath);
        _ = ManagedPathGuard.ResolveUnder(paths.GetOwnerOriginalsPath(ownerId), source);
        if (!File.Exists(source)) return null;

        var operation = new FileOperationJournal
        {
            Kind = FileOperationKind.FileQuarantine,
            OwnerId = ownerId,
            EntityId = entityId,
            SourceRelativePath = ManagedPathGuard.RelativeTo(paths.Root, source)
        };
        var target = ManagedPathGuard.ResolveUnder(
            paths.QuarantinePath,
            Path.Combine(paths.QuarantinePath, operation.Id.ToString("N"), Path.GetFileName(source)));
        operation.TargetRelativePath = ManagedPathGuard.RelativeTo(paths.Root, target);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.FileOperations.Add(operation);
        await db.SaveChangesAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target);
        operation.State = FileOperationState.Prepared;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return operation.Id;
    }

    public async Task CommitAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var operation = await db.FileOperations.SingleAsync(x => x.Id == operationId, cancellationToken);
        operation.State = FileOperationState.DatabaseCommitted;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        if (operation.Kind == FileOperationKind.Copy && operation.DeleteSourceOnCommit &&
            operation.SourceRelativePath is not null)
        {
            var referenced = await db.SourceFiles.AsNoTracking()
                .AnyAsync(x => x.StoredPath == operation.SourceRelativePath, cancellationToken);
            if (!referenced)
            {
                var source = ManagedPathGuard.ResolveUnder(paths.Root, operation.SourceRelativePath);
                if (File.Exists(source)) File.Delete(source);
            }
        }
        else if (operation.Kind == FileOperationKind.OwnerQuarantine && operation.TargetRelativePath is not null)
        {
            var quarantine = ManagedPathGuard.ResolveUnder(paths.QuarantinePath,
                Path.Combine(paths.Root, operation.TargetRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (Directory.Exists(quarantine)) Directory.Delete(quarantine, true);
        }
        else if (operation.Kind == FileOperationKind.FileQuarantine &&
                 operation.SourceRelativePath is not null && operation.TargetRelativePath is not null)
        {
            var referenced = await db.SourceFiles.AsNoTracking()
                .AnyAsync(x => x.StoredPath == operation.SourceRelativePath, cancellationToken);
            if (referenced)
                throw new InvalidOperationException("The quarantined original is still referenced and cannot be deleted.");
            var quarantine = ManagedPathGuard.ResolveUnder(paths.QuarantinePath,
                Path.Combine(paths.Root, operation.TargetRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(quarantine)) File.Delete(quarantine);
            var quarantineDirectory = Path.GetDirectoryName(quarantine);
            if (quarantineDirectory is not null && Directory.Exists(quarantineDirectory) &&
                !Directory.EnumerateFileSystemEntries(quarantineDirectory).Any())
                Directory.Delete(quarantineDirectory);
        }

        operation.State = FileOperationState.Completed;
        operation.ErrorMessage = null;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RollbackAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var operation = await db.FileOperations.SingleAsync(x => x.Id == operationId, cancellationToken);
        if (operation.Kind == FileOperationKind.Copy && operation.TargetRelativePath is not null)
        {
            var referenced = await db.SourceFiles.AsNoTracking()
                .AnyAsync(x => x.StoredPath == operation.TargetRelativePath, cancellationToken);
            if (!referenced)
            {
                var target = originals.ResolveStoredPath(operation.TargetRelativePath);
                if (File.Exists(target)) File.Delete(target);
            }
        }
        else if (operation.Kind == FileOperationKind.OwnerQuarantine &&
                 operation.SourceRelativePath is not null && operation.TargetRelativePath is not null)
        {
            var source = ManagedPathGuard.ResolveUnder(paths.OriginalsPath,
                Path.Combine(paths.Root, operation.SourceRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            var target = ManagedPathGuard.ResolveUnder(paths.QuarantinePath,
                Path.Combine(paths.Root, operation.TargetRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (Directory.Exists(target) && !Directory.Exists(source)) Directory.Move(target, source);
        }
        else if (operation.Kind == FileOperationKind.FileQuarantine &&
                 operation.SourceRelativePath is not null && operation.TargetRelativePath is not null)
        {
            var source = ManagedPathGuard.ResolveUnder(paths.OriginalsPath,
                Path.Combine(paths.Root, operation.SourceRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            var target = ManagedPathGuard.ResolveUnder(paths.QuarantinePath,
                Path.Combine(paths.Root, operation.TargetRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(target) && !File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                File.Move(target, source);
            }
            var quarantineDirectory = Path.GetDirectoryName(target);
            if (quarantineDirectory is not null && Directory.Exists(quarantineDirectory) &&
                !Directory.EnumerateFileSystemEntries(quarantineDirectory).Any())
                Directory.Delete(quarantineDirectory);
        }
        operation.State = FileOperationState.RolledBack;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var pending = await db.FileOperations.AsNoTracking()
            .Where(x => x.State == FileOperationState.Pending ||
                        x.State == FileOperationState.Prepared ||
                        x.State == FileOperationState.DatabaseCommitted)
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => new { x.Id, x.Kind, x.OwnerId, x.SourceRelativePath, x.TargetRelativePath })
            .ToListAsync(cancellationToken);
        foreach (var operation in pending)
        {
            try
            {
                if (operation.Kind == FileOperationKind.Copy)
                {
                    var referenced = operation.TargetRelativePath is not null &&
                        await db.SourceFiles.AsNoTracking().AnyAsync(
                            x => x.StoredPath == operation.TargetRelativePath, cancellationToken);
                    if (referenced) await CommitAsync(operation.Id, cancellationToken);
                    else await RollbackAsync(operation.Id, cancellationToken);
                }
                else if (operation.Kind == FileOperationKind.OwnerQuarantine)
                {
                    var ownerExists = operation.OwnerId.HasValue &&
                        await db.Owners.AsNoTracking().AnyAsync(x => x.Id == operation.OwnerId, cancellationToken);
                    if (ownerExists) await RollbackAsync(operation.Id, cancellationToken);
                    else await CommitAsync(operation.Id, cancellationToken);
                }
                else if (operation.Kind == FileOperationKind.FileQuarantine)
                {
                    var referenced = operation.SourceRelativePath is not null &&
                        await db.SourceFiles.AsNoTracking().AnyAsync(
                            x => x.StoredPath == operation.SourceRelativePath, cancellationToken);
                    if (referenced) await RollbackAsync(operation.Id, cancellationToken);
                    else await CommitAsync(operation.Id, cancellationToken);
                }
                else
                {
                    throw new InvalidOperationException($"Unsupported file operation kind {operation.Kind}.");
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not recover file operation {OperationId}.", operation.Id);
                await MarkFailedAsync(operation.Id, exception.Message);
            }
        }
    }

    private async Task MarkFailedAsync(Guid operationId, string message)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        var operation = await db.FileOperations.SingleAsync(x => x.Id == operationId);
        operation.State = FileOperationState.Failed;
        operation.ErrorMessage = SafeMessage(message);
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private static string SafeMessage(string value) => value.Replace('\r', ' ').Replace('\n', ' ')[..Math.Min(value.Length, 4000)];
}
