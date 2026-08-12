using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class ActivityQueryService(
    IDbContextFactory<ExplorerDbContext> contextFactory,
    IStatisticsService statistics,
    ISegmentService segments,
    IOriginalStore originals,
    IFileOperationCoordinator fileOperations,
    IOwnerMutationLock ownerMutationLock,
    ILogger<ActivityQueryService> logger) : IActivityQueryService
{
    public async Task<PagedResult<ActivitySummary>> SearchAsync(ActivityFilter filter, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = ApplySort(
            ApplyFilter(db.Activities.AsNoTracking().Include(x => x.Owner), filter),
            filter.Sort);

        var total = await query.CountAsync(cancellationToken);
        var page = Math.Max(filter.Page, 1);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var activities = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResult<ActivitySummary>(activities.Select(ToSummary).ToArray(), total, page, pageSize);
    }

    public async Task<IReadOnlyList<Guid>> GetMatchingActivityIdsAsync(
        ActivityFilter filter,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await ApplySort(ApplyFilter(db.Activities.AsNoTracking(), filter), filter.Sort)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<ActivityDetail?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var activity = await db.Activities.AsNoTracking()
            .Include(x => x.Owner)
            .Include(x => x.Stream)
            .Include(x => x.Laps)
            .Include(x => x.SourceFiles)
            .Include(x => x.Metrics)
            .AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (activity is null) return null;

        var efforts = await db.SegmentEfforts.AsNoTracking()
            .Include(x => x.Segment)
            .Where(x => x.ActivityId == id)
            .OrderBy(x => x.StartTimeUtc)
            .Select(x => new SegmentEffortSummary(
                x.Id, x.ActivityId, x.SegmentId, x.Segment!.Name, x.ElapsedSeconds, x.Rank, x.StartTimeUtc, x.StartPointIndex, x.EndPointIndex,
                x.MovingSeconds, x.AverageSpeedMetersPerSecond, x.MaxSpeedMetersPerSecond,
                x.AverageHeartRate, x.MaxHeartRate, x.AverageCadence, x.MaxCadence, x.AveragePowerWatts, x.MaxPowerWatts,
                x.AverageTemperatureCelsius, x.AverageRespirationRate, x.RecordedDistanceMeters,
                x.ElevationGainMeters, x.ElevationLossMeters, x.AverageGradePercent, x.CoveragePercent,
                x.MetricComputationVersion >= SegmentEffortMetricVersions.Current))
            .ToListAsync(cancellationToken);

        return new ActivityDetail(
            ToSummary(activity),
            activity.Description,
            activity.GearName,
            activity.ElapsedTimeSeconds,
            activity.Calories,
            activity.AverageSpeedMetersPerSecond,
            activity.MaxSpeedMetersPerSecond,
            activity.AverageHeartRate,
            activity.MaxHeartRate,
            activity.AverageCadence,
            activity.MaxPowerWatts,
            activity.Kilojoules,
            activity.Stream is null ? [] : TrackCodec.Decode(activity.Stream.CompressedPayload),
            activity.Laps.OrderBy(x => x.Sequence)
                .Select(x => new LapCandidate(x.Sequence, x.DistanceMeters, x.ElapsedSeconds, x.MovingSeconds, x.AverageHeartRate, x.AveragePowerWatts))
                .ToArray(),
            activity.SourceFiles.OrderByDescending(x => x.ImportedAtUtc)
                .Select(x => new SourceFileSummary(x.Id, x.OriginalName, x.SourceKind, x.Length, x.ImportedAtUtc,
                    x.Provider, x.AcquisitionMethod, x.ExternalId, x.ParserVersion))
                .ToArray(),
            efforts,
            new ActivityRichSummary(
                activity.TimerTimeSeconds, activity.MovingTimeSource,
                activity.ElevationLossMeters, activity.MinElevationMeters, activity.MaxElevationMeters,
                activity.RestingCalories, activity.ActiveCalories,
                activity.MaxCadence, activity.PedalRevolutions,
                activity.AverageTemperatureCelsius, activity.MinTemperatureCelsius, activity.MaxTemperatureCelsius,
                activity.AverageRespirationRate, activity.MinRespirationRate, activity.MaxRespirationRate,
                activity.AerobicTrainingEffect, activity.AnaerobicTrainingEffect, activity.TrainingLoad),
            activity.Metrics.OrderBy(x => x.Label)
                .Select(x => new ActivityMetricSummary(x.Id, x.Key, x.Label, x.NumericValue, x.TextValue, x.Unit, x.Origin, x.UpdatedAtUtc))
                .ToArray());
    }

    public async Task<DashboardSummary> GetDashboardAsync(Guid? ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Activities.AsNoTracking().Include(x => x.Owner).AsQueryable();
        if (ownerId.HasValue) query = query.Where(x => x.OwnerId == ownerId);

        var totals = await query.GroupBy(_ => 1).Select(x => new
        {
            Count = x.Count(),
            Distance = x.Sum(a => a.DistanceMeters),
            Moving = x.Sum(a => a.MovingTimeSeconds),
            Elevation = x.Sum(a => a.ElevationGainMeters)
        }).SingleOrDefaultAsync(cancellationToken);

        var sports = await query.GroupBy(x => x.Sport).Select(x => new SportTotal(
            x.Key, x.Count(), x.Sum(a => a.DistanceMeters), x.Sum(a => a.MovingTimeSeconds), x.Sum(a => a.ElevationGainMeters)))
            .ToListAsync(cancellationToken);
        var recentEntities = await query.OrderByDescending(x => x.StartTimeUtc).Take(8).ToListAsync(cancellationToken);
        var trendSource = await query.Select(x => new
        {
            x.StartTimeUtc,
            x.DistanceMeters,
            x.MovingTimeSeconds,
            x.DeviceName,
            x.GearName
        }).ToListAsync(cancellationToken);
        var currentMonth = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var monthlyTrend = Enumerable.Range(0, 12).Select(offset => currentMonth.AddMonths(offset - 11))
            .Select(month =>
            {
                var rows = trendSource.Where(x =>
                    x.StartTimeUtc.Year == month.Year && x.StartTimeUtc.Month == month.Month).ToArray();
                return new PeriodTotal(month, rows.Length, rows.Sum(x => x.DistanceMeters), rows.Sum(x => x.MovingTimeSeconds));
            }).ToArray();
        var devices = trendSource.Where(x => !string.IsNullOrWhiteSpace(x.DeviceName))
            .GroupBy(x => x.DeviceName!, StringComparer.OrdinalIgnoreCase)
            .Select(x => new NamedTotal(x.Key, x.Count())).OrderByDescending(x => x.Count).ThenBy(x => x.Name).Take(5).ToArray();
        var gear = trendSource.Where(x => !string.IsNullOrWhiteSpace(x.GearName))
            .GroupBy(x => x.GearName!, StringComparer.OrdinalIgnoreCase)
            .Select(x => new NamedTotal(x.Key, x.Count())).OrderByDescending(x => x.Count).ThenBy(x => x.Name).Take(5).ToArray();
        var records = await statistics.GetRecordsAsync(ownerId, RecordScope.All, cancellationToken);
        var warningQuery = db.ImportBatches.AsNoTracking().Where(x => x.Warnings > 0 || x.Status == ImportStatus.Failed);
        if (ownerId.HasValue) warningQuery = warningQuery.Where(x => x.OwnerId == ownerId);
        var warnings = await warningQuery.CountAsync(cancellationToken);

        return new DashboardSummary(
            totals?.Count ?? 0,
            totals?.Distance ?? 0,
            totals?.Moving ?? 0,
            totals?.Elevation ?? 0,
            sports,
            recentEntities.Select(ToSummary).ToArray(),
            records.Take(5).ToArray(),
            warnings,
            monthlyTrend,
            devices,
            gear);
    }

    public async Task<ActivityDeletionResult> DeleteAsync(
        IReadOnlyCollection<Guid> activityIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activityIds);
        var ids = activityIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) throw new ArgumentException("Select at least one activity to delete.", nameof(activityIds));

        await using var probe = await contextFactory.CreateDbContextAsync(cancellationToken);
        var probedActivities = await probe.Activities.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.OwnerId })
            .ToListAsync(cancellationToken);
        if (probedActivities.Count != ids.Length)
            throw new InvalidOperationException("One or more selected activities no longer exist. Refresh the selection and try again.");
        var lockedOwnerIds = probedActivities.Select(x => x.OwnerId).Distinct().Order().ToArray();

        await using var ownerLock = await ownerMutationLock.AcquireAsync(lockedOwnerIds, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var activities = await db.Activities
            .Include(x => x.SourceFiles)
            .AsSplitQuery()
            .Where(x => ids.Contains(x.Id))
            .ToListAsync(cancellationToken);
        if (activities.Count != ids.Length || activities.Any(x => !lockedOwnerIds.Contains(x.OwnerId)))
            throw new InvalidOperationException("One or more selected activities changed. Refresh the selection and try again.");

        var ownerIds = activities.Select(x => x.OwnerId).Distinct().Order().ToArray();
        var sourceRows = activities.SelectMany(x => x.SourceFiles).ToArray();
        var sourcePaths = sourceRows
            .GroupBy(x => x.StoredPath, StringComparer.Ordinal)
            .Select(group => new
            {
                StoredPath = group.Key,
                OwnerId = group.First().OwnerId,
                EntityId = group.Select(x => (Guid?)x.ActivityId).FirstOrDefault()
            })
            .ToArray();
        var storedPaths = sourcePaths.Select(x => x.StoredPath).ToArray();
        var retainedPaths = storedPaths.Length == 0
            ? []
            : await db.SourceFiles.AsNoTracking()
                .Where(x => storedPaths.Contains(x.StoredPath) &&
                            (!x.ActivityId.HasValue || !ids.Contains(x.ActivityId.Value)))
                .Select(x => x.StoredPath)
                .Distinct()
                .ToArrayAsync(cancellationToken);
        var retainedPathSet = retainedPaths.ToHashSet(StringComparer.Ordinal);
        var affectedSegmentIds = await db.SegmentEfforts.AsNoTracking()
            .Where(x => ids.Contains(x.ActivityId))
            .Select(x => x.SegmentId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var preparedOperations = new List<Guid>();

        try
        {
            foreach (var source in sourcePaths.Where(x => !retainedPathSet.Contains(x.StoredPath)))
            {
                var operationId = await fileOperations.QuarantineFileAsync(
                    source.OwnerId, source.EntityId, source.StoredPath, cancellationToken);
                if (operationId.HasValue) preparedOperations.Add(operationId.Value);
            }

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            db.SourceFiles.RemoveRange(sourceRows);
            db.StatisticSnapshots.RemoveRange(db.StatisticSnapshots.Where(x => ownerIds.Contains(x.OwnerId)));
            db.Activities.RemoveRange(activities);
            await db.SaveChangesAsync(cancellationToken);

            foreach (var segmentId in affectedSegmentIds)
            {
                var remainingEfforts = await db.SegmentEfforts
                    .Where(x => x.SegmentId == segmentId)
                    .OrderBy(x => x.ElapsedSeconds)
                    .ThenBy(x => x.StartTimeUtc)
                    .ThenBy(x => x.Id)
                    .ToListAsync(cancellationToken);
                for (var index = 0; index < remainingEfforts.Count; index++)
                    remainingEfforts[index].Rank = index + 1;
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            foreach (var operationId in preparedOperations.AsEnumerable().Reverse())
            {
                try { await fileOperations.RollbackAsync(operationId, CancellationToken.None); }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Could not roll back activity deletion file operation {OperationId}.", operationId);
                }
            }
            throw;
        }

        var fileCleanupPending = false;
        foreach (var operationId in preparedOperations)
        {
            try { await fileOperations.CommitAsync(operationId, CancellationToken.None); }
            catch (Exception exception)
            {
                fileCleanupPending = true;
                logger.LogError(exception, "Activity deletion file cleanup {OperationId} is pending recovery.", operationId);
            }
        }

        var statisticsRefreshPending = false;
        foreach (var ownerId in ownerIds)
        {
            try { await statistics.RecomputeAsync(ownerId, CancellationToken.None); }
            catch (Exception exception)
            {
                statisticsRefreshPending = true;
                logger.LogError(exception, "Activity deletion statistics refresh for owner {OwnerId} is pending.", ownerId);
            }
        }

        return new ActivityDeletionResult(ids.Length, fileCleanupPending, statisticsRefreshPending);
    }

    public async Task<Guid> AddMetricAsync(Guid activityId, ActivityMetricRequest request, CancellationToken cancellationToken = default)
    {
        ValidateMetric(request);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var activity = await db.Activities.SingleOrDefaultAsync(x => x.Id == activityId, cancellationToken)
            ?? throw new InvalidOperationException("Activity was not found.");
        var key = NormalizeMetricKey(request.Key, request.Label);
        if (await db.ActivityMetrics.AnyAsync(x => x.ActivityId == activityId && x.Key == key && x.Origin == ActivityMetricOrigin.Manual, cancellationToken))
            throw new InvalidOperationException("A custom metric with this key already exists.");
        var metric = new ActivityMetric
        {
            OwnerId = activity.OwnerId,
            ActivityId = activityId,
            Key = key,
            Label = request.Label.Trim(),
            NumericValue = request.NumericValue,
            TextValue = Clean(request.TextValue),
            Unit = Clean(request.Unit),
            Origin = ActivityMetricOrigin.Manual
        };
        db.ActivityMetrics.Add(metric);
        await db.SaveChangesAsync(cancellationToken);
        return metric.Id;
    }

    public async Task UpdateMetricAsync(Guid activityId, Guid metricId, ActivityMetricRequest request, CancellationToken cancellationToken = default)
    {
        ValidateMetric(request);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var metric = await db.ActivityMetrics
            .SingleOrDefaultAsync(x => x.Id == metricId && x.ActivityId == activityId && x.Origin == ActivityMetricOrigin.Manual, cancellationToken)
            ?? throw new InvalidOperationException("The editable custom metric was not found.");
        var key = NormalizeMetricKey(request.Key, request.Label);
        if (await db.ActivityMetrics.AnyAsync(x => x.ActivityId == activityId && x.Key == key && x.Origin == ActivityMetricOrigin.Manual && x.Id != metricId, cancellationToken))
            throw new InvalidOperationException("A custom metric with this key already exists.");
        metric.Key = key;
        metric.Label = request.Label.Trim();
        metric.NumericValue = request.NumericValue;
        metric.TextValue = Clean(request.TextValue);
        metric.Unit = Clean(request.Unit);
        metric.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteMetricAsync(Guid activityId, Guid metricId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var metric = await db.ActivityMetrics
            .SingleOrDefaultAsync(x => x.Id == metricId && x.ActivityId == activityId && x.Origin == ActivityMetricOrigin.Manual, cancellationToken)
            ?? throw new InvalidOperationException("The editable custom metric was not found.");
        db.ActivityMetrics.Remove(metric);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Guid id, UpdateActivityRequest request, CancellationToken cancellationToken = default)
    {
        var title = request.Title.Trim();
        if (title.Length is < 1 or > 240)
            throw new ArgumentException("Title must contain 1 to 240 characters.", nameof(request));
        if (request.Description?.Trim().Length > 4000)
            throw new ArgumentException("Description cannot exceed 4000 characters.", nameof(request));
        if (request.GearName?.Trim().Length > 160)
            throw new ArgumentException("Gear name cannot exceed 160 characters.", nameof(request));

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var oldOwner = await db.Activities.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => (Guid?)x.OwnerId)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Activity was not found.");
        await using var ownerLock = await ownerMutationLock.AcquireAsync([oldOwner, request.OwnerId], cancellationToken);

        var activity = await db.Activities
            .Include(x => x.Stream).Include(x => x.Laps).Include(x => x.SourceFiles).Include(x => x.Metrics)
            .AsSplitQuery()
            .SingleAsync(x => x.Id == id, cancellationToken);
        if (!await db.Owners.AnyAsync(x => x.Id == request.OwnerId, cancellationToken))
            throw new InvalidOperationException("The selected profile was not found.");

        activity.Title = title;
        activity.Description = Clean(request.Description);
        activity.GearName = Clean(request.GearName);
        activity.UserEdited = true;
        activity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (oldOwner == request.OwnerId)
        {
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (await db.Activities.AnyAsync(x =>
                x.OwnerId == request.OwnerId && x.Id != id &&
                (x.NaturalFingerprint == activity.NaturalFingerprint ||
                 activity.GarminId != null && x.GarminId == activity.GarminId ||
                 activity.StravaId != null && x.StravaId == activity.StravaId), cancellationToken))
            throw new InvalidOperationException("The target profile already contains this activity or one of its provider identifiers.");

        var hashes = activity.SourceFiles.Select(x => x.Sha256).Distinct().ToArray();
        var externalIds = activity.SourceFiles
            .Where(x => !string.IsNullOrWhiteSpace(x.ExternalId))
            .Select(x => x.ExternalId!)
            .Distinct().ToArray();
        var targetSources = await db.SourceFiles.AsNoTracking()
            .Where(x => x.OwnerId == request.OwnerId &&
                (hashes.Contains(x.Sha256) || x.ExternalId != null && externalIds.Contains(x.ExternalId)))
            .Select(x => new { x.Provider, x.Sha256, x.ExternalId })
            .ToListAsync(cancellationToken);
        if (activity.SourceFiles.Any(source => targetSources.Any(target =>
                target.Provider == source.Provider &&
                (target.Sha256 == source.Sha256 ||
                 source.ExternalId != null && target.ExternalId == source.ExternalId))))
            throw new InvalidOperationException("The target profile already contains conflicting source-file provenance.");

        var prepared = new List<PreparedFileOperation>();
        var pathMap = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var source in activity.SourceFiles.GroupBy(x => x.StoredPath).Select(x => x.First()))
            {
                var sourcePath = originals.ResolveStoredPath(source.StoredPath);
                var targetPath = originals.GetOriginalTarget(
                    request.OwnerId, source.Sha256, Path.GetExtension(source.OriginalName));
                var operation = await fileOperations.PrepareCopyAsync(
                    request.OwnerId, id, sourcePath, targetPath, source.Sha256,
                    deleteSourceOnCommit: true, cancellationToken: cancellationToken);
                prepared.Add(operation);
                pathMap[source.StoredPath] = operation.TargetRelativePath;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var transferBatch = new ImportBatch
            {
                OwnerId = request.OwnerId,
                SourceKind = activity.SourceFiles.FirstOrDefault()?.SourceKind ?? SourceKind.Fit,
                Kind = ImportBatchKind.ActivityTransfer,
                Status = ImportStatus.Completed,
                DisplayName = $"Transferred {activity.Title}",
                StagedPath = string.Empty,
                FilesDiscovered = activity.SourceFiles.Count,
                ActivitiesUpdated = 1,
                Summary = "Activity and source provenance transferred from another local profile.",
                StartedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
            db.ImportBatches.Add(transferBatch);

            await db.Routes.Where(x => x.OwnerId == oldOwner && x.SourceActivityId == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.SourceActivityId, (Guid?)null), cancellationToken);
            await db.Segments.Where(x => x.OwnerId == oldOwner && x.SourceActivityId == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.SourceActivityId, (Guid?)null), cancellationToken);

            activity.OwnerId = request.OwnerId;
            if (activity.Stream is not null) activity.Stream.OwnerId = request.OwnerId;
            foreach (var lap in activity.Laps) lap.OwnerId = request.OwnerId;
            foreach (var source in activity.SourceFiles)
            {
                source.OwnerId = request.OwnerId;
                source.ImportBatchId = transferBatch.Id;
                source.StoredPath = pathMap[source.StoredPath];
            }
            foreach (var metric in activity.Metrics) metric.OwnerId = request.OwnerId;
            db.SegmentEfforts.RemoveRange(await db.SegmentEfforts
                .Where(x => x.ActivityId == id).ToListAsync(cancellationToken));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            foreach (var operation in prepared)
                await fileOperations.RollbackAsync(operation.OperationId, CancellationToken.None);
            throw;
        }

        foreach (var operation in prepared)
            await fileOperations.CommitAsync(operation.OperationId, cancellationToken);

        await statistics.RecomputeAsync(oldOwner, cancellationToken);
        await statistics.RecomputeAsync(request.OwnerId, cancellationToken);
        var affectedSegments = await db.Segments.AsNoTracking()
            .Where(x => x.OwnerId == oldOwner || x.OwnerId == request.OwnerId)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        foreach (var segmentId in affectedSegments)
            await segments.RecomputeAsync(segmentId, cancellationToken);
    }

    private static IQueryable<Activity> ApplyFilter(IQueryable<Activity> query, ActivityFilter filter)
    {
        if (filter.OwnerId.HasValue) query = query.Where(x => x.OwnerId == filter.OwnerId);
        if (filter.Sport.HasValue) query = query.Where(x => x.Sport == filter.Sport);
        if (filter.From.HasValue)
        {
            var from = new DateTimeOffset(filter.From.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(x => x.StartTimeUtc >= from);
        }
        if (filter.To.HasValue)
        {
            var to = new DateTimeOffset(filter.To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(x => x.StartTimeUtc < to);
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            query = query.Where(x => x.Title.Contains(term) || (x.Description != null && x.Description.Contains(term)));
        }
        if (filter.HasPower.HasValue) query = query.Where(x => x.HasPower == filter.HasPower);
        if (!string.IsNullOrWhiteSpace(filter.Device))
        {
            var device = filter.Device.Trim();
            query = query.Where(x => x.DeviceName != null && x.DeviceName.Contains(device));
        }
        return query;
    }

    private static IQueryable<Activity> ApplySort(IQueryable<Activity> query, string sort) => sort switch
    {
        "start-asc" => query.OrderBy(x => x.StartTimeUtc),
        "distance-desc" => query.OrderByDescending(x => x.DistanceMeters),
        "distance-asc" => query.OrderBy(x => x.DistanceMeters),
        "duration-desc" => query.OrderByDescending(x => x.MovingTimeSeconds),
        "elevation-desc" => query.OrderByDescending(x => x.ElevationGainMeters),
        _ => query.OrderByDescending(x => x.StartTimeUtc)
    };

    private static void ValidateMetric(ActivityMetricRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Label))
            throw new ArgumentException("Metric label is required.", nameof(request));
        if (!request.NumericValue.HasValue && string.IsNullOrWhiteSpace(request.TextValue))
            throw new ArgumentException("Enter either a numeric or text value.", nameof(request));
    }

    private static string NormalizeMetricKey(string key, string label)
    {
        var source = string.IsNullOrWhiteSpace(key) ? label : key;
        var slug = string.Concat(source.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-'))
            .Trim('-');
        if (string.IsNullOrWhiteSpace(slug)) slug = Guid.NewGuid().ToString("N");
        return "custom." + slug[..Math.Min(slug.Length, 90)];
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();


    private static ActivitySummary ToSummary(Activity x) => new(
        x.Id, x.OwnerId, x.Owner?.DisplayName ?? "Unknown profile", x.Title, x.Sport, x.StartTimeUtc,
        x.DistanceMeters, x.MovingTimeSeconds, x.ElevationGainMeters, x.AveragePowerWatts,
        x.DeviceName, x.HasGps, x.HasPower);
}
