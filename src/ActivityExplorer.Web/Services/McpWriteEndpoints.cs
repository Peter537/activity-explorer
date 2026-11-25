using System.Net;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Web.Services;

public static class McpWriteEndpoints
{
    public static void MapMcpWrites(this WebApplication app)
    {
        var group = app.MapGroup("/internal/mcp");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var writes = http.RequestServices.GetRequiredService<McpWriteService>();
            if (!writes.Enabled) return Results.Json(new { error = "not_found" }, statusCode: 404);
            if (http.Connection.RemoteIpAddress is { } address && !IPAddress.IsLoopback(address)
                || !Uri.TryCreate(http.Request.Scheme + "://" + http.Request.Host, UriKind.Absolute, out var origin) || !origin.IsLoopback
                || http.Request.Headers.ContainsKey("Origin") || !writes.Authenticate(http.Request.Headers.Authorization))
                return Results.Json(new { error = "permission_denied", message = "A local MCP bridge credential is required." }, statusCode: 401);
            try { return await next(context); }
            catch (UnauthorizedAccessException) { return Results.Json(new { error = "permission_denied", message = "The requested capability or profile is unavailable." }, statusCode: 403); }
            catch (KeyNotFoundException exception) { return Results.NotFound(new { error = "not_found", message = exception.Message }); }
            catch (McpPlanClosedException exception)
            {
                return Results.Conflict(new
                {
                    error = exception.Closure == McpPlanClosure.Declined ? "plan_declined" : "plan_expired",
                    message = exception.Message,
                    action = exception.Action,
                    profileIds = exception.ProfileIds
                });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
            { return Results.BadRequest(new { error = "invalid_change", message = exception.Message }); }
        });
        group.MapPost("/prepare", async (McpChangeRequest request, McpWriteService writes, CancellationToken token) => Results.Json(await writes.PrepareAsync(request, token), McpChangeJson.Options));
        group.MapGet("/plans/{id:guid}", (Guid id, McpWriteService writes) => Results.Json(writes.GetPreview(id), McpChangeJson.Options));
        group.MapPost("/plans/{id:guid}/approve", async (Guid id, McpApplyRequest request, McpWriteService writes, CancellationToken token) => Results.Json(await writes.ApproveAsync(id, request.ConfirmationPhrase, token), McpChangeJson.Options));
        group.MapPost("/plans/{id:guid}/decline", async (Guid id, McpWriteService writes, CancellationToken token) => Results.Json(await writes.DeclineAsync(id, token), McpChangeJson.Options));
        group.MapGet("/operations/{id:guid}", async (Guid id, McpWriteService writes, CancellationToken token) => Results.Json(await writes.StatusAsync(id, token), McpChangeJson.Options));
        group.MapPost("/context", async (McpReadContextRequest request, McpWriteService writes, CancellationToken token) => Results.Json(await writes.ContextAsync(request, token), McpChangeJson.Options));

        app.MapGet("/mcp/download/{id:guid}", async (Guid id, McpWriteService writes, HttpContext http, CancellationToken token) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                var download = await writes.OpenDownloadAsync(id, token);
                return download is null ? Results.NotFound() : Results.File(download.Contents, download.ContentType, download.FileName);
            }
            catch (UnauthorizedAccessException) { return Results.NotFound(); }
        });
    }
}
