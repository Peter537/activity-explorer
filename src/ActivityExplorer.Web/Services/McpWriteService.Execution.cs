using System.Text.Json;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Web.Services;

public sealed partial class McpWriteService
{
    internal IAsyncEnumerable<Guid> ReadQueueAsync(CancellationToken token) => _queue.Reader.ReadAllAsync(token);

    public async Task RecoverAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        foreach (var operation in await db.McpOperations.Where(item => item.Status == "queued" || item.Status == "running").ToArrayAsync(token))
        {
            var effect = await db.McpEffects.AsNoTracking().SingleOrDefaultAsync(item => item.Id == operation.Id, token);
            operation.EntityId = effect?.EntityId;
            operation.Status = effect is null ? "interrupted" : "committed_with_warnings";
            operation.Message = effect is null
                ? "The Web app stopped. The operation will not be replayed automatically; inspect the current state and prepare a new preview."
                : "The database mutation committed. Check the entity and normal file/import recovery; the original command will not be repeated.";
            operation.RequestJson = "";
            operation.CompletedAtUtc = clock.GetUtcNow();
        }
        await db.SaveChangesAsync(token);
    }

    public Task ExecuteAsync(Guid id, CancellationToken token) => mutations.RunAsync(async () =>
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var operation = await db.McpOperations.SingleAsync(item => item.Id == id, token);
        if (operation.Status != "queued") return false;
        try
        {
            var request = JsonSerializer.Deserialize<McpChangeRequest>(operation.RequestJson, McpChangeJson.Options)!;
            Authorize(request);
            if (operation.Fingerprint != await FingerprintAsync(token))
                throw new InvalidOperationException("The application data changed while this operation was queued. Prepare and review it again.");
            operation.Status = "running";
            await db.SaveChangesAsync(token);
            using (var scope = new McpOperationScope(id, EffectType(request.Action)))
            {
                var (entityId, result) = await ApplyAsync(request, id, token);
                operation.EntityId = entityId;
                operation.ResultJson = JsonSerializer.Serialize(result, McpChangeJson.Options);
            }
            operation.Status = "completed";
        }
        catch (Exception exception)
        {
            var effect = await db.McpEffects.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, CancellationToken.None);
            operation.Status = effect is null ? "failed" : "committed_with_warnings";
            operation.EntityId = effect?.EntityId;
            operation.Message = exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException
                ? exception.Message : exception is OperationCanceledException ? "The operation was interrupted. Check status before preparing another change." : "The operation could not finish. Inspect its entity and Web logs before preparing another change.";
        }
        operation.RequestJson = "";
        operation.CompletedAtUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(CancellationToken.None);
        return true;
    }, token);

    private static Type? EffectType(string action) => action switch
    {
        "create_segment_from_activity" or "create_segment_from_route" or "create_segment_path" or "create_subsegment" or "import_segment" => typeof(Segment),
        "attach_segment" or "detach_segment" => typeof(SegmentLink),
        "recompute_segment" => typeof(SegmentEffort),
        "update_activity" or "transfer_activity" or "delete_activities" or "set_activity_tags" or "apply_activity_batch" => typeof(Core.Domain.Activity),
        "add_activity_metric" or "update_activity_metric" or "delete_activity_metric" => typeof(ActivityMetric),
        "create_tag" or "rename_tag" or "delete_tag" => typeof(Tag),
        "save_saved_search" or "rename_saved_search" or "delete_saved_search" => typeof(SavedSearch),
        "create_route_from_activity" or "create_route_path" or "import_route" => typeof(Core.Domain.Route),
        "create_goal" or "edit_goal" or "archive_goal" => typeof(PersonalGoal),
        "create_profile" or "update_profile_timezone" or "delete_profile" => typeof(OwnerProfile),
        "add_watched_folder" or "remove_watched_folder" => typeof(WatchedFolder),
        "import_activities" => typeof(ImportBatch),
        "recompute_statistics" => typeof(StatisticSnapshot),
        "rebuild_exploration" => typeof(ActivityExplorationIndex),
        "set_map_privacy" => typeof(ApplicationSetting),
        _ => null
    };

    private async Task<(Guid? EntityId, object Result)> ApplyAsync(McpChangeRequest change, Guid operationId, CancellationToken token)
    {
        var args = change.Arguments;
        var profile = change.ProfileId.GetValueOrDefault();
        Guid Id(string field) => Read<Guid>(args, field);
        string Text(string field) => Read<string>(args, field);
        T Service<T>() where T : notnull => services.GetRequiredService<T>();
        Guid? entityId = null;
        object result = new { applied = true };
        switch (change.Action)
        {
            case "create_segment_from_activity":
            case "create_segment_from_route":
            case "create_segment_path":
            case "import_segment":
                entityId = await Service<ISegmentService>().CreateAsync(await SegmentRequestAsync(change, token), token); break;
            case "create_subsegment":
                entityId = await Service<ISegmentService>().CreateSubsegmentAsync(new(Id("segmentId"), Text("name"), Read<int>(args, "startPointIndex"), Read<int>(args, "endPointIndex"), Read<double>(args, "toleranceMeters")), token); break;
            case "attach_segment": await Service<ISegmentService>().AttachAsync(Id("parentSegmentId"), Id("childSegmentId"), token); break;
            case "detach_segment": await Service<ISegmentService>().DetachAsync(Id("parentSegmentId"), Id("childSegmentId"), token); break;
            case "recompute_segment": await Service<ISegmentService>().RecomputeAsync(Id("segmentId"), token); break;
            case "update_activity": await Service<IActivityQueryService>().UpdateAsync(Id("activityId"), Read<UpdateActivityRequest>(args, "request"), token); break;
            case "transfer_activity":
                var activity = await Service<IActivityQueryService>().GetAsync(Id("activityId"), token) ?? throw new ArgumentException("The activity is unavailable.");
                await Service<IActivityQueryService>().UpdateAsync(Id("activityId"), new(activity.Summary.Title, activity.Description, activity.GearName, Id("destinationProfileId")), token); break;
            case "delete_activities": result = await Service<IActivityQueryService>().DeleteAsync(Read<Guid[]>(args, "activityIds"), token); break;
            case "add_activity_metric": entityId = await Service<IActivityQueryService>().AddMetricAsync(Id("activityId"), Read<ActivityMetricRequest>(args, "request"), token); break;
            case "update_activity_metric": await Service<IActivityQueryService>().UpdateMetricAsync(Id("activityId"), Id("metricId"), Read<ActivityMetricRequest>(args, "request"), token); break;
            case "delete_activity_metric": await Service<IActivityQueryService>().DeleteMetricAsync(Id("activityId"), Id("metricId"), token); break;
            case "create_tag": entityId = await Service<IActivityOrganizationService>().CreateTagAsync(profile, Text("name"), token); break;
            case "rename_tag": await Service<IActivityOrganizationService>().RenameTagAsync(profile, Id("tagId"), Text("name"), token); break;
            case "delete_tag": await Service<IActivityOrganizationService>().DeleteTagAsync(profile, Id("tagId"), token); break;
            case "set_activity_tags":
                await using (var db = await factory.CreateDbContextAsync(token))
                {
                    var activityId = Id("activityId");
                    var version = await db.Activities.Where(item => item.Id == activityId && item.OwnerId == profile).Select(item => item.MutationVersion).SingleAsync(token);
                    await Service<IActivityOrganizationService>().SetActivityTagsAsync(profile, Id("activityId"), Read<Guid[]>(args, "tagIds"), version, token);
                }
                break;
            case "apply_activity_batch":
                var review = await Service<IActivityOrganizationService>().PrepareBatchAsync(profile, Read<Guid[]>(args, "activityIds"), null, Read<BatchChangeRequest>(args, "changes"), token);
                result = await Service<IActivityOrganizationService>().ApplyBatchAsync(review, token); break;
            case "save_saved_search": entityId = await Service<IActivityOrganizationService>().SaveSavedSearchAsync(profile, Text("name"), Read<SavedSearchCriteria>(args, "criteria"), Optional<Guid?>(args, "id"), token); break;
            case "rename_saved_search": await Service<IActivityOrganizationService>().RenameSavedSearchAsync(profile, Id("savedSearchId"), Text("name"), token); break;
            case "delete_saved_search": await Service<IActivityOrganizationService>().DeleteSavedSearchAsync(profile, Id("savedSearchId"), token); break;
            case "create_route_from_activity": case "create_route_path": entityId = await Service<IRouteService>().CreateAsync(await RouteRequestAsync(change, token), token); break;
            case "import_route":
                var route = await RouteRequestAsync(change, token);
                var routeAsset = RequireAsset(Id("assetId"), profile, "file");
                _assets.TryRemove(routeAsset.Summary.Id, out _);
                entityId = await Service<IRouteService>().ImportGpxAsync(route, routeAsset.Path, routeAsset.Summary.Name, routeAsset.Hash, routeAsset.Summary.Length, token); break;
            case "import_activities":
                var import = RequireAsset(Id("assetId"), profile, "file");
                if (import.Hash != await Fingerprint.Sha256Async(import.Path, token)) throw new InvalidDataException("The approved file changed.");
                _assets.TryRemove(import.Summary.Id, out _);
                entityId = await Service<IImportQueue>().EnqueueAsync(new(profile, import.Path, import.Summary.Name, Read<SourceKind>(args, "sourceKind")), token);
                result = new { importBatchId = entityId, message = "Import queued. Poll operation status for its result." }; break;
            case "create_goal": entityId = await Service<IGoalService>().CreateAsync(Read<CreateGoalRequest>(args, "request"), token); break;
            case "edit_goal": await Service<IGoalService>().EditAsync(Read<EditGoalRequest>(args, "request"), token); break;
            case "archive_goal": await Service<IGoalService>().ArchiveAsync(Read<ArchiveGoalRequest>(args, "request"), token); break;
            case "create_profile": entityId = await Service<IProfileService>().CreateAsync(Text("name"), token); break;
            case "update_profile_timezone": await Service<IProfileService>().UpdateTimeZoneAsync(profile, Text("timeZoneId"), token); break;
            case "delete_profile":
                await using (var db = await factory.CreateDbContextAsync(token))
                {
                    var name = await db.Owners.Where(item => item.Id == profile).Select(item => item.DisplayName).SingleAsync(token);
                    await Service<IProfileService>().DeleteAsync(profile, "DELETE " + name, token);
                    CleanupAssets(all: true, profileId: profile);
                    var profileText = profile.ToString();
                    var downloads = await db.McpOperations.AsNoTracking().Where(item => (item.Action == "export_profile" || item.Action == "export_route" || item.Action == "download_original") && item.ProfileIdsJson.Contains(profileText)).Select(item => item.Id).ToArrayAsync(token);
                    foreach (var download in downloads) if (File.Exists(DownloadPath(download))) File.Delete(DownloadPath(download));
                }
                break;
            case "add_watched_folder": entityId = await Service<IWatchedFolderService>().AddAsync(profile, RequireAsset(Id("assetId"), profile, "folder").Path, token); break;
            case "remove_watched_folder": await Service<IWatchedFolderService>().RemoveAsync(Id("folderId"), token); break;
            case "recompute_statistics": await Service<IStatisticsService>().RecomputeAsync(profile, token); break;
            case "rebuild_exploration": result = await Service<IExplorationIndexService>().BuildAsync(new(profile), recheckLimited: Read<bool>(args, "recheckLimited"), cancellationToken: token); break;
            case "set_map_privacy": await Service<IMapSettingsService>().SetModeAsync(Read<MapPrivacyMode>(args, "mode"), token); break;
            case "export_profile":
            case "export_route":
            case "download_original":
                var target = DownloadPath(operationId);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string fileName;
                string contentType;
                if (change.Action == "export_profile")
                {
                    var export = await Service<IProfileService>().ExportAsync(profile, token);
                    await File.WriteAllTextAsync(target, export.Json, token);
                    fileName = export.FileName; contentType = "application/json";
                }
                else if (change.Action == "export_route")
                {
                    var gpx = await Service<IRouteService>().ExportGpxAsync(Id("routeId"), token) ?? throw new ArgumentException("The route is unavailable.");
                    await File.WriteAllTextAsync(target, gpx, token);
                    fileName = "route.gpx"; contentType = "application/gpx+xml";
                }
                else
                {
                    await using var originalDb = await factory.CreateDbContextAsync(token);
                    var sourceId = Id("sourceFileId");
                    var original = await originalDb.SourceFiles.AsNoTracking().SingleAsync(item => item.Id == sourceId && item.OwnerId == profile, token);
                    await using var input = File.OpenRead(Service<IOriginalStore>().ResolveStoredPath(original.StoredPath));
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(output, token);
                    fileName = original.OriginalName; contentType = "application/octet-stream";
                }
                result = new
                {
                    downloadUrl = Endpoint + "/mcp/download/" + operationId,
                    profileId = profile,
                    fileName,
                    contentType,
                    expiresAt = clock.GetUtcNow().AddHours(1),
                    message = "Immutable download, available for one hour or until the Web app stops. Contents are not returned to the model."
                }; break;
            default: throw new ArgumentException("Unsupported change.");
        }
        if (entityId.HasValue && change.Action != "import_activities") result = new { entityId };
        return (entityId, result);
    }

    private async Task<object> DescribeImportAsync(Asset asset, SourceKind source, Guid profile, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(asset.Path)!;
        var existing = Directory.GetDirectories(directory).ToHashSet(StringComparer.Ordinal);
        try
        {
            var importer = services.GetServices<IActivityImporter>().FirstOrDefault(item => item.CanImport(asset.Path)) ?? throw new InvalidDataException("Unsupported import file.");
            var candidates = await importer.ReadAsync(asset.Path, source, token);
            await using var db = await factory.CreateDbContextAsync(token);
            var hashes = (await db.SourceFiles.AsNoTracking().Where(item => item.OwnerId == profile).Select(item => item.Sha256).ToArrayAsync(token)).ToHashSet(StringComparer.Ordinal);
            var fingerprints = (await db.Activities.AsNoTracking().Where(item => item.OwnerId == profile).Select(item => item.NaturalFingerprint).ToArrayAsync(token)).ToHashSet(StringComparer.Ordinal);
            var providerIds = await db.Activities.AsNoTracking().Where(item => item.OwnerId == profile).Select(item => new { item.GarminId, item.StravaId }).ToArrayAsync(token);
            var created = 0; var enriched = 0; var duplicates = 0;
            foreach (var candidate in candidates)
            {
                var fingerprint = Fingerprint.For(candidate.Parsed);
                var providerMatch = providerIds.Any(item => candidate.ExternalId is not null && (candidate.Provider == SourceProvider.Garmin && item.GarminId == candidate.ExternalId || candidate.Provider == SourceProvider.Strava && item.StravaId == candidate.ExternalId));
                if (!hashes.Add(candidate.Sha256)) duplicates++;
                else if (providerMatch || !fingerprints.Add(fingerprint)) enriched++;
                else created++;
            }
            return new
            {
                asset.Summary.Name,
                asset.Summary.Length,
                asset.Hash,
                candidates = candidates.Count,
                expectedCreates = created,
                expectedEnrichments = enriched,
                duplicateSources = duplicates,
                warnings = (importer as IImporterDiagnosticsSource)?.ConsumeDiagnostics(),
                note = "Duplicate sources may also enrich metadata or upgrade parsed technical data. Import commits are incremental; interruption can leave completed activities. Preview extraction uses temporary scratch files, without enqueuing an import."
            };
        }
        finally
        {
            foreach (var scratch in Directory.GetDirectories(directory).Where(path => !existing.Contains(path)))
            {
                var fullPath = ManagedPathGuard.ResolveUnder(directory, scratch);
                Directory.Delete(fullPath, recursive: true);
            }
        }
    }

    public async Task<McpWriteContext> ContextAsync(McpReadContextRequest request, CancellationToken token)
    {
        if (!Enabled || !options.ProfileIds.Contains(request.ProfileId)) throw new UnauthorizedAccessException("The profile is unavailable.");
        var offset = new ReadPage(request.Page, request.PageSize).Offset;
        await using var db = await factory.CreateDbContextAsync(token);
        var profile = request.ProfileId;
        if (!await db.Owners.AnyAsync(owner => owner.Id == profile, token)) throw new ArgumentException("The profile is unavailable.");
        JsonElement[] items;
        int total;
        switch (request.Kind)
        {
            case "tags":
                var tags = await services.GetRequiredService<IActivityOrganizationService>().ListTagsAsync(profile, token);
                total = tags.Count; items = tags.Skip(offset).Take(request.PageSize).Select(McpChangeJson.Element).ToArray(); break;
            case "saved-searches":
                var searches = await services.GetRequiredService<IActivityOrganizationService>().ListSavedSearchesAsync(profile, token);
                total = searches.Count; items = searches.Skip(offset).Take(request.PageSize).Select(McpChangeJson.Element).ToArray(); break;
            case "goals":
                var goals = await services.GetRequiredService<IGoalService>().ListAsync(profile, GoalListView.All, token);
                total = goals.Count; items = goals.Skip(offset).Take(request.PageSize).Select(McpChangeJson.Element).ToArray(); break;
            case "goal":
                var goal = await services.GetRequiredService<IGoalService>().GetDetailAsync(request.EntityId ?? throw new ArgumentException("entityId is required."), cancellationToken: token);
                if (goal is null || goal.Goal.OwnerId != profile) throw new ArgumentException("The goal is unavailable.");
                total = 1; items = [McpChangeJson.Element(goal)]; break;
            case "metrics":
                var activityId = request.EntityId ?? throw new ArgumentException("entityId is required.");
                var metrics = db.ActivityMetrics.AsNoTracking().Where(item => item.ActivityId == activityId && item.OwnerId == profile);
                total = await metrics.CountAsync(token);
                items = (await metrics.OrderBy(item => item.Key).Skip(offset).Take(request.PageSize).Select(item => new { item.Id, item.Key, item.Label, item.NumericValue, item.TextValue, item.Unit, item.Origin }).ToArrayAsync(token)).Select(McpChangeJson.Element).ToArray(); break;
            case "assets":
                var assets = _assets.Values.Where(item => item.Summary.ProfileId == profile && item.Summary.ExpiresAt > clock.GetUtcNow()).Select(item => item.Summary).OrderBy(item => item.Name).ToArray();
                total = assets.Length; items = assets.Skip(offset).Take(request.PageSize).Select(McpChangeJson.Element).ToArray(); break;
            case "watched-folders":
                var folders = await db.WatchedFolders.AsNoTracking().Where(item => item.OwnerId == profile).Select(item => new { item.Id, item.Enabled }).ToArrayAsync(token);
                total = folders.Length; items = folders.Skip(offset).Take(request.PageSize).Select(McpChangeJson.Element).ToArray(); break;
            case "originals":
                var sources = db.SourceFiles.AsNoTracking().Where(item => item.OwnerId == profile && (!request.EntityId.HasValue || item.ActivityId == request.EntityId));
                total = await sources.CountAsync(token); items = (await sources.OrderBy(item => item.Id).Skip(offset).Take(request.PageSize).Select(item => new { item.Id, item.OriginalName, item.Length, item.ActivityId, item.RouteId }).ToArrayAsync(token)).Select(McpChangeJson.Element).ToArray(); break;
            case "activity-points":
                var id = request.EntityId ?? throw new ArgumentException("entityId is required.");
                var stream = await db.ActivityStreams.AsNoTracking().SingleOrDefaultAsync(item => item.ActivityId == id && item.OwnerId == profile, token) ?? throw new ArgumentException("The activity stream is unavailable.");
                var points = TrackCodec.Decode(stream.CompressedPayload);
                total = points.Count;
                var analysis = new TrackPathAnalysis(points);
                items = points.Select((point, index) => new { index, hasGps = point.Latitude.HasValue && point.Longitude.HasValue, point.Timestamp, distanceMeters = index == 0 ? 0 : analysis.Slice(0, index).DistanceMeters })
                    .Skip(offset).Take(request.PageSize).Select(McpChangeJson.Element).ToArray(); break;
            case "route-points":
            case "segment-points":
                var entityId = request.EntityId ?? throw new ArgumentException("entityId is required.");
                var geometry = request.Kind == "route-points"
                    ? await db.Routes.Where(item => item.Id == entityId && item.OwnerId == profile).Select(item => item.GeometryWkb).SingleOrDefaultAsync(token)
                    : await db.Segments.Where(item => item.Id == entityId && item.OwnerId == profile).Select(item => item.GeometryWkb).SingleOrDefaultAsync(token);
                if (geometry is null) throw new ArgumentException("The path is unavailable.");
                var path = GeometryCodec.FromWkb(geometry); total = path.Count;
                var pathAnalysis = new TrackPathAnalysis(path);
                items = Enumerable.Range(offset, Math.Min(request.PageSize, Math.Max(0, total - offset))).Select(index => McpChangeJson.Element(new { index, distanceMeters = index == 0 ? 0 : pathAnalysis.Slice(0, index).DistanceMeters })).ToArray(); break;
            default: throw new ArgumentException("Choose tags, saved-searches, goals, goal, metrics, assets, watched-folders, originals, activity-points, route-points or segment-points.");
        }
        return new(request.Kind, items, total, request.Page, request.PageSize);
    }
}
