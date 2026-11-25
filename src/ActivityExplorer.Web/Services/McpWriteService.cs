using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Web.Services;

public enum McpPlanClosure { Declined, Expired }

public sealed class McpPlanClosedException(McpPlanClosure closure, string action, Guid[] profileIds) : InvalidOperationException(closure switch
{
    McpPlanClosure.Declined => "The change was declined. No operation was queued.",
    _ => "The preview expired. Prepare and review it again."
})
{
    public McpPlanClosure Closure { get; } = closure;
    public string Action { get; } = action;
    public Guid[] ProfileIds { get; } = profileIds;
}

public sealed partial class McpWriteService(
    McpWriteOptions options, IDbContextFactory<ExplorerDbContext> factory,
    IOwnerMutationLock mutations, AppDataPaths paths, IServiceProvider services, TimeProvider clock, ILogger<McpWriteService> logger) : IProfileDeletionCleanup, IDisposable
{
    private sealed record Plan(McpChangePreview Preview, McpChangeRequest Request, long Revision, LocalReviewPath? Path);
    private sealed record Asset(McpApprovedAsset Summary, string Path, string Hash);
    private sealed record ClosedPlan(string Action, Guid[] ProfileIds, McpPlanClosure Closure, DateTimeOffset RetainUntil);
    private readonly ConcurrentDictionary<Guid, Plan> _plans = new();
    private readonly ConcurrentDictionary<Guid, ClosedPlan> _closedPlans = new();
    private readonly ConcurrentDictionary<Guid, Asset> _assets = new();
    private readonly Dictionary<Guid, Guid?> _preparations = new();
    private readonly SemaphoreSlim _filePreviews = new(1);
    // The durable queued/running limit bounds admissions; the channel only notifies the worker of committed receipts.
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    public string Token { get; private set; } = "";
    public string Endpoint { get; private set; } = "";
    public bool Enabled => options.Enabled;
    public McpWriteOptions Options => options;
    public string ConnectionFile => Path.Combine(paths.Root, "mcp-connection.json");

    public async Task PublishConnectionAsync(string endpoint, CancellationToken token)
    {
        if (!Enabled) return;
        await CleanupArtifactsAsync(token, discardDownloads: true);
        var uri = new Uri(endpoint);
        if (!uri.IsLoopback || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("MCP writes require a loopback HTTP Web listener.");
        endpoint = endpoint.TrimEnd('/');
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var connection = new McpConnection(endpoint, secret, DataIdentity(paths.Root), options.ProfileIds,
            options.AllowDestructive, options.AllowAdministration);
        var temporary = Path.Combine(paths.Root, $".mcp-connection-{Guid.NewGuid():N}.tmp");
        try
        {
            var open = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) open.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, open))
                await JsonSerializer.SerializeAsync(stream, connection, McpChangeJson.Options, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, ConnectionFile, overwrite: true);
            Endpoint = endpoint;
            Token = secret;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The MCP write bridge could not publish mcp-connection.json.", exception);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                CredentialCleanupFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(4101, LogLevel.Warning, "Could not remove temporary MCP connection credentials.")]
    private static partial void CredentialCleanupFailed(ILogger logger, Exception exception);

    internal async Task RemoveConnectionAsync(CancellationToken cancellationToken)
    {
        var published = Token.Length > 0;
        Token = "";
        Endpoint = "";
        try
        {
            if (published) File.Delete(ConnectionFile);
        }
        finally
        {
            await CleanupArtifactsAsync(cancellationToken, discardDownloads: true, clearSelections: true);
        }
    }

    public static string DataIdentity(string root) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        OperatingSystem.IsWindows() ? Path.GetFullPath(root).ToUpperInvariant() : Path.GetFullPath(root))));

    public bool Authenticate(string? authorization)
    {
        if (!Enabled || Token.Length == 0 || authorization is null) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(authorization), Encoding.UTF8.GetBytes("Bearer " + Token));
    }

    public void Authorize(McpChangeRequest request)
    {
        AuthorizeAction(request.Action);
        if (request.ProfileId is null && !McpChangeCatalog.Administration(request.Action)) throw new ArgumentException("Select an explicit profile.");
        AuthorizeProfiles(Profiles(request));
    }

    private void AuthorizeAction(string action)
    {
        if (!Enabled || !McpChangeCatalog.Actions.Contains(action)) throw new UnauthorizedAccessException("This write capability is unavailable.");
        if (McpChangeCatalog.Destructive(action) && !options.AllowDestructive || McpChangeCatalog.Administration(action) && !options.AllowAdministration)
            throw new UnauthorizedAccessException("This action requires a separate destructive or administration opt-in.");
    }

    private void AuthorizeProfiles(IEnumerable<Guid> profileIds)
    {
        foreach (var id in profileIds)
            if (!options.ProfileIds.Contains(id)) throw new UnauthorizedAccessException("The requested profile or entity is unavailable.");
    }

    private static Guid[] Profiles(McpChangeRequest request)
    {
        var ids = request.ProfileId.HasValue ? new List<Guid> { request.ProfileId.Value } : [];
        if (request.Action == "transfer_activity") ids.Add(Read<Guid>(request.Arguments, "destinationProfileId"));
        return ids.Distinct().Order().ToArray();
    }

    public async Task<McpChangePreview> PrepareAsync(McpChangeRequest request, CancellationToken token)
    {
        Authorize(request);
        if (JsonSerializer.SerializeToUtf8Bytes(request, McpChangeJson.Options).Length > 1024 * 1024) throw new ArgumentException("The change exceeds the 1 MiB input limit. Select a Web-approved file instead.");
        request = request with { Arguments = request.Arguments.Clone(), ReviewedGeometrySha256 = null };
        var id = Guid.NewGuid();
        var (revision, asset) = await mutations.RunAsync(async () =>
        {
            Prune();
            if (_plans.Count + _preparations.Count >= options.MaxPendingPlans)
                throw new InvalidOperationException("Too many pending previews. Cancel or let existing previews expire.");
            Asset? selected = null;
            if (request.Arguments.TryGetProperty("assetId", out var property) && _assets.TryGetValue(property.GetGuid(), out var entry) &&
                entry.Summary.ProfileId == request.ProfileId && entry.Summary.ExpiresAt > clock.GetUtcNow()) selected = entry;
            await using var db = await factory.CreateDbContextAsync(token);
            var captured = await McpLibraryRevision.ReadAsync(db, token);
            _preparations.Add(id, selected?.Summary.Id);
            return (captured, selected);
        }, token);

        var published = false;
        try
        {
            var (details, path) = await DescribeWithBudgetAsync(request, asset, token);
            request = request with { ReviewedGeometrySha256 = path?.Digest };
            var phrase = await ConfirmationPhraseAsync(request, token);
            return await mutations.RunAsync(async () =>
            {
                await using var db = await factory.CreateDbContextAsync(token);
                if (revision != await McpLibraryRevision.ReadAsync(db, token))
                    throw new InvalidOperationException("The application data changed while preparing this preview. Prepare the change again.");
                var now = clock.GetUtcNow();
                var expiresAt = now.AddMinutes(options.PlanLifetimeMinutes);
                if (asset is not null)
                {
                    if (asset.Summary.ExpiresAt <= now || !_assets.ContainsKey(asset.Summary.Id))
                        throw new InvalidOperationException("The selected file or folder became unavailable. Approve it in the Web app again.");
                    if (asset.Summary.ExpiresAt < expiresAt) expiresAt = asset.Summary.ExpiresAt;
                }
                var preview = new McpChangePreview(id, request.Action, Profiles(request), request.Action.Replace('_', ' '), details,
                    expiresAt, phrase, Endpoint + "/mcp/review/" + id);
                if (!_plans.TryAdd(id, new(preview, request, revision, path))) throw new InvalidOperationException("Could not prepare the change.");
                _preparations.Remove(id);
                published = true;
                return preview;
            }, token);
        }
        finally
        {
            if (!published) await mutations.RunAsync(() => Task.FromResult(_preparations.Remove(id)), CancellationToken.None);
        }
    }

    private async Task<(JsonElement Details, LocalReviewPath? Path)> DescribeWithBudgetAsync(McpChangeRequest request, Asset? asset, CancellationToken token)
    {
        if (asset?.Summary.Kind != "file" && request.Action is not ("create_segment_from_activity" or "create_segment_from_route" or
            "create_subsegment" or "create_route_from_activity" or "create_segment_path" or "create_route_path"))
            return await DescribeAsync(request, token);

        await _filePreviews.WaitAsync(token);
        try { return await DescribeAsync(request, token); }
        finally { _filePreviews.Release(); }
    }

    private async Task<string?> ConfirmationPhraseAsync(McpChangeRequest request, CancellationToken token)
    {
        if (!McpChangeCatalog.Destructive(request.Action)) return null;
        if (request.Action != "delete_profile") return "CONFIRM " + request.Action;
        await using var db = await factory.CreateDbContextAsync(token);
        return "DELETE " + await db.Owners.Where(owner => owner.Id == request.ProfileId).Select(owner => owner.DisplayName).SingleAsync(token);
    }

    private bool IsPreparingAsset(Guid assetId) => _preparations.ContainsValue(assetId);

    public McpChangePreview GetPreview(Guid id) => RequirePlan(id).Preview;

    internal (McpChangePreview Preview, LocalReviewPath? Path) GetLocalReview(Guid id)
    {
        var plan = RequirePlan(id);
        return (plan.Preview, plan.Path);
    }

    public Task<McpApplyResult> DeclineAsync(Guid id, CancellationToken token) => mutations.RunAsync(async () =>
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var existing = await db.McpOperations.AsNoTracking().SingleOrDefaultAsync(operation => operation.Id == id, token);
        if (existing is not null)
        {
            var operation = await ReadStatusAsync(db, existing, token);
            return new McpApplyResult(operation.Status, id, Operation: operation);
        }
        try
        {
            var plan = RequirePlan(id, closeExpired: true);
            var now = clock.GetUtcNow();
            var closure = plan.Preview.ExpiresAt <= now ? McpPlanClosure.Expired : McpPlanClosure.Declined;
            ClosePlan(id, plan, closure, now);
            if (closure == McpPlanClosure.Expired) throw ClosedException(plan, closure);
        }
        catch (McpPlanClosedException exception) when (exception.Closure == McpPlanClosure.Declined) { }
        return new McpApplyResult("declined", id);
    }, token);

    public Task<McpOperationStatus> ApproveAsync(Guid id, string? phrase, CancellationToken token) => mutations.RunAsync(async () =>
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var existing = await db.McpOperations.AsNoTracking().SingleOrDefaultAsync(operation => operation.Id == id, token);
        if (existing is not null) return await ReadStatusAsync(db, existing, token);
        var plan = RequirePlan(id, closeExpired: true);
        if (!string.Equals(phrase, plan.Preview.ConfirmationPhrase, StringComparison.Ordinal)) throw new ArgumentException("The confirmation phrase does not match the reviewed change.");
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (plan.Revision != await McpLibraryRevision.ReadAsync(db, token)) throw new InvalidOperationException("The application data changed after preview. Prepare and review the change again.");
        if (await db.McpOperations.CountAsync(operation => operation.Status == McpExecutionState.Queued || operation.Status == McpExecutionState.Running, token) >= 32)
            throw new InvalidOperationException("The write queue is full. Try again when an operation finishes.");
        var now = clock.GetUtcNow();
        if (plan.Preview.ExpiresAt <= now)
        {
            ClosePlan(id, plan, McpPlanClosure.Expired, now);
            throw ClosedException(plan, McpPlanClosure.Expired);
        }
        var approved = new McpOperation
        {
            Id = id,
            Action = plan.Request.Action,
            ProfileIdsJson = JsonSerializer.Serialize(plan.Preview.ProfileIds),
            RequestJson = JsonSerializer.Serialize(plan.Request, McpChangeJson.Options),
            RevisionToken = RevisionToken(plan.Revision),
            CreatedAtUtc = now
        };
        db.McpOperations.Add(approved);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(CancellationToken.None);
        _plans.TryRemove(id, out _);
        await _queue.Writer.WriteAsync(id, CancellationToken.None);
        return await ReadStatusAsync(db, approved, CancellationToken.None);
    }, token);

    public async Task<McpOperationStatus> StatusAsync(Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var operation = await db.McpOperations.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token)
            ?? throw new KeyNotFoundException("The operation is unavailable.");
        return await ReadStatusAsync(db, operation, token);
    }

    private async Task<McpOperationStatus> ReadStatusAsync(ExplorerDbContext db, McpOperation operation, CancellationToken token)
    {
        AuthorizeReceipt(operation);
        var result = operation.ResultJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(operation.ResultJson);
        var progress = new OperationProgress(operation.Status);
        if (operation.Action == "import_activities" && operation.EntityId.HasValue)
        {
            var batch = await db.ImportBatches.AsNoTracking().SingleOrDefaultAsync(item => item.Id == operation.EntityId, token);
            if (batch is not null)
            {
                progress = progress with { Import = batch.Status };
                result = McpChangeJson.Element(new { importBatchId = batch.Id, batch.ActivitiesCreated, batch.ActivitiesUpdated, batch.DuplicatesSkipped, batch.UnsupportedSkipped, batch.Warnings });
            }
        }
        return new(operation.Id, operation.Action, progress.ProtocolStatus, operation.EntityId, result, operation.Message, operation.CreatedAtUtc, operation.CompletedAtUtc, JsonSerializer.Deserialize<Guid[]>(operation.ProfileIdsJson)!);
    }

    private sealed record OperationProgress(McpExecutionState Execution, ImportStatus? Import = null)
    {
        public string ProtocolStatus => Import?.ToString() ?? McpExecutionStates.ToValue(Execution);
    }

    private void AuthorizeReceipt(McpOperation operation)
    {
        if (!Enabled || McpChangeCatalog.Destructive(operation.Action) && !options.AllowDestructive
            || McpChangeCatalog.Administration(operation.Action) && !options.AllowAdministration
            || JsonSerializer.Deserialize<Guid[]>(operation.ProfileIdsJson)!.Any(id => !options.ProfileIds.Contains(id)))
            throw new UnauthorizedAccessException("The operation is unavailable.");
    }

    private Plan RequirePlan(Guid id, bool closeExpired = false)
    {
        var now = clock.GetUtcNow();
        if (_plans.TryGetValue(id, out var plan))
        {
            Authorize(plan.Request);
            if (plan.Preview.ExpiresAt > now) return plan;
            if (closeExpired) ClosePlan(id, plan, McpPlanClosure.Expired, now);
            throw ClosedException(plan, McpPlanClosure.Expired);
        }
        if (_closedPlans.TryGetValue(id, out var closed) && closed.RetainUntil > now)
        {
            AuthorizeAction(closed.Action);
            AuthorizeProfiles(closed.ProfileIds);
            throw new McpPlanClosedException(closed.Closure, closed.Action, closed.ProfileIds);
        }
        throw new KeyNotFoundException("The preview is unavailable. Prepare it again.");
    }

    private static McpPlanClosedException ClosedException(Plan plan, McpPlanClosure closure) =>
        new(closure, plan.Request.Action, Profiles(plan.Request));

    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var pair in _closedPlans)
            if (pair.Value.RetainUntil <= now) _closedPlans.TryRemove(pair.Key, out _);
        foreach (var pair in _plans)
            if (pair.Value.Preview.ExpiresAt <= now) ClosePlan(pair.Key, pair.Value, McpPlanClosure.Expired, now);
    }

    private void ClosePlan(Guid id, Plan plan, McpPlanClosure closure, DateTimeOffset now)
    {
        if (_closedPlans.Count >= options.MaxPendingPlans * 2)
        {
            var oldest = _closedPlans.MinBy(pair => pair.Value.RetainUntil);
            _closedPlans.TryRemove(oldest.Key, out _);
        }
        _closedPlans[id] = new(plan.Request.Action, Profiles(plan.Request), closure, now.AddMinutes(options.PlanLifetimeMinutes));
        _plans.TryRemove(id, out _);
    }

    private static string RevisionToken(long revision) => "r:" + revision.ToString(CultureInfo.InvariantCulture);

    public void Dispose() => _filePreviews.Dispose();

    private static T Read<T>(JsonElement args, string name) => args.TryGetProperty(name, out var value)
        ? value.Deserialize<T>(McpChangeJson.Options) ?? throw new ArgumentException($"{name} is required.")
        : throw new ArgumentException($"{name} is required.");
    private static T? Optional<T>(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
        ? value.Deserialize<T>(McpChangeJson.Options) : default;
}

public sealed partial class McpOperationWorker(
    McpWriteService writes, Microsoft.AspNetCore.Hosting.Server.IServer server, IConfiguration configuration,
    ILogger<McpOperationWorker> logger) : BackgroundService, IHostedLifecycleService
{
    private int _stopped;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (writes.Enabled) await writes.RecoverAsync(cancellationToken);
        else await writes.CleanupArtifactsAsync(cancellationToken, discardDownloads: true);
        await base.StartAsync(cancellationToken);
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        if (!writes.Enabled) return Task.CompletedTask;
        var bound = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        var addresses = bound is { Addresses.Count: > 0 }
            ? bound.Addresses
            : (configuration["Urls"] ?? "http://localhost:8342").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (addresses.Count == 0 || addresses.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var address) ||
                !address.IsLoopback || address.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("All Web listeners must be loopback-only when MCP writes are enabled.");
        return writes.PublishConnectionAsync(addresses.First(), cancellationToken);
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Interlocked.Exchange(ref _stopped, 1) == 0 && writes.Enabled
        ? writes.RemoveConnectionAsync(cancellationToken)
        : Task.CompletedTask;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        writes.Enabled ? Task.WhenAll(ProcessQueueAsync(stoppingToken), writes.RunArtifactCleanupAsync(stoppingToken)) : Task.CompletedTask;

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        if (!writes.Enabled) return;
        await foreach (var id in writes.ReadQueueAsync(stoppingToken))
        {
            try { await writes.ExecuteAsync(id, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { RecordingFailed(logger, id, exception); }
        }
    }

    [LoggerMessage(4100, LogLevel.Error, "MCP operation {OperationId} could not be recorded.")]
    private static partial void RecordingFailed(ILogger logger, Guid operationId, Exception exception);
}
