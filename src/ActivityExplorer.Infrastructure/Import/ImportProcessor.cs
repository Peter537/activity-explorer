using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ActivityExplorer.Infrastructure.Import;

public sealed class ImportProcessor(
    IDbContextFactory<ExplorerDbContext> contextFactory,
    IEnumerable<IActivityImporter> importers,
    AppDataPaths paths,
    IOriginalStore originalStore,
    IFileOperationCoordinator fileOperations,
    IOwnerMutationLock ownerMutationLock,
    IStatisticsService statistics,
    ISegmentService segments,
    ILogger<ImportProcessor> logger) : IImportProcessor
{
    public Task ProcessAsync(Guid importBatchId, CancellationToken cancellationToken = default) =>
        ownerMutationLock.RunAsync(async () => { await ProcessCoreAsync(importBatchId, cancellationToken); return true; }, cancellationToken);

    private async Task ProcessCoreAsync(Guid importBatchId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await db.ImportBatches.SingleOrDefaultAsync(x => x.Id == importBatchId, cancellationToken)
            ?? throw new InvalidOperationException("Import batch was not found.");
        await using var ownerLock = await ownerMutationLock.AcquireAsync([batch.OwnerId], cancellationToken);
        if (batch.Status is not (ImportStatus.Queued or ImportStatus.Interrupted)) return;
        var stagedPath = ResolveStagedPath(batch.StagedPath);

        batch.Status = ImportStatus.Running;
        batch.StartedAtUtc ??= DateTimeOffset.UtcNow;
        batch.ErrorMessage = null;
        await db.SaveChangesAsync(cancellationToken);

        var persistOutcomeSeparately = false;
        var preparingInput = true;
        var rejected = false;
        try
        {
            var importer = importers.FirstOrDefault(x => x.CanImport(stagedPath))
                ?? throw new InvalidDataException("Supported files are FIT, GPX, TCX, GZ, and ZIP archives.");
            string? diagnosticSummary = null;
            if (importer is ArchiveActivityImporter archive)
            {
                // OpenAsync validates and expands the entire archive before its first candidate can commit.
                using var plan = await archive.OpenAsync(stagedPath, batch.SourceKind, recoverableExtraction: true, cancellationToken);
                preparingInput = false;
                diagnosticSummary = await ProcessArchiveAsync(db, batch, plan, cancellationToken);
            }
            else
            {
                var candidates = await importer.ReadAsync(stagedPath, batch.SourceKind, cancellationToken);
                preparingInput = false;
                await ProcessStandaloneAsync(db, batch, candidates, cancellationToken);
            }

            var projectionPending = await RefreshProjectionsAsync(db, batch.OwnerId, cancellationToken);
            batch.Status = batch.Warnings > 0 || batch.UnsupportedSkipped > 0 || projectionPending
                ? ImportStatus.CompletedWithWarnings
                : ImportStatus.Completed;
            batch.Summary = $"Created {batch.ActivitiesCreated}, enriched {batch.ActivitiesUpdated}, skipped {batch.DuplicatesSkipped} duplicates." +
                (string.IsNullOrWhiteSpace(diagnosticSummary) ? string.Empty : $" {diagnosticSummary}") +
                (projectionPending ? " Records or segment efforts are pending repair." : string.Empty);
        }
        catch (UnsupportedActivityException exception)
        {
            batch.UnsupportedSkipped++;
            batch.Warnings++;
            batch.Status = ImportStatus.CompletedWithWarnings;
            batch.Summary = exception.Message;
        }
        catch (LegacyImportProgressException exception)
        {
            batch.Status = ImportStatus.ReviewRequired;
            batch.ErrorMessage = exception.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            persistOutcomeSeparately = true;
            batch.Status = ImportStatus.Interrupted;
            batch.ErrorMessage = "Import stopped because the application is shutting down.";
            throw;
        }
        catch (Exception exception)
        {
            persistOutcomeSeparately = true;
            rejected = preparingInput && exception is (UnsafeArchiveException or InvalidDataException or System.Xml.XmlException);
            batch.Status = ImportStatus.Failed;
            batch.ErrorMessage = Redact(exception.Message);
            logger.LogError(exception, "Import batch {ImportBatchId} failed.", batch.Id);
        }
        finally
        {
            var terminal = batch.Status is ImportStatus.Completed or ImportStatus.CompletedWithWarnings or ImportStatus.Failed;
            batch.CompletedAtUtc = terminal || batch.Status == ImportStatus.ReviewRequired ? DateTimeOffset.UtcNow : null;
            if (persistOutcomeSeparately)
            {
                await using var outcomeDb = await contextFactory.CreateDbContextAsync(CancellationToken.None);
                var persisted = await outcomeDb.ImportBatches.SingleAsync(x => x.Id == batch.Id, CancellationToken.None);
                persisted.Status = batch.Status;
                persisted.ErrorMessage = batch.ErrorMessage;
                persisted.CompletedAtUtc = batch.CompletedAtUtc;
                if (rejected && batch.SourceKind == SourceKind.WatchedFolder)
                {
                    var admission = await outcomeDb.Set<WatchedImportAdmission>()
                        .SingleOrDefaultAsync(x => x.BatchId == batch.Id, CancellationToken.None);
                    if (admission is not null) admission.Rejected = true;
                }
                await outcomeDb.SaveChangesAsync(CancellationToken.None);
            }
            else
            {
                await db.SaveChangesAsync(CancellationToken.None);
            }
            if (terminal) await CleanupStagingAsync(db, stagedPath);
        }
    }

    private async Task<bool> RefreshProjectionsAsync(ExplorerDbContext db, Guid ownerId, CancellationToken cancellationToken)
    {
        var pending = false;
        try { await statistics.RecomputeAsync(ownerId, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            pending = true;
            logger.LogWarning(exception, "Record refresh for profile {OwnerId} is pending repair.", ownerId);
        }

        try
        {
            var segmentIds = await ProjectionVersions.PendingSegments(db).Where(segment => segment.OwnerId == ownerId)
                .Select(segment => segment.Id).ToArrayAsync(cancellationToken);
            foreach (var segmentId in segmentIds)
            {
                try { await segments.RecomputeAsync(segmentId, cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    pending = true;
                    logger.LogWarning(exception, "Effort refresh for segment {SegmentId} is pending repair.", segmentId);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            pending = true;
            logger.LogWarning(exception, "Effort refresh for profile {OwnerId} is pending repair.", ownerId);
        }
        return pending;
    }

    private async Task<string?> ProcessArchiveAsync(ExplorerDbContext db, ImportBatch batch,
        ArchiveActivityImporter.ArchiveImportPlan plan, CancellationToken cancellationToken)
    {
        var checkpoint = await BeginEntriesAsync(db, batch, plan.EntryCount, plan.FilesDiscovered, plan.DiscoveryUnsupported,
            plan.DiscoveryWarnings, plan.ReadEntryAsync, cancellationToken);
        for (var index = checkpoint.NextEntry; index < plan.EntryCount; index++)
        {
            var entry = await plan.ReadEntryAsync(index, cancellationToken);
            checkpoint.NextEntry = index + 1;
            batch.UnsupportedSkipped += entry.Unsupported;
            batch.Warnings += entry.Warnings;
            if (entry.Candidate is { } candidate)
                await ProcessCandidateAsync(db, batch, candidate, cancellationToken);
            else
                await db.SaveChangesAsync(cancellationToken);
            ClearCandidateState(db, batch, checkpoint);
        }
        return plan.Diagnostics(batch.UnsupportedSkipped, batch.Warnings).Summary;
    }

    private async Task ProcessStandaloneAsync(ExplorerDbContext db, ImportBatch batch,
        IReadOnlyList<ImportCandidate> candidates, CancellationToken cancellationToken)
    {
        var checkpoint = await BeginEntriesAsync(db, batch, candidates.Count, candidates.Count, 0, 0,
            (index, _) => Task.FromResult(new ArchiveActivityImporter.ArchiveEntry(candidates[index], 0, 0)), cancellationToken);
        for (var index = checkpoint.NextEntry; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkpoint.NextEntry = index + 1;
            await ProcessCandidateAsync(db, batch, candidates[index], cancellationToken);
            ClearCandidateState(db, batch, checkpoint);
        }
    }

    private static async Task<ImportBatchCheckpoint> BeginEntriesAsync(ExplorerDbContext db, ImportBatch batch,
        int entries, int discovered, int unsupported, int warnings,
        Func<int, CancellationToken, Task<ArchiveActivityImporter.ArchiveEntry>> readEntry, CancellationToken cancellationToken)
    {
        var checkpoint = await db.Set<ImportBatchCheckpoint>()
            .SingleOrDefaultAsync(x => x.BatchId == batch.Id, cancellationToken);
        if (checkpoint is not null)
        {
            if (checkpoint.NextEntry < 0 || checkpoint.NextEntry > entries || batch.FilesDiscovered != discovered)
                throw new InvalidDataException("The staged import no longer matches its recorded progress.");
            return checkpoint;
        }

        var next = 0;
        if (batch.ActivitiesCreated > 0 || batch.ActivitiesUpdated > 0 || batch.DuplicatesSkipped > 0)
        {
            // Earlier versions counted some exact-source updates as both an update and a duplicate.
            // Resume only the prefix that saved provenance can identify without interpreting those counters.
            if (batch.ActivitiesUpdated != 0 || batch.DuplicatesSkipped != 0) throw new LegacyImportProgressException();
            var provenance = await db.SourceFiles.AsNoTracking().Where(x => x.ImportBatchId == batch.Id && x.ActivityId != null)
                .Select(x => new { x.Provider, x.Sha256, x.OriginalName }).ToArrayAsync(cancellationToken);
            var committed = provenance.Select(x => (x.Provider, x.Sha256, x.OriginalName)).ToHashSet();
            if (committed.Count != batch.ActivitiesCreated) throw new LegacyImportProgressException();
            while (committed.Count > 0)
            {
                if (next >= entries) throw new LegacyImportProgressException();
                var item = await readEntry(next++, cancellationToken);
                if (item.Candidate is { } candidate && !committed.Remove((candidate.Provider, candidate.Sha256, Path.GetFileName(candidate.OriginalName))))
                    throw new LegacyImportProgressException();
                unsupported += item.Unsupported;
                warnings += item.Warnings;
            }
        }
        batch.FilesDiscovered = discovered;
        batch.UnsupportedSkipped = unsupported;
        batch.Warnings = warnings;
        checkpoint = new ImportBatchCheckpoint { BatchId = batch.Id, NextEntry = next };
        db.Add(checkpoint);
        await db.SaveChangesAsync(cancellationToken);
        return checkpoint;
    }

    private sealed class LegacyImportProgressException() : Exception(
        "This import stopped before this version could record its exact progress. Its saved activities and staged file were kept. Review the results before importing the original again.");

    private static void ClearCandidateState(ExplorerDbContext db, ImportBatch batch, ImportBatchCheckpoint checkpoint)
    {
        // The cursor, report counters and candidate graph are durable together at this point.
        db.ChangeTracker.Clear();
        db.Attach(batch);
        db.Attach(checkpoint);
    }

    private async Task ProcessCandidateAsync(
        ExplorerDbContext db,
        ImportBatch batch,
        ImportCandidate candidate,
        CancellationToken cancellationToken)
    {
        Activity? activityById = null;
        if (candidate.Provider != SourceProvider.Unknown && !string.IsNullOrWhiteSpace(candidate.ExternalId))
        {
            activityById = await db.Activities
                .Include(x => x.Stream)
                .Include(x => x.Laps)
                .Include(x => x.SourceFiles)
                .Include(x => x.Metrics)
                .AsSplitQuery()
                .SingleOrDefaultAsync(x => x.OwnerId == batch.OwnerId &&
                    (candidate.Provider == SourceProvider.Garmin && x.GarminId == candidate.ExternalId ||
                     candidate.Provider == SourceProvider.Strava && x.StravaId == candidate.ExternalId),
                    cancellationToken);
        }


        var exactSource = await db.SourceFiles
            .Where(x => x.OwnerId == batch.OwnerId && (x.ActivityId != null || x.RouteId == null) && x.Sha256 == candidate.Sha256)
            .OrderByDescending(x => x.Provider == candidate.Provider)
            .FirstOrDefaultAsync(cancellationToken);
        if (exactSource is not null)
        {
            var originalProvider = exactSource.Provider;
            Activity? exactActivity = null;
            if (exactSource.ActivityId.HasValue)
            {
                exactActivity = await db.Activities
                    .Include(x => x.Stream)
                    .Include(x => x.Laps)
                    .Include(x => x.SourceFiles)
                    .Include(x => x.Metrics)
                    .AsSplitQuery()
                    .SingleOrDefaultAsync(x => x.Id == exactSource.ActivityId && x.OwnerId == batch.OwnerId, cancellationToken);
                if (exactActivity is not null)
                {
                    if (exactSource.ParserVersion < candidate.ParserVersion) ReplaceTechnicalData(exactActivity, candidate.Parsed);
                    else EnrichActivity(exactActivity, candidate.Parsed, batch.SourceKind, candidate.OriginalName);
                    ApplyExternalId(exactActivity, candidate.Provider, candidate.ExternalId);
                    batch.ActivitiesUpdated++;
                }
            }

            var sameProvenance = candidate.Provider == SourceProvider.Unknown ||
                                 originalProvider == SourceProvider.Unknown ||
                                 originalProvider == candidate.Provider;
            if (sameProvenance)
            {
                exactSource.ExternalId ??= candidate.ExternalId;
                if (exactSource.Provider == SourceProvider.Unknown) exactSource.Provider = candidate.Provider;
                exactSource.AcquisitionMethod = candidate.AcquisitionMethod;
                exactSource.ParserVersion = Math.Max(exactSource.ParserVersion, candidate.ParserVersion);
            }
            else if (!await db.SourceFiles.AnyAsync(x =>
                         x.OwnerId == batch.OwnerId && (x.ActivityId != null || x.RouteId == null) && x.Provider == candidate.Provider && x.Sha256 == candidate.Sha256,
                         cancellationToken))
            {
                if (exactActivity is not null) exactActivity.ExplorationInputVersion++;
                db.SourceFiles.Add(new SourceFile
                {
                    OwnerId = batch.OwnerId,
                    ImportBatchId = batch.Id,
                    ActivityId = exactSource.ActivityId,
                    SourceKind = batch.SourceKind,
                    Provider = candidate.Provider,
                    AcquisitionMethod = candidate.AcquisitionMethod,
                    ParserVersion = candidate.ParserVersion,
                    OriginalName = Path.GetFileName(candidate.OriginalName),
                    StoredPath = exactSource.StoredPath,
                    Sha256 = candidate.Sha256,
                    ExternalId = candidate.ExternalId,
                    Length = candidate.Length
                });
            }

            batch.DuplicatesSkipped++;
            await SaveCandidateAsync(db, exactActivity, candidate.Parsed.Points, cancellationToken);
            return;
        }

        var fingerprint = Fingerprint.For(candidate.Parsed);
        var activity = activityById ?? await db.Activities
            .Include(x => x.Stream)
            .Include(x => x.Laps)
            .Include(x => x.SourceFiles)
            .Include(x => x.Metrics)
            .AsSplitQuery()
            .SingleOrDefaultAsync(x => x.OwnerId == batch.OwnerId && x.NaturalFingerprint == fingerprint, cancellationToken);
        var created = activity is null;
        if (created)
        {
            activity = CreateActivity(batch.OwnerId, candidate.Parsed, fingerprint, candidate.ParserVersion);
            db.Activities.Add(activity);
            batch.ActivitiesCreated++;
        }
        else
        {
            EnrichActivity(activity!, candidate.Parsed, batch.SourceKind, candidate.OriginalName);
            batch.ActivitiesUpdated++;
        }

        var extension = Path.GetExtension(candidate.OriginalName).ToLowerInvariant();
        var target = originalStore.GetOriginalTarget(batch.OwnerId, candidate.Sha256, extension);
        PreparedFileOperation? fileOperation = null;
        try
        {
            fileOperation = await fileOperations.PrepareCopyAsync(
                batch.OwnerId, activity!.Id, candidate.FilePath, target, candidate.Sha256,
                cancellationToken: cancellationToken);
            db.SourceFiles.Add(new SourceFile
            {
                OwnerId = batch.OwnerId,
                ImportBatchId = batch.Id,
                ActivityId = activity.Id,
                SourceKind = batch.SourceKind,
                Provider = candidate.Provider,
                AcquisitionMethod = candidate.AcquisitionMethod,
                ParserVersion = candidate.ParserVersion,
                OriginalName = Path.GetFileName(candidate.OriginalName),
                StoredPath = fileOperation.TargetRelativePath,
                Sha256 = candidate.Sha256,
                ExternalId = candidate.ExternalId,
                Length = candidate.Length
            });
            activity.ExplorationInputVersion++;

            ApplyExternalId(activity, candidate.Provider, candidate.ExternalId);
            await SaveCandidateAsync(db, activity, candidate.Parsed.Points, cancellationToken);
            await fileOperations.CommitAsync(fileOperation.OperationId, cancellationToken);
        }
        catch
        {
            if (fileOperation is not null)
                await fileOperations.RollbackAsync(fileOperation.OperationId, CancellationToken.None);
            throw;
        }
    }

    private static async Task SaveCandidateAsync(ExplorerDbContext db, Activity? activity, IReadOnlyList<TrackPoint> points,
        CancellationToken cancellationToken)
    {
        if (activity?.Stream is not { } stream)
        {
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        db.ChangeTracker.DetectChanges();
        var entry = db.Entry(stream);
        if (entry.State != EntityState.Added && !entry.Property(value => value.CompressedPayload).IsModified &&
            !entry.Property(value => value.SchemaVersion).IsModified &&
            !db.Entry(activity).Property(value => value.Sport).IsModified)
        {
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var values = RecordEffortReadModel.Build(points, activity.Sport, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await RecordEffortReadModel.SaveAsync(db, activity.Id, activity.Sport, values, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static Activity CreateActivity(Guid ownerId, ParsedActivity parsed, string fingerprint, int parserVersion)
    {
        var bounds = GeometryCodec.Bounds(parsed.Points);
        var geometry = GeometryCodec.ToWkb(parsed.Points);
        return new Activity
        {
            OwnerId = ownerId,
            Sport = parsed.Sport,
            Title = parsed.Title,
            Description = parsed.Description,
            DeviceName = parsed.DeviceName,
            GearName = parsed.GearName,
            NaturalFingerprint = fingerprint,
            StartTimeUtc = parsed.StartTimeUtc,
            OriginalUtcOffset = parsed.OriginalUtcOffset,
            DistanceMeters = parsed.DistanceMeters,
            MovingTimeSeconds = parsed.MovingTimeSeconds,
            TimerTimeSeconds = parsed.TimerTimeSeconds,
            MovingTimeSource = parsed.MovingTimeSource,
            ElapsedTimeSeconds = parsed.ElapsedTimeSeconds,
            ElevationGainMeters = parsed.ElevationGainMeters,
            ElevationLossMeters = parsed.ElevationLossMeters,
            MinElevationMeters = parsed.MinElevationMeters,
            MaxElevationMeters = parsed.MaxElevationMeters,
            Calories = parsed.Calories,
            RestingCalories = parsed.RestingCalories,
            ActiveCalories = parsed.ActiveCalories,
            AverageSpeedMetersPerSecond = parsed.AverageSpeedMetersPerSecond,
            MaxSpeedMetersPerSecond = parsed.MaxSpeedMetersPerSecond,
            AverageHeartRate = parsed.AverageHeartRate,
            MaxHeartRate = parsed.MaxHeartRate,
            AverageCadence = parsed.AverageCadence,
            MaxCadence = parsed.MaxCadence,
            PedalRevolutions = parsed.PedalRevolutions,
            AveragePowerWatts = parsed.AveragePowerWatts,
            MaxPowerWatts = parsed.MaxPowerWatts,
            NormalizedPowerWatts = parsed.NormalizedPowerWatts,
            Kilojoules = parsed.Kilojoules,
            AverageTemperatureCelsius = parsed.AverageTemperatureCelsius,
            MinTemperatureCelsius = parsed.MinTemperatureCelsius,
            MaxTemperatureCelsius = parsed.MaxTemperatureCelsius,
            AverageRespirationRate = parsed.AverageRespirationRate,
            MinRespirationRate = parsed.MinRespirationRate,
            MaxRespirationRate = parsed.MaxRespirationRate,
            AerobicTrainingEffect = parsed.AerobicTrainingEffect,
            AnaerobicTrainingEffect = parsed.AnaerobicTrainingEffect,
            TrainingLoad = parsed.TrainingLoad,
            TechnicalDataVersion = parserVersion,
            MinLatitude = bounds.MinLat,
            MinLongitude = bounds.MinLon,
            MaxLatitude = bounds.MaxLat,
            MaxLongitude = bounds.MaxLon,
            GeometryWkb = geometry,
            SimplifiedGeometryWkb = GeometryCodec.ToWkb(parsed.Points, 0.00005),
            HasGps = geometry is not null,
            IsIndoor = ResolveIndoor(parsed, geometry),
            HasPower = parsed.Points.Any(x => x.PowerWatts.HasValue),
            Stream = new ActivityStream
            {
                OwnerId = ownerId,
                SchemaVersion = 1,
                CompressedPayload = TrackCodec.Encode(parsed.Points),
                PointCount = parsed.Points.Count
            },
            Laps = parsed.Laps.Select(x => new ActivityLap
            {
                OwnerId = ownerId,
                Sequence = x.Sequence,
                DistanceMeters = x.DistanceMeters,
                ElapsedSeconds = x.ElapsedSeconds,
                MovingSeconds = x.MovingSeconds,
                AverageHeartRate = x.AverageHeartRate,
                AveragePowerWatts = x.AveragePowerWatts
            }).ToList(),
            Metrics = parsed.Metrics.Select(x => new ActivityMetric
            {
                OwnerId = ownerId,
                Key = x.Key,
                Label = x.Label,
                NumericValue = x.NumericValue,
                TextValue = x.TextValue,
                Unit = x.Unit,
                Origin = ActivityMetricOrigin.Imported
            }).ToList()
        };
    }

    private static void EnrichActivity(
        Activity target, ParsedActivity source, SourceKind sourceKind, string incomingName)
    {
        var previousMutationVersion = target.MutationVersion;
        if (!target.UserEdited && sourceKind == SourceKind.StravaArchive)
        {
            target.Title = source.Title;
            target.Description = source.Description ?? target.Description;
            target.GearName = source.GearName ?? target.GearName;
        }

        var currentPriority = target.SourceFiles.Count == 0
            ? 0
            : target.SourceFiles.Max(x => TechnicalPriority(x.OriginalName));
        if (TechnicalPriority(incomingName) > currentPriority ||
            target.TechnicalDataVersion < FitActivityImporter.CurrentParserVersion && TechnicalPriority(incomingName) == 3)
        {
            ReplaceTechnicalData(target, source);
        }
        else
        {
            target.DeviceName ??= source.DeviceName;
            target.Calories ??= source.Calories;
            target.AverageHeartRate ??= source.AverageHeartRate;
            target.MaxHeartRate ??= source.MaxHeartRate;
            target.AverageCadence ??= source.AverageCadence;
            target.AveragePowerWatts ??= source.AveragePowerWatts;
            target.MaxPowerWatts ??= source.MaxPowerWatts;
            target.NormalizedPowerWatts ??= source.NormalizedPowerWatts;
            target.Kilojoules ??= source.Kilojoules;
        }
        if (source.IsIndoor.HasValue) target.IsIndoor = source.IsIndoor.Value;
        target.UpdatedAtUtc = DateTimeOffset.UtcNow;
        target.MutationVersion = previousMutationVersion + 1;
    }

    internal static void ReplaceTechnicalData(Activity target, ParsedActivity source, int parserVersion = FitActivityImporter.CurrentParserVersion)
    {
        target.MutationVersion++;
        target.ExplorationInputVersion++;
        target.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var bounds = GeometryCodec.Bounds(source.Points);
        var geometry = GeometryCodec.ToWkb(source.Points);
        target.Sport = source.Sport;
        target.DeviceName = source.DeviceName ?? target.DeviceName;
        target.StartTimeUtc = source.StartTimeUtc;
        target.OriginalUtcOffset = source.OriginalUtcOffset ?? target.OriginalUtcOffset;
        target.DistanceMeters = source.DistanceMeters;
        target.MovingTimeSeconds = source.MovingTimeSeconds;
        target.TimerTimeSeconds = source.TimerTimeSeconds;
        target.MovingTimeSource = source.MovingTimeSource;
        target.ElapsedTimeSeconds = source.ElapsedTimeSeconds;
        target.ElevationGainMeters = source.ElevationGainMeters;
        target.ElevationLossMeters = source.ElevationLossMeters;
        target.MinElevationMeters = source.MinElevationMeters;
        target.MaxElevationMeters = source.MaxElevationMeters;
        target.Calories = source.Calories;
        target.AverageSpeedMetersPerSecond = source.AverageSpeedMetersPerSecond;
        target.RestingCalories = source.RestingCalories;
        target.ActiveCalories = source.ActiveCalories;
        target.MaxSpeedMetersPerSecond = source.MaxSpeedMetersPerSecond;
        target.AverageHeartRate = source.AverageHeartRate;
        target.MaxHeartRate = source.MaxHeartRate;
        target.AverageCadence = source.AverageCadence;
        target.AveragePowerWatts = source.AveragePowerWatts;
        target.MaxCadence = source.MaxCadence;
        target.PedalRevolutions = source.PedalRevolutions;
        target.MaxPowerWatts = source.MaxPowerWatts;
        target.NormalizedPowerWatts = source.NormalizedPowerWatts;
        target.Kilojoules = source.Kilojoules;
        target.AverageTemperatureCelsius = source.AverageTemperatureCelsius;
        target.MinTemperatureCelsius = source.MinTemperatureCelsius;
        target.MaxTemperatureCelsius = source.MaxTemperatureCelsius;
        target.AverageRespirationRate = source.AverageRespirationRate;
        target.MinRespirationRate = source.MinRespirationRate;
        target.MaxRespirationRate = source.MaxRespirationRate;
        target.AerobicTrainingEffect = source.AerobicTrainingEffect;
        target.AnaerobicTrainingEffect = source.AnaerobicTrainingEffect;
        target.TrainingLoad = source.TrainingLoad;
        target.TechnicalDataVersion = parserVersion;
        target.MinLatitude = bounds.MinLat;
        target.MinLongitude = bounds.MinLon;
        target.MaxLatitude = bounds.MaxLat;
        target.MaxLongitude = bounds.MaxLon;
        target.GeometryWkb = geometry;
        target.SimplifiedGeometryWkb = GeometryCodec.ToWkb(source.Points, 0.00005);
        target.HasGps = geometry is not null;
        target.IsIndoor = ResolveIndoor(source, geometry);
        target.HasPower = source.Points.Any(x => x.PowerWatts.HasValue);
        target.Stream ??= new ActivityStream { OwnerId = target.OwnerId };
        target.Stream.SchemaVersion = 1;
        target.Stream.CompressedPayload = TrackCodec.Encode(source.Points);
        target.Stream.PointCount = source.Points.Count;
        target.Laps.Clear();
        target.Laps.AddRange(source.Laps.Select(x => new ActivityLap
        {
            OwnerId = target.OwnerId,
            Sequence = x.Sequence,
            DistanceMeters = x.DistanceMeters,
            ElapsedSeconds = x.ElapsedSeconds,
            MovingSeconds = x.MovingSeconds,
            AverageHeartRate = x.AverageHeartRate,
            AveragePowerWatts = x.AveragePowerWatts
        }));
        var importedMetrics = target.Metrics.Where(x => x.Origin == ActivityMetricOrigin.Imported).ToArray();
        foreach (var metric in importedMetrics) target.Metrics.Remove(metric);
        target.Metrics.AddRange(source.Metrics.Select(x => new ActivityMetric
        {
            OwnerId = target.OwnerId,
            Key = x.Key,
            Label = x.Label,
            NumericValue = x.NumericValue,
            TextValue = x.TextValue,
            Unit = x.Unit,
            Origin = ActivityMetricOrigin.Imported
        }));

    }

    private static bool ResolveIndoor(ParsedActivity activity, byte[]? geometry) =>
        activity.IsIndoor ?? geometry is null;

    private static int TechnicalPriority(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".fit" => 3,
            ".tcx" => 2,
            ".gpx" => 1,
            _ => 0
        };

    private static void ApplyExternalId(Activity target, SourceProvider provider, string? externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return;
        if (provider == SourceProvider.Strava) target.StravaId ??= externalId;
        if (provider == SourceProvider.Garmin) target.GarminId ??= externalId;
    }

    private string ResolveStagedPath(string storedPath)
    {
        var candidate = Path.IsPathRooted(storedPath)
            ? storedPath
            : Path.Combine(paths.Root, storedPath.Replace('/', Path.DirectorySeparatorChar));
        return ManagedPathGuard.ResolveUnder(paths.StagingPath, candidate);
    }

    private async Task CleanupStagingAsync(ExplorerDbContext db, string stagedPath)
    {
        try
        {
            var resolved = ManagedPathGuard.ResolveUnder(paths.StagingPath, stagedPath);
            var parent = Path.GetDirectoryName(resolved);
            if (parent is null || !Guid.TryParseExact(Path.GetFileName(parent), "N", out _) ||
                !string.Equals(Path.GetDirectoryName(parent), Path.GetFullPath(paths.StagingPath),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
            var active = await StagingRecovery.GetProtectedNamesAsync(db, paths, CancellationToken.None);
            if (!active.Contains(Path.GetFileName(parent)))
                ManagedStagingDirectory.DeleteOwned(paths.StagingPath, parent);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Could not clean import staging: {Reason}", Redact(exception.Message));
        }
    }

    private static string Redact(string message)
    {
        var fileName = Path.GetFileName(message);
        return message.Length > 500 ? fileName[..Math.Min(fileName.Length, 500)] : message.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<user>", StringComparison.OrdinalIgnoreCase);
    }
}
