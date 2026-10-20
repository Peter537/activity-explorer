using System.Text.Json;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Web.Services;

public sealed partial class McpWriteService
{
    private async Task<JsonElement> DescribeAsync(McpChangeRequest request, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var profile = request.ProfileId;
        if (profile.HasValue && !await db.Owners.AnyAsync(item => item.Id == profile, token)) throw new ArgumentException("The profile is unavailable.");
        var args = request.Arguments;
        object? before = null;
        object? effects = null;
        if (args.TryGetProperty("name", out var name))
        {
            var maximum = request.Action.Contains("tag", StringComparison.Ordinal) ? 80 : request.Action is "create_profile" or "save_saved_search" or "rename_saved_search" ? 120 : 240;
            if (string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Trim().Length > maximum) throw new ArgumentException($"Name must contain 1 to {maximum} characters.");
        }
        var activityIds = new HashSet<Guid>();
        if (args.TryGetProperty("activityId", out var activityId)) activityIds.Add(activityId.GetGuid());
        if (args.TryGetProperty("activityIds", out var list))
        {
            var ids = list.Deserialize<Guid[]>() ?? [];
            if (ids.Length is < 1 or > 1000 || ids.Contains(Guid.Empty) || ids.Distinct().Count() != ids.Length) throw new ArgumentException("Select 1 to 1000 distinct activities.");
            activityIds.UnionWith(ids);
        }
        if (activityIds.Count > 0)
        {
            var activities = await db.Activities.AsNoTracking().Where(item => activityIds.Contains(item.Id) && item.OwnerId == profile)
                .Select(item => new { item.Id, item.Title, item.Description, item.GearName, item.MutationVersion, item.HasGps }).ToArrayAsync(token);
            if (activities.Length != activityIds.Count) throw new ArgumentException("One or more activities are unavailable for this profile.");
            before = request.Action == "update_activity" ? activities : activities.Select(item => new { item.Id, item.Title, item.MutationVersion }).ToArray();
        }
        if (args.TryGetProperty("segmentId", out var segmentId))
        {
            var id = segmentId.GetGuid();
            before = await db.Segments.AsNoTracking().Where(item => item.Id == id && item.OwnerId == profile)
                .Select(item => new { item.Id, item.Name, item.DistanceMeters, item.ToleranceMeters }).SingleOrDefaultAsync(token)
                ?? throw new ArgumentException("The segment is unavailable for this profile.");
        }
        if (args.TryGetProperty("routeId", out var routeId))
        {
            var id = routeId.GetGuid();
            before = await db.Routes.AsNoTracking().Where(item => item.Id == id && item.OwnerId == profile)
                .Select(item => new { item.Id, item.Name, item.DistanceMeters }).SingleOrDefaultAsync(token)
                ?? throw new ArgumentException("The route is unavailable for this profile.");
        }
        foreach (var field in new[] { "tagId", "savedSearchId", "goalId", "folderId", "sourceFileId" })
        {
            if (!args.TryGetProperty(field, out var property)) continue;
            var id = property.GetGuid();
            var exists = field switch
            {
                "tagId" => await db.Tags.AnyAsync(item => item.Id == id && item.OwnerId == profile, token),
                "savedSearchId" => await db.SavedSearches.AnyAsync(item => item.Id == id && item.OwnerId == profile, token),
                "goalId" => await db.Goals.AnyAsync(item => item.Id == id && item.OwnerId == profile, token),
                "folderId" => await db.WatchedFolders.AnyAsync(item => item.Id == id && item.OwnerId == profile, token),
                _ => await db.SourceFiles.AnyAsync(item => item.Id == id && item.OwnerId == profile, token)
            };
            if (!exists) throw new ArgumentException("The requested entity is unavailable for this profile.");
            object? entity = field switch
            {
                "tagId" => await db.Tags.Where(item => item.Id == id).Select(item => new { item.Id, item.Name }).SingleAsync(token),
                "savedSearchId" => await db.SavedSearches.Where(item => item.Id == id).Select(item => new { item.Id, item.Name, item.CriteriaJson }).SingleAsync(token),
                "folderId" => await db.WatchedFolders.Where(item => item.Id == id).Select(item => new { item.Id, item.Enabled }).SingleAsync(token),
                "sourceFileId" => await db.SourceFiles.Where(item => item.Id == id).Select(item => new { item.Id, item.OriginalName, item.Length }).SingleAsync(token),
                _ => null
            };
            if (entity is not null) before = new { activity = before, entity };
        }
        if (args.TryGetProperty("metricId", out var metricId) && !await db.ActivityMetrics.AnyAsync(item => item.Id == metricId.GetGuid()
            && item.ActivityId == Read<Guid>(args, "activityId") && item.OwnerId == profile && item.Origin == ActivityMetricOrigin.Manual, token))
            throw new ArgumentException("The editable custom metric is unavailable.");

        switch (request.Action)
        {
            case "create_segment_from_activity":
            case "create_segment_from_route":
            case "create_subsegment":
            case "create_segment_path":
            case "import_segment":
                var segment = await SegmentRequestAsync(request, token);
                if (!Enum.IsDefined(segment.Sport)) throw new ArgumentException("Select a supported sport.");
                SegmentService.ValidateNameAndTolerance(segment.Name, segment.ToleranceMeters, "request");
                var validPoints = SegmentService.ValidatePoints(segment.Points, "points");
                effects = new
                {
                    pointCount = validPoints.Length,
                    distanceMeters = new TrackPathAnalysis(validPoints).Slice(0, validPoints.Length - 1).DistanceMeters,
                    geometrySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(GeometryCodec.ToWkb(validPoints)!)),
                    historicalEfforts = "Recomputed against current history; count is determined during execution."
                };
                break;
            case "attach_segment":
            case "detach_segment":
                var parent = Read<Guid>(args, "parentSegmentId");
                var child = Read<Guid>(args, "childSegmentId");
                if (await db.Segments.CountAsync(item => (item.Id == parent || item.Id == child) && item.OwnerId == profile, token) != 2)
                    throw new ArgumentException("Select two distinct segments from this profile.");
                before = new { parentSegmentId = parent, childSegmentId = child };
                if (request.Action == "attach_segment") effects = await services.GetRequiredService<ISegmentService>().PreviewAttachmentAsync(parent, child, token);
                break;
            case "create_route_from_activity":
            case "create_route_path":
            case "import_route":
                var route = await RouteRequestAsync(request, token);
                RouteService.ValidateRequest(route);
                var routePoints = RouteService.ValidPoints(route.Points);
                effects = new { pointCount = routePoints.Length, distanceMeters = GeometryCodec.DistanceMeters(routePoints) };
                break;
            case "update_activity":
                var update = Read<UpdateActivityRequest>(args, "request");
                if (update.OwnerId != profile) throw new ArgumentException("Use transfer_activity to move an activity.");
                if (string.IsNullOrWhiteSpace(update.Title) || update.Title.Trim().Length > 240 || update.Description?.Trim().Length > 4000 || update.GearName?.Trim().Length > 160)
                    throw new ArgumentException("Activity title, description or gear exceeds the supported limits.");
                break;
            case "transfer_activity":
                var destination = Read<Guid>(args, "destinationProfileId");
                if (destination == profile || !await db.Owners.AnyAsync(item => item.Id == destination, token)) throw new ArgumentException("Select a different permitted destination profile.");
                effects = new { destinationProfileId = destination, sourceFiles = "Transferred with provenance", tags = "Matched or created by name in the destination", efforts = "Recomputed in both profiles" };
                break;
            case "add_activity_metric":
            case "update_activity_metric":
                ActivityQueryService.ValidateMetric(Read<ActivityMetricRequest>(args, "request"));
                break;
            case "set_activity_tags":
                var tags = Read<Guid[]>(args, "tagIds");
                if (tags.Length > 100 || tags.Distinct().Count() != tags.Length || await db.Tags.CountAsync(item => item.OwnerId == profile && tags.Contains(item.Id), token) != tags.Length)
                    throw new ArgumentException("Select distinct tags from this profile.");
                break;
            case "apply_activity_batch":
                effects = await services.GetRequiredService<IActivityOrganizationService>().PrepareBatchAsync(profile!.Value, activityIds,
                    null, Read<BatchChangeRequest>(args, "changes"), token);
                break;
            case "create_goal":
                var createGoal = Read<CreateGoalRequest>(args, "request");
                if (createGoal.OwnerId != profile) throw new ArgumentException("The goal profile must match.");
                await services.GetRequiredService<IGoalService>().ValidateCreateAsync(createGoal, token);
                break;
            case "edit_goal":
                var edit = Read<EditGoalRequest>(args, "request");
                if (edit.OwnerId != profile) throw new ArgumentException("The goal profile must match.");
                await services.GetRequiredService<IGoalService>().ValidateEditAsync(edit, token);
                var goal = await services.GetRequiredService<IGoalService>().GetDetailAsync(edit.GoalId, edit.ExpectedEdition, cancellationToken: token);
                before = goal;
                break;
            case "archive_goal":
                var archive = Read<ArchiveGoalRequest>(args, "request");
                if (archive.OwnerId != profile) throw new ArgumentException("The goal profile must match.");
                await services.GetRequiredService<IGoalService>().ValidateArchiveAsync(archive, token);
                effects = "Archives future editions according to the profile's reporting date; completed history is retained.";
                break;
            case "save_saved_search":
                var criteria = Read<SavedSearchCriteria>(args, "criteria");
                ReportingDates.Validate(criteria.Dates);
                var savedId = Optional<Guid?>(args, "id");
                await services.GetRequiredService<IActivityOrganizationService>().ValidateSavedSearchAsync(profile!.Value, Read<string>(args, "name"), criteria, savedId, token);
                break;
            case "update_profile_timezone":
                _ = ReportingTimeZone.Resolve(Read<string>(args, "timeZoneId"));
                effects = "Changes reporting dates, calendar buckets and goal edition boundaries for this profile.";
                break;
            case "delete_activities":
            case "delete_profile":
                effects = new
                {
                    activities = request.Action == "delete_profile" ? await db.Activities.CountAsync(item => item.OwnerId == profile, token) : activityIds.Count,
                    originalFiles = await db.SourceFiles.CountAsync(item => item.OwnerId == profile && (request.Action == "delete_profile" || item.ActivityId.HasValue && activityIds.Contains(item.ActivityId.Value)), token),
                    routes = request.Action == "delete_profile" ? await db.Routes.CountAsync(item => item.OwnerId == profile, token) : 0,
                    segments = request.Action == "delete_profile" ? await db.Segments.CountAsync(item => item.OwnerId == profile, token) : 0,
                    consequence = "Deletes application records and eligible originals. There is no product undo. Shared originals are retained while referenced."
                };
                if (request.Action == "delete_profile" && await db.ImportBatches.AnyAsync(item => item.OwnerId == profile && (item.Status == ImportStatus.Running || item.Status == ImportStatus.Queued || item.Status == ImportStatus.Interrupted), token))
                    throw new ArgumentException("Finish or resolve pending imports before deleting this profile.");
                break;
            case "delete_tag":
                var tagId = Read<Guid>(args, "tagId");
                before = (await services.GetRequiredService<IActivityOrganizationService>().ListTagsAsync(profile!.Value, token)).Single(item => item.Id == tagId);
                effects = "Removes assignments. Saved searches referencing this tag require explicit repair.";
                break;
            case "add_watched_folder":
                var folder = RequireAsset(Read<Guid>(args, "assetId"), profile!.Value, "folder");
                effects = new { folder = folder.Summary.Name, ongoingAuthorization = "Imports existing and future supported files from this folder until removed. Source files are copied, never removed." };
                break;
            case "import_activities":
                var asset = RequireAsset(Read<Guid>(args, "assetId"), profile!.Value, "file");
                if (asset.Hash != await Fingerprint.Sha256Async(asset.Path, token)) throw new InvalidDataException("The selected file changed.");
                var source = Read<SourceKind>(args, "sourceKind");
                if (!Enum.IsDefined(source)) throw new ArgumentException("Select a valid import source.");
                effects = await DescribeImportAsync(asset, source, profile.Value, token);
                break;
            case "set_map_privacy":
                var mode = Read<MapPrivacyMode>(args, "mode");
                if (!Enum.IsDefined(mode)) throw new ArgumentException("Select a supported map mode.");
                before = await services.GetRequiredService<IMapSettingsService>().GetModeAsync(token);
                effects = mode == MapPrivacyMode.Blank ? "Uses blank maps without external tiles." : "Enables external OpenFreeMap tile requests from the browser.";
                break;
            case "export_profile":
            case "export_route":
            case "download_original":
                effects = "Creates a Web download link. Exported contents are not included in the MCP/model response.";
                break;
            case "create_profile":
                if (await db.Owners.AnyAsync(item => item.DisplayName == Read<string>(args, "name").Trim(), token)) throw new ArgumentException("A profile with that name already exists.");
                break;
            case "create_tag":
            case "rename_tag":
                var normalized = Read<string>(args, "name").Trim().ToUpperInvariant();
                var exceptTag = Optional<Guid?>(args, "tagId");
                if (await db.Tags.AnyAsync(item => item.OwnerId == profile && item.NormalizedName == normalized && item.Id != exceptTag, token)) throw new ArgumentException("A tag with that name already exists.");
                break;
            case "rename_saved_search":
                await services.GetRequiredService<IActivityOrganizationService>().ValidateSavedSearchRenameAsync(profile!.Value, Read<Guid>(args, "savedSearchId"), Read<string>(args, "name"), token);
                break;
            case "delete_saved_search":
            case "delete_activity_metric":
            case "remove_watched_folder":
            case "recompute_statistics":
            case "recompute_segment":
            case "rebuild_exploration":
                break;
            default: throw new ArgumentException("Unsupported change.");
        }
        var requested = args.EnumerateObject().Where(property => property.Name != "points").ToDictionary(property => property.Name, property => property.Value);
        return McpChangeJson.Element(new { requested, before, effects, dryRun = true });
    }

    private async Task<CreateSegmentPathRequest> SegmentRequestAsync(McpChangeRequest request, CancellationToken token)
    {
        var args = request.Arguments;
        var profile = request.ProfileId!.Value;
        var name = Read<string>(args, "name");
        var tolerance = Read<double>(args, "toleranceMeters");
        if (request.Action == "create_segment_path")
            return new(profile, name, Read<SportKind>(args, "sport"), Read<TrackPoint[]>(args, "points"), tolerance);
        if (request.Action == "import_segment")
        {
            var asset = RequireAsset(Read<Guid>(args, "assetId"), profile, "file");
            if (asset.Summary.Length > services.GetRequiredService<IConfiguration>().GetValue("Segments:MaxPathUploadBytes", 50L * 1024 * 1024))
                throw new InvalidDataException("The selected file exceeds the segment path upload limit.");
            if (asset.Hash != await Fingerprint.Sha256Async(asset.Path, token)) throw new InvalidDataException("The selected file changed.");
            var path = await services.GetRequiredService<ISegmentPathReader>().ReadAsync(asset.Path, token);
            return new(profile, name, Read<SportKind>(args, "sport"), path.Points, tolerance, SourceKind: SegmentSourceKind.ImportedFile, SourceName: asset.Summary.Name, SourceFormat: path.Format);
        }
        await using var db = await factory.CreateDbContextAsync(token);
        IReadOnlyList<TrackPoint> points;
        SportKind sport;
        string sourceName;
        Guid? sourceActivity = null;
        if (request.Action == "create_segment_from_activity")
        {
            var id = Read<Guid>(args, "activityId");
            var activity = await db.Activities.AsNoTracking().Include(item => item.Stream).SingleOrDefaultAsync(item => item.Id == id && item.OwnerId == profile, token)
                ?? throw new ArgumentException("The activity is unavailable.");
            if (activity.Stream is null) throw new ArgumentException("The activity has no stream.");
            points = TrackCodec.Decode(activity.Stream.CompressedPayload);
            sport = activity.Sport; sourceName = activity.Title; sourceActivity = id;
        }
        else if (request.Action == "create_segment_from_route")
        {
            var id = Read<Guid>(args, "routeId");
            var route = await db.Routes.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && item.OwnerId == profile, token) ?? throw new ArgumentException("The route is unavailable.");
            points = GeometryCodec.FromWkb(route.GeometryWkb); sport = route.Sport; sourceName = route.Name;
        }
        else
        {
            var id = Read<Guid>(args, "segmentId");
            var segment = await db.Segments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && item.OwnerId == profile, token) ?? throw new ArgumentException("The segment is unavailable.");
            points = GeometryCodec.FromWkb(segment.GeometryWkb); sport = segment.Sport; sourceName = segment.Name;
        }
        var start = Read<int>(args, "startPointIndex"); var end = Read<int>(args, "endPointIndex");
        if (start < 0 || end >= points.Count || end <= start || request.Action == "create_subsegment" && start == 0 && end == points.Count - 1)
            throw new ArgumentException("Select a valid range of at least two points; a subsegment must be smaller than its parent.");
        IEnumerable<TrackPoint> selected = points.Skip(start).Take(end - start + 1);
        if (Optional<bool>(args, "reverseDirection")) selected = selected.Reverse();
        return new(profile, name, sport, selected.ToArray(), tolerance, sourceActivity,
            request.Action == "create_segment_from_activity" ? SegmentSourceKind.Activity : request.Action == "create_segment_from_route" ? SegmentSourceKind.Route : SegmentSourceKind.Segment, sourceName);
    }

    private async Task<CreateRoutePathRequest> RouteRequestAsync(McpChangeRequest request, CancellationToken token)
    {
        var args = request.Arguments;
        if (request.Action == "create_route_path") return new(request.ProfileId!.Value, Read<string>(args, "name"), Optional<string>(args, "description"), Read<SportKind>(args, "sport"), Read<TrackPoint[]>(args, "points"));
        if (request.Action == "import_route")
        {
            var asset = RequireAsset(Read<Guid>(args, "assetId"), request.ProfileId!.Value, "file");
            if (asset.Summary.Length > services.GetRequiredService<IConfiguration>().GetValue("Routes:MaxGpxUploadBytes", 50L * 1024 * 1024))
                throw new InvalidDataException("The selected file exceeds the route upload limit.");
            if (asset.Hash != await Fingerprint.Sha256Async(asset.Path, token)) throw new InvalidDataException("The selected file changed.");
            return new(request.ProfileId.Value, Read<string>(args, "name"), Optional<string>(args, "description"), Read<SportKind>(args, "sport"), await services.GetRequiredService<GpxRouteReader>().ReadAsync(asset.Path, token));
        }
        await using var db = await factory.CreateDbContextAsync(token);
        var id = Read<Guid>(args, "activityId");
        var activity = await db.Activities.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && item.OwnerId == request.ProfileId, token) ?? throw new ArgumentException("The activity is unavailable.");
        if (activity.GeometryWkb is null) throw new ArgumentException("The activity has no GPS geometry.");
        return new(request.ProfileId!.Value, Read<string>(args, "name"), Optional<string>(args, "description"), activity.Sport, GeometryCodec.FromWkb(activity.GeometryWkb), id);
    }
}
