using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class ExplorationIndexService(
    IDbContextFactory<ExplorerDbContext> contextFactory,
    IOwnerMutationLock ownerMutationLock,
    ExplorationSourceReader sourceReader) : IExplorationIndexService, IDisposable
{
    public const int ComputationVersion = 1;
    private readonly SemaphoreSlim _activityGate = new(1, 1);

    public void Dispose() => _activityGate.Dispose();

    public async Task<ExplorationIndexStatus> GetStatusAsync(ExplorationScope scope, CancellationToken cancellationToken = default)
    {
        Validate(scope);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);
        if (scope.OwnerId.HasValue && !await db.Owners.AnyAsync(x => x.Id == scope.OwnerId, cancellationToken))
            throw new InvalidOperationException("The selected profile was not found.");
        var activities = Scoped(db, scope);
        var total = await activities.CountAsync(cancellationToken);
        var indexed = await activities.CountAsync(activity => db.ActivityExplorationIndexes.Any(index =>
            index.ActivityId == activity.Id && index.InputVersion == activity.ExplorationInputVersion &&
            index.ComputationVersion == ComputationVersion), cancellationToken);
        var limited = await activities.CountAsync(activity => db.ActivityExplorationIndexes.Any(index =>
            index.ActivityId == activity.Id && index.InputVersion == activity.ExplorationInputVersion &&
            index.ComputationVersion == ComputationVersion && index.IsLimited), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(total, indexed, limited);
    }

    public async Task<ExplorationIndexStatus> BuildAsync(ExplorationScope scope, IProgress<ExplorationBuildProgress>? progress = null,
        bool recheckLimited = false, CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(scope, cancellationToken);
        var recheck = new Queue<Guid>();
        if (recheckLimited)
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var ids = await Scoped(db, scope).Where(activity => db.ActivityExplorationIndexes.Any(index =>
                    index.ActivityId == activity.Id && index.IsLimited))
                .OrderBy(x => x.StartTimeUtc).ThenBy(x => x.Id).Select(x => x.Id).ToArrayAsync(cancellationToken);
            recheck = new(ids);
        }
        Report(progress, status, recheck.Count);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (status.IsComplete && recheck.Count == 0) return status;
            var force = recheck.Count > 0;
            var batch = force
                ? Enumerable.Range(0, Math.Min(32, recheck.Count)).Select(_ => recheck.Dequeue()).ToArray()
                : await PendingAsync(scope, cancellationToken);
            for (var position = 0; position < batch.Length; position++)
            {
                await _activityGate.WaitAsync(cancellationToken);
                try
                {
                    var activity = await NextAsync(scope, batch[position], force, cancellationToken);
                    if (activity is not null)
                    {
                        var published = await IndexAsync(activity, cancellationToken);
                        if (published && !force)
                            status = status with { IndexedActivities = Math.Min(status.TotalActivities, status.IndexedActivities + 1) };
                        else if (!published && force) recheck.Enqueue(activity.Id);
                    }
                }
                finally
                {
                    _activityGate.Release();
                }
                Report(progress, status, recheck.Count + (force ? batch.Length - position - 1 : 0), confirmed: false);
            }
            status = await GetStatusAsync(scope, cancellationToken);
            Report(progress, status, recheck.Count);
        }
    }

    private async Task<Guid[]> PendingAsync(ExplorationScope scope, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await Scoped(db, scope).Where(activity => !db.ActivityExplorationIndexes.Any(index =>
                index.ActivityId == activity.Id && index.InputVersion == activity.ExplorationInputVersion &&
                index.ComputationVersion == ComputationVersion))
            .OrderBy(x => x.StartTimeUtc).ThenBy(x => x.Id).Select(x => x.Id).Take(32).ToArrayAsync(cancellationToken);
    }

    private async Task<Activity?> NextAsync(ExplorationScope scope, Guid id, bool force, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);
        var query = Scoped(db, scope).Where(x => x.Id == id);
        query = force
            ? query.Where(activity => db.ActivityExplorationIndexes.Any(index => index.ActivityId == activity.Id && index.IsLimited))
            : query.Where(activity => !db.ActivityExplorationIndexes.Any(index => index.ActivityId == activity.Id &&
                index.InputVersion == activity.ExplorationInputVersion && index.ComputationVersion == ComputationVersion));
        var activity = await query.OrderBy(x => x.StartTimeUtc).ThenBy(x => x.Id)
            .Include(x => x.Stream).Include(x => x.SourceFiles).AsSplitQuery().FirstOrDefaultAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return activity;
    }

    private async Task<bool> IndexAsync(Activity activity, CancellationToken cancellationToken)
    {
        if (activity.Stream is null && activity.HasGps)
            throw new InvalidDataException($"GPS samples for '{activity.Title}' are missing. Reimport the activity before retrying exploration.");
        if (activity.Stream is not null && activity.Stream.OwnerId != activity.OwnerId)
            throw new InvalidDataException($"GPS samples for '{activity.Title}' belong to a different profile.");
        var points = activity.Stream is null ? [] : await Task.Run(
            () => TrackCodec.DecodeAsync(activity.Stream.CompressedPayload, cancellationToken).AsTask(), cancellationToken);
        if (activity.Stream is { } stream && points.Count != stream.PointCount)
            throw new InvalidDataException($"GPS samples for '{activity.Title}' do not match the saved stream. Reimport the activity before retrying exploration.");
        cancellationToken.ThrowIfCancellationRequested();
        var hasCoordinates = points.Any(point => point.Latitude is { } latitude && double.IsFinite(latitude) &&
            Math.Abs(latitude) <= ExplorationGrid.LatitudeLimit && point.Longitude is { } longitude &&
            double.IsFinite(longitude) && longitude is >= -180 and <= 180);
        var evidence = hasCoordinates
            ? await sourceReader.ReadAsync(points, activity.SourceFiles, cancellationToken)
            : new ExplorationSourceEvidence(true, new HashSet<int>(), null);
        var cells = hasCoordinates
            ? await Task.Run(() => ExplorationGrid.Extract(points, evidence.BreakBeforeIndices, evidence.VerifiedContinuity, cancellationToken), cancellationToken)
            : [];

        await using var ownerLock = await ownerMutationLock.AcquireAsync([activity.OwnerId], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var current = await db.Activities.AsNoTracking().Where(x => x.Id == activity.Id)
            .Select(x => new { x.OwnerId, x.ExplorationInputVersion }).SingleOrDefaultAsync(cancellationToken);
        if (current is null || current.OwnerId != activity.OwnerId || current.ExplorationInputVersion != activity.ExplorationInputVersion)
            return false;
        var currentSources = await db.SourceFiles.AsNoTracking().Where(x => x.ActivityId == activity.Id).ToArrayAsync(cancellationToken);
        if (!SourceIdentity(currentSources).SequenceEqual(SourceIdentity(activity.SourceFiles))) return false;
        await db.ActivityExplorationCells.Where(x => x.ActivityId == activity.Id).ExecuteDeleteAsync(cancellationToken);
        var header = await db.ActivityExplorationIndexes.SingleOrDefaultAsync(x => x.ActivityId == activity.Id, cancellationToken);
        if (header is null)
        {
            header = new ActivityExplorationIndex { ActivityId = activity.Id };
            db.ActivityExplorationIndexes.Add(header);
        }
        header.InputVersion = activity.ExplorationInputVersion;
        header.ComputationVersion = ComputationVersion;
        header.CellCount = cells.Count;
        header.IsLimited = hasCoordinates && !evidence.VerifiedContinuity;
        header.Diagnostic = evidence.Diagnostic;
        db.ActivityExplorationCells.AddRange(cells.Select(cellId => new ActivityExplorationCell { ActivityId = activity.Id, CellId = cellId }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static IQueryable<Activity> Scoped(ExplorerDbContext db, ExplorationScope scope) => db.Activities.AsNoTracking()
        .Where(x => (!scope.OwnerId.HasValue || x.OwnerId == scope.OwnerId) && (!scope.Sport.HasValue || x.Sport == scope.Sport));

    private static IEnumerable<(Guid Id, Guid Owner, Guid Batch, string Path, string Hash, int Parser, SourceKind Kind, string Name)>
        SourceIdentity(IEnumerable<SourceFile> sources) => sources.OrderBy(x => x.Id).Select(x =>
            (x.Id, x.OwnerId, x.ImportBatchId, x.StoredPath, x.Sha256, x.ParserVersion, x.SourceKind, x.OriginalName));

    private static void Validate(ExplorationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Sport.HasValue && !Enum.IsDefined(scope.Sport.Value)) throw new ArgumentException("Choose a valid sport.", nameof(scope));
    }

    private static void Report(IProgress<ExplorationBuildProgress>? progress, ExplorationIndexStatus status, int rechecking, bool confirmed = true) =>
        progress?.Report(new(Math.Max(0, status.IndexedActivities - rechecking), status.TotalActivities,
            rechecking > 0 ? "Rechecking limited source coverage…" : confirmed && status.IsComplete ? "Exploration index is ready." : "Building exploration from recorded activities…"));
}
