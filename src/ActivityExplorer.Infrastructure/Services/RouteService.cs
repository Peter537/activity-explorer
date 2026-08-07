using System.Globalization;
using System.Text;
using System.Xml;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class RouteService(
    IDbContextFactory<ExplorerDbContext> contextFactory,
    IOriginalStore originals,
    IFileOperationCoordinator fileOperations,
    IOwnerMutationLock ownerMutationLock) : IRouteService
{
    public async Task<IReadOnlyList<RouteSummary>> ListAsync(Guid? ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Routes.AsNoTracking().Include(x => x.Owner).AsQueryable();
        if (ownerId.HasValue) query = query.Where(x => x.OwnerId == ownerId);
        return await query.OrderBy(x => x.Name)
            .Select(x => new RouteSummary(x.Id, x.OwnerId, x.Owner!.DisplayName, x.Name, x.Sport, x.DistanceMeters, x.ElevationGainMeters))
            .ToListAsync(cancellationToken);
    }

    public async Task<RouteDetail?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var route = await db.Routes.AsNoTracking().Include(x => x.Owner).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return route is null
            ? null
            : new RouteDetail(
                new RouteSummary(route.Id, route.OwnerId, route.Owner!.DisplayName, route.Name, route.Sport, route.DistanceMeters, route.ElevationGainMeters),
                route.Description,
                GeometryCodec.FromWkb(route.GeometryWkb));
    }

    public async Task<Guid> CreateFromActivityAsync(CreateRouteRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var activity = await db.Activities.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.ActivityId && x.OwnerId == request.OwnerId, cancellationToken)
            ?? throw new InvalidOperationException("Activity was not found for this profile.");
        if (activity.GeometryWkb is null)
        {
            throw new InvalidOperationException("A route requires an activity with GPS data.");
        }

        return await CreateAsync(new CreateRoutePathRequest(
            request.OwnerId, request.Name, request.Description, activity.Sport,
            GeometryCodec.FromWkb(activity.GeometryWkb), activity.Id), cancellationToken);
    }

    public async Task<Guid> CreateAsync(CreateRoutePathRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var points = ValidPoints(request.Points);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Owners.AnyAsync(x => x.Id == request.OwnerId, cancellationToken))
            throw new InvalidOperationException("The selected profile was not found.");
        if (request.SourceActivityId.HasValue &&
            !await db.Activities.AnyAsync(x => x.Id == request.SourceActivityId && x.OwnerId == request.OwnerId, cancellationToken))
            throw new InvalidOperationException("The route source activity does not belong to this profile.");

        var bounds = GeometryCodec.Bounds(points);
        var route = new Route
        {
            OwnerId = request.OwnerId,
            SourceActivityId = request.SourceActivityId,
            Sport = request.Sport,
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            DistanceMeters = GeometryCodec.DistanceMeters(points),
            ElevationGainMeters = ElevationGain(points),
            GeometryWkb = GeometryCodec.ToWkb(points)!,
            MinLatitude = bounds.MinLat!.Value,
            MinLongitude = bounds.MinLon!.Value,
            MaxLatitude = bounds.MaxLat!.Value,
            MaxLongitude = bounds.MaxLon!.Value
        };
        db.Routes.Add(route);
        await db.SaveChangesAsync(cancellationToken);
        return route.Id;
    }

    public async Task<Guid> ImportGpxAsync(
        CreateRoutePathRequest request,
        string stagedPath,
        string originalName,
        string sha256,
        long length,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var points = ValidPoints(request.Points);
        await using var ownerLock = await ownerMutationLock.AcquireAsync([request.OwnerId], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Owners.AnyAsync(x => x.Id == request.OwnerId, cancellationToken))
            throw new InvalidOperationException("The selected profile was not found.");

        var target = originals.GetOriginalTarget(request.OwnerId, sha256, ".gpx");
        var operation = await fileOperations.PrepareCopyAsync(
            request.OwnerId, null, stagedPath, target, sha256, deleteSourceOnCommit: true, cancellationToken: cancellationToken);
        var databaseCommitted = false;
        try
        {
            var bounds = GeometryCodec.Bounds(points);
            var route = new Route
            {
                OwnerId = request.OwnerId,
                SourceActivityId = request.SourceActivityId,
                Sport = request.Sport,
                Name = request.Name.Trim(),
                Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                DistanceMeters = GeometryCodec.DistanceMeters(points),
                ElevationGainMeters = ElevationGain(points),
                GeometryWkb = GeometryCodec.ToWkb(points)!,
                MinLatitude = bounds.MinLat!.Value,
                MinLongitude = bounds.MinLon!.Value,
                MaxLatitude = bounds.MaxLat!.Value,
                MaxLongitude = bounds.MaxLon!.Value
            };
            var batch = new ImportBatch
            {
                OwnerId = request.OwnerId,
                SourceKind = SourceKind.Gpx,
                Kind = ImportBatchKind.RouteImport,
                Status = ImportStatus.Completed,
                DisplayName = Path.GetFileName(originalName),
                StagedPath = string.Empty,
                FilesDiscovered = 1,
                Summary = "Imported one GPX route with retained source provenance.",
                StartedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
            route.SourceFiles.Add(new SourceFile
            {
                OwnerId = request.OwnerId,
                ImportBatchId = batch.Id,
                RouteId = route.Id,
                SourceKind = SourceKind.Gpx,
                Provider = SourceProvider.Unknown,
                AcquisitionMethod = AcquisitionMethod.DirectUpload,
                OriginalName = Path.GetFileName(originalName),
                StoredPath = operation.TargetRelativePath,
                Sha256 = sha256,
                Length = length
            });
            db.ImportBatches.Add(batch);
            db.Routes.Add(route);
            await db.SaveChangesAsync(cancellationToken);
            databaseCommitted = true;
            await fileOperations.CommitAsync(operation.OperationId, cancellationToken);
            return route.Id;
        }
        catch
        {
            if (!databaseCommitted)
                await fileOperations.RollbackAsync(operation.OperationId, CancellationToken.None);
            throw;
        }
    }

    public async Task<string?> ExportGpxAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var route = await GetAsync(id, cancellationToken);
        if (route is null) return null;

        var builder = new StringBuilder();
        using var writer = XmlWriter.Create(builder, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false });
        writer.WriteStartDocument();
        writer.WriteStartElement("gpx", "http://www.topografix.com/GPX/1/1");
        writer.WriteAttributeString("version", "1.1");
        writer.WriteAttributeString("creator", "Activity Explorer 0.1.0");
        writer.WriteStartElement("rte");
        writer.WriteElementString("name", route.Summary.Name);
        foreach (var point in route.Points.Where(x => x.Latitude.HasValue && x.Longitude.HasValue))
        {
            writer.WriteStartElement("rtept");
            writer.WriteAttributeString("lat", point.Latitude!.Value.ToString("R", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("lon", point.Longitude!.Value.ToString("R", CultureInfo.InvariantCulture));
            if (point.ElevationMeters.HasValue)
            {
                writer.WriteElementString("ele", point.ElevationMeters.Value.ToString("R", CultureInfo.InvariantCulture));
            }
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();
        return builder.ToString();
    }
    private static void ValidateRequest(CreateRoutePathRequest request)
    {
        var name = request.Name.Trim();
        if (name.Length is < 1 or > 240)
            throw new ArgumentException("Route name must contain 1 to 240 characters.", nameof(request));
        if (request.Description?.Trim().Length > 4000)
            throw new ArgumentException("Route description cannot exceed 4000 characters.", nameof(request));
        if (!Enum.IsDefined(request.Sport))
            throw new ArgumentOutOfRangeException(nameof(request), "Choose a supported sport.");
        if (request.Points.Count > 250_000)
            throw new ArgumentException("A route cannot contain more than 250,000 points.", nameof(request));
    }

    private static TrackPoint[] ValidPoints(IReadOnlyList<TrackPoint> source)
    {
        var points = source.Where(x => x.Latitude.HasValue && x.Longitude.HasValue).ToArray();
        if (points.Length < 2)
            throw new ArgumentException("A route needs at least two map points.", nameof(source));
        if (points.Any(point =>
                !double.IsFinite(point.Latitude!.Value) || point.Latitude is < -90 or > 90 ||
                !double.IsFinite(point.Longitude!.Value) || point.Longitude is < -180 or > 180 ||
                point.ElevationMeters.HasValue && !double.IsFinite(point.ElevationMeters.Value)))
            throw new ArgumentException("Route points must contain finite, valid coordinates.", nameof(source));
        return points;
    }

    private static double ElevationGain(IReadOnlyList<TrackPoint> points)
    {
        var gain = 0d;
        double? previous = null;
        foreach (var elevation in points.Select(x => x.ElevationMeters).Where(x => x.HasValue).Select(x => x!.Value))
        {
            if (previous.HasValue && elevation > previous.Value) gain += elevation - previous.Value;
            previous = elevation;
        }
        return gain;
    }
}
