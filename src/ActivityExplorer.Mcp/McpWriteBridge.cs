using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using ActivityExplorer.Mcp.Configuration;

namespace ActivityExplorer.Mcp;

public sealed class McpBridgeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class McpWriteBridge(McpOptions options, AppDataPaths paths) : IDisposable
{
    internal sealed record ConfirmationChallenge(string State, DateTimeOffset ExpiresAt);
    // Tool targets can be recreated between MRTR calls; confirmation state belongs to this shared service.
    internal ConcurrentDictionary<Guid, ConfirmationChallenge> Challenges { get; } = new();
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds) };

    public void Authorize(string action, IEnumerable<Guid> profileIds)
    {
        if (!options.WritesEnabled) throw new McpBridgeException("read_only", "Writes are disabled.");
        if (McpChangeCatalog.Destructive(action) && !options.AllowDestructive || McpChangeCatalog.Administration(action) && !options.AllowAdministration)
            throw new McpBridgeException("permission_denied", "This action requires a separate destructive or administration opt-in.");
        if (profileIds.Any(id => options.AllowedWriteProfileIds?.Contains(id) != true))
            throw new McpBridgeException("permission_denied", "The requested profile or entity is unavailable.");
    }

    public async Task<McpChangePreview> PrepareAsync(McpChangeRequest request, CancellationToken token)
    {
        var ids = request.ProfileId.HasValue ? new List<Guid> { request.ProfileId.Value } : [];
        if (request.Action == "transfer_activity") ids.Add(request.Arguments.GetProperty("destinationProfileId").GetGuid());
        Authorize(request.Action, ids);
        return await SendAsync<McpChangePreview>(HttpMethod.Post, "prepare", request, token);
    }
    public async Task<McpChangePreview> PreviewAsync(Guid id, CancellationToken token)
    {
        var preview = await SendAsync<McpChangePreview>(HttpMethod.Get, "plans/" + id, null, token);
        Authorize(preview.Action, preview.ProfileIds);
        return preview;
    }
    public async Task<McpOperationStatus> ApproveAsync(Guid id, string? phrase, CancellationToken token)
    {
        _ = await PreviewAsync(id, token);
        return await SendAsync<McpOperationStatus>(HttpMethod.Post, $"plans/{id}/approve", new McpApplyRequest(phrase), token);
    }
    public Task DeclineAsync(Guid id, CancellationToken token) => SendAsync<JsonElement>(HttpMethod.Post, $"plans/{id}/decline", new { }, token);
    public async Task<McpOperationStatus> StatusAsync(Guid id, CancellationToken token)
    {
        var status = await SendAsync<McpOperationStatus>(HttpMethod.Get, "operations/" + id, null, token);
        Authorize(status.Action, status.ProfileIds);
        return status;
    }
    public Task<McpWriteContext> ContextAsync(McpReadContextRequest request, CancellationToken token)
    {
        Authorize("context", [request.ProfileId]);
        return SendAsync<McpWriteContext>(HttpMethod.Post, "context", request, token);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken token)
    {
        if (!options.WritesEnabled) throw new McpBridgeException("read_only", "Writes are disabled.");
        try
        {
            var connection = JsonSerializer.Deserialize<McpConnection>(await File.ReadAllTextAsync(options.ConnectionFile ?? Path.Combine(paths.Root, "mcp-connection.json"), token), McpChangeJson.Options)
                ?? throw new McpBridgeException("web_unavailable", "Start the matching Web app with MCP writes enabled.");
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? Path.GetFullPath(paths.Root).ToUpperInvariant() : Path.GetFullPath(paths.Root))));
            if (connection.Version != 1 || connection.DataIdentity != identity || !Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var endpoint)
                || !endpoint.IsLoopback || endpoint.Scheme is not ("http" or "https") || endpoint.AbsolutePath != "/" || endpoint.Query.Length > 0 || endpoint.UserInfo.Length > 0 || endpoint.Fragment.Length > 0)
                throw new McpBridgeException("web_incompatible", "The Web connection does not match this data root or protocol.");
            using var request = new HttpRequestMessage(method, new Uri(endpoint, "/internal/mcp/" + path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
            if (body is not null) request.Content = JsonContent.Create(body, options: McpChangeJson.Options);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.Content.Headers.ContentLength > options.MaxResponseBytes)
                throw new McpBridgeException("response_too_large", "The Web response exceeds the size limit. Narrow the selection or review it in the Web app.");
            await response.Content.LoadIntoBufferAsync(options.MaxResponseBytes, token);
            if (!response.IsSuccessStatusCode)
            {
                var code = response.StatusCode == HttpStatusCode.NotFound ? "not_found" : response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ? "permission_denied" : "invalid_change";
                var message = "The Web app rejected the request. Check its permissions and prepare a fresh preview if data changed.";
                try { var error = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token); if (error.TryGetProperty("message", out var detail)) message = detail.GetString() ?? message; }
                catch (JsonException) { }
                throw new McpBridgeException(code, message);
            }
            if (response.Content.Headers.ContentLength == 0) return default!;
            return await response.Content.ReadFromJsonAsync<T>(McpChangeJson.Options, token) ?? throw new McpBridgeException("web_incompatible", "The Web app returned an invalid result.");
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        { throw new McpBridgeException("web_unavailable", "The Web app is unavailable. Start it with MCP writes enabled; check operation status before retrying a write."); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new McpBridgeException("timeout", "The Web request timed out. Check operation status before retrying a write."); }
        catch (JsonException) { throw new McpBridgeException("web_incompatible", "The Web app returned incompatible data."); }
    }

    public void Dispose() => _http.Dispose();
}
