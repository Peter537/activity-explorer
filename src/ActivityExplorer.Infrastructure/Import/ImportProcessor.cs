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
    public async Task ProcessAsync(Guid importBatchId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await db.ImportBatches.SingleOrDefaultAsync(x => x.Id == importBatchId, cancellationToken)
            ?? throw new InvalidOperationException("Import batch was not found.");
        await using var ownerLock = await ownerMutationLock.AcquireAsync([batch.OwnerId], cancellationToken);
        var stagedPath = ResolveStagedPath(batch.StagedPath);

        batch.Status = ImportStatus.Running;
        batch.StartedAtUtc ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var importer = importers.FirstOrDefault(x => x.CanImport(stagedPath))
                ?? throw new InvalidDataException("Supported files are FIT, GPX, TCX, GZ, and ZIP archives.");
            var candidates = await importer.ReadAsync(stagedPath, batch.SourceKind, cancellationToken);
            var diagnostics = importer is IImporterDiagnosticsSource diagnosticSource
                ? diagnosticSource.ConsumeDiagnostics()
                : new ImporterDiagnostics(0, 0, null);
            batch.FilesDiscovered = candidates.Count + diagnostics.Unsupported + diagnostics.Warnings;
            batch.UnsupportedSkipped += diagnostics.Unsupported;
            batch.Warnings += diagnostics.Warnings;
            await db.SaveChangesAsync(cancellationToken);

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessCandidateAsync(db, batch, candidate, cancellationToken);
            }

            await statistics.RecomputeAsync(batch.OwnerId, cancellationToken);
            var segmentIds = await db.Segments.AsNoTracking()
                .Where(x => x.OwnerId == batch.OwnerId)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
            foreach (var segmentId in segmentIds)
            {
                await segments.RecomputeAsync(segmentId, cancellationToken);
            }

            batch.Status = batch.Warnings > 0 || batch.UnsupportedSkipped > 0
                ? ImportStatus.CompletedWithWarnings
                : ImportStatus.Completed;
            batch.Summary = $"Created {batch.ActivitiesCreated}, enriched {batch.ActivitiesUpdated}, skipped {batch.DuplicatesSkipped} duplicates." +
                (string.IsNullOrWhiteSpace(diagnostics.Summary) ? string.Empty : $" {diagnostics.Summary}");
        }
        catch (UnsupportedActivityException exception)
        {
            batch.UnsupportedSkipped++;
            batch.Warnings++;
            batch.Status = ImportStatus.CompletedWithWarnings;
            batch.Summary = exception.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            batch.Status = ImportStatus.Interrupted;
            batch.ErrorMessage = "Import stopped because the application is shutting down.";
            throw;
        }
        catch (Exception exception)
        {
            batch.Status = ImportStatus.Failed;
            batch.ErrorMessage = Redact(exception.Message);
            logger.LogError(exception, "Import batch {ImportBatchId} failed.", batch.Id);
        }
        finally
        {
            var terminal = batch.Status is ImportStatus.Completed or ImportStatus.CompletedWithWarnings or ImportStatus.Failed;
            batch.CompletedAtUtc = terminal ? DateTimeOffset.UtcNow : null;
            await db.SaveChangesAsync(CancellationToken.None);
            if (terminal) CleanupStaging(stagedPath);
        }
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
            .Where(x => x.OwnerId == batch.OwnerId && x.Sha256 == candidate.Sha256)
            .OrderByDescending(x => x.Provider == candidate.Provider)
            .FirstOrDefaultAsync(cancellationToken);
        if (exactSource is not null)
        {
            var originalProvider = exactSource.Provider;
            if (exactSource.ActivityId.HasValue)
            {
                var exactActivity = await db.Activities
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
                         x.OwnerId == batch.OwnerId && x.Provider == candidate.Provider && x.Sha256 == candidate.Sha256,
                         cancellationToken))
            {
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
            await db.SaveChangesAsync(cancellationToken);
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
            activity.SourceFiles.Add(new SourceFile
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

            ApplyExternalId(activity, candidate.Provider, candidate.ExternalId);
            await db.SaveChangesAsync(cancellationToken);
            await fileOperations.CommitAsync(fileOperation.OperationId, cancellationToken);
        }
        catch
        {
            if (fileOperation is not null)
                await fileOperations.RollbackAsync(fileOperation.OperationId, CancellationToken.None);
            throw;
        }
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
    }

    internal static void ReplaceTechnicalData(Activity target, ParsedActivity source, int parserVersion = FitActivityImporter.CurrentParserVersion)
    {
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

    private void CleanupStaging(string stagedPath)
    {
        try
        {
            var resolved = ManagedPathGuard.ResolveUnder(paths.StagingPath, stagedPath);
            var parent = Path.GetDirectoryName(resolved);
            if (parent is not null &&
                !string.Equals(parent, Path.GetFullPath(paths.StagingPath), StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
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
