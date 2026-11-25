using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Import;

public sealed class ImportQueue(
    IDbContextFactory<ExplorerDbContext> contextFactory,
    AppDataPaths paths,
    IOwnerMutationLock ownerMutationLock) : IImportQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public async ValueTask<Guid> EnqueueAsync(ImportRequest request, CancellationToken cancellationToken = default)
    {
        await using var ownerLock = await ownerMutationLock.AcquireAsync([request.OwnerId], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await EnsureOwnerAsync(db, request.OwnerId, cancellationToken);
        var batch = CreateBatch(request);
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);
        // The persisted batch owns the staged file even if the request is cancelled now.
        await _channel.Writer.WriteAsync(batch.Id, CancellationToken.None);
        return batch.Id;
    }

    public async ValueTask<bool> EnqueueWatchedAsync(
        Guid ownerId, string stagedPath, string displayName, CancellationToken cancellationToken = default)
    {
        await using var ownerLock = await ownerMutationLock.AcquireAsync([ownerId], cancellationToken);
        var filePath = ManagedPathGuard.ResolveUnder(paths.StagingPath, stagedPath);
        var sha256 = await Fingerprint.Sha256Async(filePath, cancellationToken);
        var format = GetWatchedFormat(filePath);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await EnsureOwnerAsync(db, ownerId, cancellationToken);

        if (await IsKnownWatchedContentAsync(db, ownerId, sha256, format, cancellationToken))
            return false;

        var admission = await db.Set<WatchedImportAdmission>()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.Sha256 == sha256 && x.Format == format, cancellationToken);
        var batch = CreateBatch(new ImportRequest(ownerId, filePath, displayName, SourceKind.WatchedFolder));
        db.ImportBatches.Add(batch);
        if (admission is null)
        {
            db.Add(new WatchedImportAdmission { OwnerId = ownerId, Sha256 = sha256, Format = format, BatchId = batch.Id });
        }
        else
        {
            admission.BatchId = batch.Id;
        }
        await db.SaveChangesAsync(cancellationToken);
        await _channel.Writer.WriteAsync(batch.Id, CancellationToken.None);
        return true;
    }

    internal static async Task<bool> IsKnownWatchedContentAsync(ExplorerDbContext db, Guid ownerId, string sha256,
        string format, CancellationToken cancellationToken) =>
        await db.Set<WatchedImportAdmission>().AnyAsync(x => x.OwnerId == ownerId && x.Sha256 == sha256 &&
            x.Format == format && (x.Rejected || x.Batch.Status != ImportStatus.Failed), cancellationToken) ||
        await db.SourceFiles.AnyAsync(x => x.OwnerId == ownerId && (x.ActivityId != null || x.RouteId == null) &&
            x.Sha256 == sha256, cancellationToken);

    internal static string GetWatchedFormat(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        // GZip has no member name. Its inner format comes from the remaining filename.
        return extension == ".gz"
            ? Path.GetExtension(Path.GetFileNameWithoutExtension(path)).ToLowerInvariant() + extension
            : extension;
    }

    private static async Task EnsureOwnerAsync(ExplorerDbContext db, Guid ownerId, CancellationToken cancellationToken)
    {
        if (!await db.Owners.AnyAsync(x => x.Id == ownerId, cancellationToken))
            throw new InvalidOperationException("The selected profile no longer exists.");
    }

    private ImportBatch CreateBatch(ImportRequest request) =>
        new()
        {
            OwnerId = request.OwnerId,
            SourceKind = request.SourceKind,
            DisplayName = request.DisplayName,
            StagedPath = ManagedPathGuard.RelativeTo(paths.Root,
                ManagedPathGuard.ResolveUnder(paths.StagingPath, request.StagedPath)),
            Status = ImportStatus.Queued
        };

    internal async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var recoverable = await db.ImportBatches
            .Where(x => x.Status == ImportStatus.Queued || x.Status == ImportStatus.Interrupted)
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        foreach (var batch in recoverable)
        {
            var stagedPath = ResolveStagedPath(batch.StagedPath);
            if (!File.Exists(stagedPath))
            {
                batch.Status = ImportStatus.Failed;
                batch.ErrorMessage = "The staged source is no longer available. Upload the file again.";
                batch.CompletedAtUtc = DateTimeOffset.UtcNow;
                continue;
            }
            batch.Status = ImportStatus.Queued;
            batch.ErrorMessage = null;
            batch.CompletedAtUtc = null;
            await _channel.Writer.WriteAsync(batch.Id, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    internal string ResolveStagedPath(string storedPath)
    {
        var candidate = Path.IsPathRooted(storedPath)
            ? storedPath
            : Path.Combine(paths.Root, storedPath.Replace('/', Path.DirectorySeparatorChar));
        return ManagedPathGuard.ResolveUnder(paths.StagingPath, candidate);
    }

    internal IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public async IAsyncEnumerable<ImportProgress> WatchAsync(
        Guid importId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ImportStatus? previous = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var batch = await db.ImportBatches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == importId, cancellationToken);
            if (batch is null) yield break;

            if (batch.Status != previous || batch.Status == ImportStatus.Running)
            {
                yield return new ImportProgress(
                    batch.Id,
                    batch.Status,
                    batch.FilesDiscovered,
                    batch.ActivitiesCreated,
                    batch.ActivitiesUpdated,
                    batch.DuplicatesSkipped + batch.UnsupportedSkipped,
                    batch.ErrorMessage ?? batch.Summary);
                previous = batch.Status;
            }

            if (batch.Status is ImportStatus.Completed or ImportStatus.CompletedWithWarnings or ImportStatus.Failed or
                ImportStatus.Interrupted or ImportStatus.ReviewRequired)
            {
                yield break;
            }

            await Task.Delay(350, cancellationToken);
        }
    }
}
