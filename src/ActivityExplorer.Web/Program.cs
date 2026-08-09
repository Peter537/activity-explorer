using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using ActivityExplorer.Web.Components;
using ActivityExplorer.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://localhost:8342");
var maximumUploadBytes = builder.Configuration.GetValue("Imports:MaxUploadBytes", 10L * 1024 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = checked(maximumUploadBytes + 1024 * 1024));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddActivityExplorer();
var keyPaths = new AppDataPaths();
keyPaths.EnsureCreated();
var keyDirectory = Path.Combine(keyPaths.Root, "keys");
Directory.CreateDirectory(keyDirectory);
builder.Services.AddDataProtection()
    .SetApplicationName(AppSecurityIdentifiers.DataProtectionApplicationName)
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = AppSecurityIdentifiers.GetAntiforgeryCookieName(keyPaths.Root);
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.HeaderName = "X-CSRF-TOKEN";
    options.SuppressXFrameOptionsHeader = true;
});
builder.Services.AddSingleton<ILoggerProvider>(provider => new RollingFileLoggerProvider(provider.GetRequiredService<AppDataPaths>().LogsPath));
builder.Services.AddScoped<ProfileState>();
builder.Services.AddSingleton<MultipartUploadReader>();

var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found");
app.UseHostFiltering();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
    var mode = await context.RequestServices.GetRequiredService<IMapSettingsService>()
        .GetModeAsync(context.RequestAborted);
    var openFreeMap = mode == MapPrivacyMode.OpenFreeMap
        ? " https://tiles.openfreemap.org https://*.openfreemap.org"
        : string.Empty;
    var websocketOrigin = $"{(context.Request.IsHttps ? "wss" : "ws")}://{context.Request.Host}";
    context.Response.Headers.ContentSecurityPolicy =
        $"default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; " +
        $"script-src 'self'; style-src 'self'; font-src 'self' data:; worker-src 'self' blob:; " +
        $"connect-src 'self' {websocketOrigin}{openFreeMap}; img-src 'self' data: blob:{openFreeMap}";
    await next(context);
});
app.UseStaticFiles();
app.UseAntiforgery();
app.MapStaticAssets();

var internalApi = app.MapGroup("/internal");
internalApi.MapGet("/antiforgery/token", (HttpContext context, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    return Results.Ok(new { token = tokens.RequestToken });
});
internalApi.MapPost("/imports", async (
    HttpContext context,
    IAntiforgery antiforgery,
    IImportQueue queue,
    MultipartUploadReader uploads,
    CancellationToken cancellationToken) =>
{
    var request = context.Request;
    if (request.Headers["X-Activity-Explorer"] != "1")
        return Results.BadRequest(new { error = "The required same-origin request header is missing." });
    try
    {
        await antiforgery.ValidateRequestAsync(context);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest(new { error = "A valid same-origin antiforgery token is required." });
    }
    if (!Guid.TryParse(request.Query["ownerId"], out var ownerId))
        return Results.BadRequest(new { error = "Select a profile before importing." });

    StagedUpload? upload = null;
    try
    {
        upload = await uploads.ReadSingleFileAsync(request, uploads.ImportLimit, cancellationToken);
        var extension = Path.GetExtension(upload.FileName).ToLowerInvariant();
        if (extension is not (".fit" or ".gpx" or ".tcx" or ".gz" or ".zip"))
            return Results.BadRequest(new { error = "Supported files are FIT, GPX, TCX, GZ, and ZIP." });

        SourceKind sourceKind;
        if (request.Query.ContainsKey("sourceKind"))
        {
            if (!Enum.TryParse<SourceKind>(request.Query["sourceKind"], true, out sourceKind) || !Enum.IsDefined(sourceKind))
                return Results.BadRequest(new { error = "Choose a supported import source." });
        }
        else
        {
            sourceKind = extension switch
            {
                ".fit" => SourceKind.Fit,
                ".tcx" => SourceKind.Tcx,
                ".gpx" => SourceKind.Gpx,
                _ => SourceKind.GarminArchive
            };
            if (extension == ".zip" && ImportSourceDetector.IsStravaBulkExport(upload.FilePath))
                sourceKind = SourceKind.StravaArchive;
        }

        var id = await queue.EnqueueAsync(
            new ImportRequest(ownerId, upload.FilePath, upload.FileName, sourceKind), cancellationToken);
        upload = null;
        return Results.Accepted($"/internal/imports/{id}", new { id });
    }
    catch (UploadTooLargeException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status413PayloadTooLarge);
    }
    catch (InvalidDataException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    finally
    {
        if (upload is not null) uploads.Cleanup(upload);
    }
});

internalApi.MapGet("/imports/{id:guid}", async (Guid id, IImportHistoryService history, CancellationToken token) =>
{
    var report = await history.GetReportAsync(id, token);
    return report is null ? Results.NotFound() : Results.Ok(report);
});
internalApi.MapGet("/map/activities", (HttpRequest request, IMapFeatureService maps, CancellationToken token) =>
    MapEndpointHandler.ExecuteAsync(request, maps.GetActivitiesAsync, token));
internalApi.MapGet("/map/routes", (HttpRequest request, IMapFeatureService maps, CancellationToken token) =>
    MapEndpointHandler.ExecuteAsync(request, maps.GetRoutesAsync, token));
internalApi.MapGet("/map/segments", (HttpRequest request, IMapFeatureService maps, CancellationToken token) =>
    MapEndpointHandler.ExecuteAsync(request, maps.GetSegmentsAsync, token));
internalApi.MapGet("/originals/{id:guid}", async (
    Guid id, IDbContextFactory<ExplorerDbContext> factory, IOriginalStore originals, CancellationToken token) =>
{
    await using var db = await factory.CreateDbContextAsync(token);
    var source = await db.SourceFiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
    if (source is null) return Results.NotFound();
    try
    {
        var fullPath = originals.ResolveStoredPath(source.StoredPath);
        return !File.Exists(fullPath)
            ? Results.NotFound()
            : Results.File(fullPath, "application/octet-stream", source.OriginalName, enableRangeProcessing: true);
    }
    catch (InvalidDataException)
    {
        return Results.NotFound();
    }
});
internalApi.MapPost("/segments/import", async (
    HttpContext context,
    IAntiforgery antiforgery,
    ISegmentPathReader reader,
    ISegmentService segments,
    MultipartUploadReader uploads,
    CancellationToken token) =>
{
    var request = context.Request;
    if (request.Headers["X-Activity-Explorer"] != "1")
        return Results.BadRequest(new { error = "The required same-origin request header is missing." });
    try
    {
        await antiforgery.ValidateRequestAsync(context);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest(new { error = "A valid same-origin antiforgery token is required." });
    }

    if (!Guid.TryParse(request.Query["ownerId"], out var ownerId))
        return Results.BadRequest(new { error = "Select a profile before importing a segment path." });
    if (!Enum.TryParse<SportKind>(request.Query["sport"], true, out var sport) || !Enum.IsDefined(sport))
        return Results.BadRequest(new { error = "Choose Cycling, Running, or Walking." });
    var name = request.Query["name"].ToString().Trim();
    if (name.Length is < 1 or > 240)
        return Results.BadRequest(new { error = "Segment name must contain 1 to 240 characters." });
    if (!double.TryParse(request.Query["toleranceMeters"], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var tolerance) ||
        !double.IsFinite(tolerance) || tolerance is < 10 or > 200)
        return Results.BadRequest(new { error = "Tolerance must be between 10 and 200 metres." });

    static bool TryIndex(HttpRequest httpRequest, string key, out int? value)
    {
        var text = httpRequest.Query[key].ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            value = null;
            return true;
        }
        if (int.TryParse(text, out var parsed) && parsed >= 0)
        {
            value = parsed;
            return true;
        }
        value = null;
        return false;
    }

    if (!TryIndex(request, "startIndex", out var startIndex) || !TryIndex(request, "endIndex", out var endIndex))
        return Results.BadRequest(new { error = "Trim points must be zero-based whole numbers." });
    var reverseText = request.Query["reverseDirection"].ToString();
    if (!string.IsNullOrWhiteSpace(reverseText) && !bool.TryParse(reverseText, out _))
        return Results.BadRequest(new { error = "Choose a valid direction." });
    var reverse = bool.TryParse(reverseText, out var reverseValue) && reverseValue;

    StagedUpload? upload = null;
    try
    {
        upload = await uploads.ReadSingleFileAsync(request, uploads.SegmentLimit, token);
        var extension = Path.GetExtension(upload.FileName).ToLowerInvariant();
        if (extension is not (".gpx" or ".fit" or ".tcx" or ".kml" or ".geojson" or ".json"))
            return Results.BadRequest(new { error = "Supported segment path files are GPX, FIT segment/course, TCX, KML, and GeoJSON." });

        var parsed = await reader.ReadAsync(upload.FilePath, token);
        var start = startIndex ?? 0;
        var end = endIndex ?? parsed.Points.Count - 1;
        if (start < 0 || end >= parsed.Points.Count || end - start < 1)
            return Results.BadRequest(new { error = $"Choose at least two valid points between 0 and {parsed.Points.Count - 1}." });
        IEnumerable<TrackPoint> selected = parsed.Points.Skip(start).Take(end - start + 1);
        if (reverse) selected = selected.Reverse();

        var id = await segments.CreateAsync(new CreateSegmentPathRequest(
            ownerId,
            name,
            sport,
            selected.ToArray(),
            tolerance,
            SourceKind: SegmentSourceKind.ImportedFile,
            SourceName: Path.GetFileName(upload.FileName),
            SourceFormat: parsed.Format), token);
        return Results.Ok(new { id });
    }
    catch (UploadTooLargeException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status413PayloadTooLarge);
    }
    catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException or
        System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    finally
    {
        if (upload is not null) uploads.Cleanup(upload);
    }
});
internalApi.MapPost("/routes/import", async (
    HttpContext context,
    IAntiforgery antiforgery,
    GpxRouteReader reader,
    IRouteService routes,
    MultipartUploadReader uploads,
    CancellationToken token) =>
{
    var request = context.Request;
    if (request.Headers["X-Activity-Explorer"] != "1")
        return Results.BadRequest(new { error = "The required same-origin request header is missing." });
    try
    {
        await antiforgery.ValidateRequestAsync(context);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest(new { error = "A valid same-origin antiforgery token is required." });
    }
    if (!Guid.TryParse(request.Query["ownerId"], out var ownerId))
        return Results.BadRequest(new { error = "Select a profile before importing a route." });
    if (!Enum.TryParse<SportKind>(request.Query["sport"], true, out var sport) || !Enum.IsDefined(sport))
        return Results.BadRequest(new { error = "Choose Cycling, Running, or Walking." });
    var name = request.Query["name"].ToString().Trim();
    if (name.Length is < 1 or > 240)
        return Results.BadRequest(new { error = "Route name must contain 1 to 240 characters." });

    StagedUpload? upload = null;
    try
    {
        upload = await uploads.ReadSingleFileAsync(request, uploads.RouteLimit, token);
        if (!Path.GetExtension(upload.FileName).Equals(".gpx", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Choose a non-empty GPX file." });
        var points = await reader.ReadAsync(upload.FilePath, token);
        var hash = await Fingerprint.Sha256Async(upload.FilePath, token);
        var id = await routes.ImportGpxAsync(
            new CreateRoutePathRequest(ownerId, name, "Imported GPX path", sport, points),
            upload.FilePath, upload.FileName, hash, upload.Length, token);
        return Results.Ok(new { id });
    }
    catch (UploadTooLargeException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status413PayloadTooLarge);
    }
    catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException or ArgumentException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = "The GPX path could not be read safely. Check that it contains route or track points and belongs to the selected profile." });
    }
    finally
    {
        if (upload is not null) uploads.Cleanup(upload);
    }
});

internalApi.MapGet("/routes/{id:guid}.gpx", async (Guid id, IRouteService routes, CancellationToken token) =>
{
    var gpx = await routes.ExportGpxAsync(id, token);
    return gpx is null ? Results.NotFound() : Results.Text(gpx, "application/gpx+xml");
});
internalApi.MapGet("/profiles/{id:guid}/export", async (Guid id, IProfileService profiles, CancellationToken token) =>
{
    var export = await profiles.ExportAsync(id, token);
    return Results.File(System.Text.Encoding.UTF8.GetBytes(export.Json), "application/json", export.FileName);
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
app.Run();

public partial class Program;

internal static class MapEndpointHandler
{
    public static async Task<IResult> ExecuteAsync(
        HttpRequest request,
        Func<MapQuery, CancellationToken, Task<MapFeatureCollection>> query,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Json(await query(MapQueryParser.Parse(request), cancellationToken));
        }
        catch (BadHttpRequestException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }
}

internal static class MapQueryParser
{
    public static MapQuery Parse(HttpRequest request)
    {
        var ownerText = request.Query["ownerId"].ToString();
        Guid? owner = string.IsNullOrWhiteSpace(ownerText)
            ? null
            : Guid.TryParse(ownerText, out var ownerId)
                ? ownerId
                : throw new BadHttpRequestException("The map owner identifier is invalid.");

        var sportText = request.Query["sport"].ToString();
        SportKind? sport = string.IsNullOrWhiteSpace(sportText)
            ? null
            : Enum.TryParse<SportKind>(sportText, true, out var sportValue) && Enum.IsDefined(sportValue)
                ? sportValue
                : throw new BadHttpRequestException("The map sport is invalid.");

        DateOnly? Date(string key)
        {
            var raw = request.Query[key].ToString();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return DateOnly.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var value)
                ? value
                : throw new BadHttpRequestException($"The map {key} date is invalid.");
        }

        double? Number(string key)
        {
            var raw = request.Query[key].ToString();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return double.TryParse(raw, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
                ? value
                : throw new BadHttpRequestException($"The map {key} bound must be a finite number.");
        }

        var zoomText = request.Query["zoom"].ToString();
        var zoom = string.IsNullOrWhiteSpace(zoomText)
            ? 8
            : int.TryParse(zoomText, out var zoomValue) && zoomValue is >= 0 and <= 24
                ? zoomValue
                : throw new BadHttpRequestException("The map zoom must be between 0 and 24.");
        var query = new MapQuery(owner, sport, Date("from"), Date("to"),
            Number("west"), Number("south"), Number("east"), Number("north"), zoom);
        var boundCount = new[] { query.West, query.South, query.East, query.North }.Count(value => value.HasValue);
        if (boundCount is not 0 and not 4)
            throw new BadHttpRequestException("Map bounds must include west, south, east, and north.");
        if (boundCount == 4 && (query.West is < -180 or > 180 || query.East is < -180 or > 180 ||
                                query.South is < -90 or > 90 || query.North is < -90 or > 90 || query.South > query.North))
            throw new BadHttpRequestException("Map bounds contain an invalid latitude or longitude range.");
        if (query.From.HasValue && query.To.HasValue && query.From > query.To)
            throw new BadHttpRequestException("The map start date cannot be after the end date.");
        return query;
    }
}
internal static class ImportSourceDetector
{
    public static bool IsStravaBulkExport(string path)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(path);
            return archive.Entries.Any(entry =>
                entry.FullName.Replace('\\', '/').EndsWith("activities.csv", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
