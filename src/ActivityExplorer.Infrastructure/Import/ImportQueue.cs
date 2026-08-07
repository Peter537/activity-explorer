using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
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
        if (!await db.Owners.AnyAsync(x => x.Id == request.OwnerId, cancellationToken))
        {
            throw new InvalidOperationException("The selected profile no longer exists.");
        }

        var batch = new ImportBatch
        {
            OwnerId = request.OwnerId,
            SourceKind = request.SourceKind,
            DisplayName = request.DisplayName,
            StagedPath = ManagedPathGuard.RelativeTo(paths.Root,
                ManagedPathGuard.ResolveUnder(paths.StagingPath, request.StagedPath)),
            Status = ImportStatus.Queued
        };
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);
        await _channel.Writer.WriteAsync(batch.Id, cancellationToken);
        return batch.Id;
    }

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

            if (batch.Status is ImportStatus.Completed or ImportStatus.CompletedWithWarnings or ImportStatus.Failed or ImportStatus.Interrupted)
            {
                yield break;
            }

            await Task.Delay(350, cancellationToken);
        }
    }
}
