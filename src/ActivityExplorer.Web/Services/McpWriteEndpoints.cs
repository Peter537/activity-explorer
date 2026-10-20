using System.Net;
using System.Text.Json;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

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
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
            { return Results.BadRequest(new { error = "invalid_change", message = exception.Message }); }
        });
        group.MapPost("/prepare", async (McpChangeRequest request, McpWriteService writes, CancellationToken token) => Results.Json(await writes.PrepareAsync(request, token), McpChangeJson.Options));
        group.MapGet("/plans/{id:guid}", (Guid id, McpWriteService writes) => Results.Json(writes.GetPreview(id), McpChangeJson.Options));
        group.MapPost("/plans/{id:guid}/approve", async (Guid id, McpApplyRequest request, McpWriteService writes, CancellationToken token) => Results.Json(await writes.ApproveAsync(id, request.ConfirmationPhrase, token), McpChangeJson.Options));
        group.MapPost("/plans/{id:guid}/decline", (Guid id, McpWriteService writes) => { writes.Decline(id); return Results.Ok(new { declined = true }); });
        group.MapGet("/operations/{id:guid}", async (Guid id, McpWriteService writes, CancellationToken token) => Results.Json(await writes.StatusAsync(id, token), McpChangeJson.Options));
        group.MapPost("/context", async (McpReadContextRequest request, McpWriteService writes, CancellationToken token) => Results.Json(await writes.ContextAsync(request, token), McpChangeJson.Options));

        app.MapGet("/mcp/download/{id:guid}", async (Guid id, McpWriteService writes, IDbContextFactory<ExplorerDbContext> factory,
            TimeProvider clock, CancellationToken token) =>
        {
            if (!writes.Enabled) return Results.NotFound();
            try
            {
                var status = await writes.StatusAsync(id, token);
                if (status.Status != "completed") return Results.NotFound();
                await using var db = await factory.CreateDbContextAsync(token);
                var operation = await db.McpOperations.AsNoTracking().SingleAsync(item => item.Id == id, token);
                if (operation.ResultJson is null) return Results.NotFound();
                var selector = JsonSerializer.Deserialize<JsonElement>(operation.ResultJson);
                var profileId = selector.GetProperty("profileId").GetGuid();
                if (operation.Action is not ("export_profile" or "export_route" or "download_original")
                    || selector.GetProperty("expiresAt").GetDateTimeOffset() <= clock.GetUtcNow()
                    || !await db.Owners.AnyAsync(item => item.Id == profileId, token)) return Results.NotFound();
                var path = writes.DownloadPath(id);
                return File.Exists(path) ? Results.File(path, selector.GetProperty("contentType").GetString()!, selector.GetProperty("fileName").GetString()) : Results.NotFound();
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or KeyNotFoundException) { return Results.NotFound(); }
        });
    }
}
