using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ActivityExplorer.Web.Services;

public sealed partial class McpWriteService(
    McpWriteOptions options, IDbContextFactory<ExplorerDbContext> factory,
    IOwnerMutationLock mutations, AppDataPaths paths, IServiceProvider services, TimeProvider clock, ILogger<McpWriteService> logger)
{
    private sealed record Plan(McpChangePreview Preview, McpChangeRequest Request, string Fingerprint);
    private sealed record Asset(McpApprovedAsset Summary, string Path, string Hash);
    private readonly ConcurrentDictionary<Guid, Plan> _plans = new();
    private readonly ConcurrentDictionary<Guid, Asset> _assets = new();
    private readonly Channel<Guid> _queue = Channel.CreateBounded<Guid>(32);
    public string Token { get; private set; } = "";
    public string Endpoint { get; private set; } = "";
    public bool Enabled => options.Enabled;
    public McpWriteOptions Options => options;
    public string ConnectionFile => Path.Combine(paths.Root, "mcp-connection.json");

    public async Task PublishConnectionAsync(string endpoint, CancellationToken token)
    {
        if (!Enabled) return;
        CleanupDownloads();
        await CleanupOrphanAssetsAsync(token);
        var uri = new Uri(endpoint);
        if (!uri.IsLoopback) throw new InvalidOperationException("MCP writes require a loopback Web listener.");
        Endpoint = endpoint.TrimEnd('/');
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var connection = new McpConnection(Endpoint, Token, DataIdentity(paths.Root), options.ProfileIds,
            options.AllowDestructive, options.AllowAdministration);
        await File.WriteAllTextAsync(ConnectionFile, JsonSerializer.Serialize(connection, McpChangeJson.Options), token);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(ConnectionFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void RemoveConnection()
    {
        if (Token.Length > 0 && File.Exists(ConnectionFile)) File.Delete(ConnectionFile);
        CleanupAssets(all: true);
        CleanupDownloads();
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
        if (!Enabled || !McpChangeCatalog.Actions.Contains(request.Action)) throw new UnauthorizedAccessException("This write capability is unavailable.");
        if (McpChangeCatalog.Destructive(request.Action) && !options.AllowDestructive || McpChangeCatalog.Administration(request.Action) && !options.AllowAdministration)
            throw new UnauthorizedAccessException("This action requires a separate destructive or administration opt-in.");
        if (request.ProfileId is null && !McpChangeCatalog.Administration(request.Action)) throw new ArgumentException("Select an explicit profile.");
        foreach (var id in Profiles(request))
            if (!options.ProfileIds.Contains(id)) throw new UnauthorizedAccessException("The requested profile or entity is unavailable.");
    }

    private static Guid[] Profiles(McpChangeRequest request)
    {
        var ids = request.ProfileId.HasValue ? new List<Guid> { request.ProfileId.Value } : [];
        if (request.Action == "transfer_activity") ids.Add(Read<Guid>(request.Arguments, "destinationProfileId"));
        return ids.Distinct().Order().ToArray();
    }

    public Task<McpChangePreview> PrepareAsync(McpChangeRequest request, CancellationToken token) => mutations.RunAsync(async () =>
    {
        Authorize(request);
        if (JsonSerializer.SerializeToUtf8Bytes(request, McpChangeJson.Options).Length > 1024 * 1024) throw new ArgumentException("The change exceeds the 1 MiB input limit. Select a Web-approved file instead.");
        Prune();
        if (_plans.Count >= options.MaxPendingPlans) throw new InvalidOperationException("Too many pending previews. Cancel or let existing previews expire.");
        var details = await DescribeAsync(request, token);
        var fingerprint = await FingerprintAsync(token);
        var id = Guid.NewGuid();
        string? phrase = null;
        if (McpChangeCatalog.Destructive(request.Action))
        {
            await using var db = await factory.CreateDbContextAsync(token);
            phrase = request.Action == "delete_profile"
                ? "DELETE " + await db.Owners.Where(owner => owner.Id == request.ProfileId).Select(owner => owner.DisplayName).SingleAsync(token)
                : "CONFIRM " + request.Action;
        }
        var expiresAt = clock.GetUtcNow().AddMinutes(options.PlanLifetimeMinutes);
        if (request.Arguments.TryGetProperty("assetId", out var assetId) && _assets.TryGetValue(assetId.GetGuid(), out var asset))
            expiresAt = asset.Summary.ExpiresAt < expiresAt ? asset.Summary.ExpiresAt : expiresAt;
        var preview = new McpChangePreview(id, request.Action, Profiles(request), request.Action.Replace('_', ' '), details,
            expiresAt, phrase, Endpoint + "/mcp/review/" + id);
        if (!_plans.TryAdd(id, new(preview, request, fingerprint))) throw new InvalidOperationException("Could not prepare the change.");
        return preview;
    }, token);

    public McpChangePreview GetPreview(Guid id)
    {
        Prune();
        var plan = RequirePlan(id);
        Authorize(plan.Request);
        return plan.Preview;
    }

    public void Decline(Guid id)
    {
        var plan = RequirePlan(id);
        Authorize(plan.Request);
        _plans.TryRemove(id, out _);
    }

    public Task<McpOperationStatus> ApproveAsync(Guid id, string? phrase, CancellationToken token) => mutations.RunAsync(async () =>
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var existing = await db.McpOperations.AsNoTracking().SingleOrDefaultAsync(operation => operation.Id == id, token);
        if (existing is not null) return await StatusAsync(id, token);
        var plan = RequirePlan(id);
        Authorize(plan.Request);
        if (!string.Equals(phrase, plan.Preview.ConfirmationPhrase, StringComparison.Ordinal)) throw new ArgumentException("The confirmation phrase does not match the reviewed change.");
        if (plan.Preview.ExpiresAt <= clock.GetUtcNow()) throw new InvalidOperationException("The preview expired. Prepare and review it again.");
        if (plan.Fingerprint != await FingerprintAsync(token)) throw new InvalidOperationException("The application data changed after preview. Prepare and review the change again.");
        if (await db.McpOperations.CountAsync(operation => operation.Status == "queued" || operation.Status == "running", token) >= 32)
            throw new InvalidOperationException("The write queue is full. Try again when an operation finishes.");
        db.McpOperations.Add(new McpOperation
        {
            Id = id,
            Action = plan.Request.Action,
            ProfileIdsJson = JsonSerializer.Serialize(plan.Preview.ProfileIds),
            RequestJson = JsonSerializer.Serialize(plan.Request, McpChangeJson.Options),
            Fingerprint = plan.Fingerprint,
            CreatedAtUtc = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(token);
        if (!_queue.Writer.TryWrite(id)) throw new InvalidOperationException("The write queue is unavailable. Check operation status before retrying.");
        _plans.TryRemove(id, out _);
        return await StatusAsync(id, CancellationToken.None);
    }, token);

    public async Task<McpOperationStatus> StatusAsync(Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var operation = await db.McpOperations.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token)
            ?? throw new KeyNotFoundException("The operation is unavailable.");
        AuthorizeReceipt(operation);
        var result = operation.ResultJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(operation.ResultJson);
        var status = operation.Status;
        if (operation.Action == "import_activities" && operation.EntityId.HasValue)
        {
            var batch = await db.ImportBatches.AsNoTracking().SingleOrDefaultAsync(item => item.Id == operation.EntityId, token);
            if (batch is not null)
            {
                status = batch.Status.ToString();
                result = McpChangeJson.Element(new { importBatchId = batch.Id, batch.ActivitiesCreated, batch.ActivitiesUpdated, batch.DuplicatesSkipped, batch.UnsupportedSkipped, batch.Warnings });
            }
        }
        return new(id, operation.Action, status, operation.EntityId, result, operation.Message, operation.CreatedAtUtc, operation.CompletedAtUtc, JsonSerializer.Deserialize<Guid[]>(operation.ProfileIdsJson)!);
    }

    private void AuthorizeReceipt(McpOperation operation)
    {
        if (!Enabled || McpChangeCatalog.Destructive(operation.Action) && !options.AllowDestructive
            || McpChangeCatalog.Administration(operation.Action) && !options.AllowAdministration
            || JsonSerializer.Deserialize<Guid[]>(operation.ProfileIdsJson)!.Any(id => !options.ProfileIds.Contains(id)))
            throw new UnauthorizedAccessException("The operation is unavailable.");
    }

    private Plan RequirePlan(Guid id) => _plans.TryGetValue(id, out var plan) && plan.Preview.ExpiresAt > clock.GetUtcNow()
        ? plan : throw new KeyNotFoundException("The preview is unavailable or expired. Prepare it again.");

    private void Prune()
    {
        foreach (var pair in _plans.Where(pair => pair.Value.Preview.ExpiresAt <= clock.GetUtcNow())) _plans.TryRemove(pair.Key, out _);
    }

    public async Task<McpApprovedAsset> ApproveFileAsync(Guid profileId, string name, Stream input, long maximumBytes, CancellationToken token)
    {
        if (!Enabled || !options.ProfileIds.Contains(profileId)) throw new UnauthorizedAccessException("Select a permitted profile.");
        if (Token.Length == 0) throw new InvalidOperationException("The Web write connection is starting. Try the selection again shortly.");
        var fileName = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." || !new[] { ".fit", ".gpx", ".tcx", ".gz", ".zip", ".kml", ".geojson", ".json" }.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Select a supported activity or path file.");
        CleanupAssets(all: false);
        if (_assets.Count >= options.MaxPendingPlans) throw new InvalidOperationException("Too many approved assets. Restart or use existing selections.");
        var id = Guid.NewGuid();
        var directory = Path.Combine(paths.StagingPath, "mcp-assets", id.ToString("N"));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, Path.GetFileName(name));
        try
        {
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += read;
                    if (total > maximumBytes) throw new InvalidDataException("The selected file exceeds the upload limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
            }
            var summary = new McpApprovedAsset(id, profileId, Path.GetFileName(name), "file", new FileInfo(target).Length, clock.GetUtcNow().AddHours(1));
            _assets[id] = new(summary, target, await Fingerprint.Sha256Async(target, token));
            return summary;
        }
        catch { File.Delete(target); Directory.Delete(directory); throw; }
    }

    public McpApprovedAsset ApproveFolder(Guid profileId, string path)
    {
        if (!Enabled || !options.ProfileIds.Contains(profileId)) throw new UnauthorizedAccessException("Select a permitted profile.");
        if (Token.Length == 0) throw new InvalidOperationException("The Web write connection is starting. Try the selection again shortly.");
        CleanupAssets(all: false);
        if (_assets.Count >= options.MaxPendingPlans) throw new InvalidOperationException("Too many approved assets.");
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Select an existing local folder, without a symbolic link.");
        var summary = new McpApprovedAsset(Guid.NewGuid(), profileId, new DirectoryInfo(fullPath).Name, "folder", 0, clock.GetUtcNow().AddHours(1));
        _assets[summary.Id] = new(summary, fullPath, "");
        return summary;
    }

    private Asset RequireAsset(Guid id, Guid profileId, string kind)
    {
        if (!_assets.TryGetValue(id, out var asset) || asset.Summary.ProfileId != profileId || asset.Summary.Kind != kind || asset.Summary.ExpiresAt <= clock.GetUtcNow())
            throw new ArgumentException("Select or approve the file or folder in the Web app again.");
        if (kind == "folder" && (!Directory.Exists(asset.Path) || (File.GetAttributes(asset.Path) & FileAttributes.ReparsePoint) != 0))
            throw new ArgumentException("The approved folder changed. Select it again in the Web app.");
        return asset;
    }

    private void CleanupAssets(bool all, Guid? profileId = null)
    {
        foreach (var item in _assets.Where(item => (all || item.Value.Summary.ExpiresAt <= clock.GetUtcNow()) && (!profileId.HasValue || item.Value.Summary.ProfileId == profileId)))
        {
            if (!_assets.TryRemove(item.Key, out var asset) || asset.Summary.Kind != "file") continue;
            var directory = ManagedPathGuard.ResolveUnder(Path.Combine(paths.StagingPath, "mcp-assets"), Path.GetDirectoryName(asset.Path)!);
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _assets.TryAdd(item.Key, asset);
                AssetCleanupPending(logger, item.Key, exception);
            }
        }
    }

    private async Task CleanupOrphanAssetsAsync(CancellationToken token)
    {
        var root = ManagedPathGuard.ResolveUnder(paths.StagingPath, Path.Combine(paths.StagingPath, "mcp-assets"));
        if (!Directory.Exists(root)) return;
        await using var db = await factory.CreateDbContextAsync(token);
        var references = await db.ImportBatches.AsNoTracking().Where(batch => batch.Status == ImportStatus.Queued || batch.Status == ImportStatus.Running || batch.Status == ImportStatus.Interrupted)
            .Select(batch => batch.StagedPath).ToListAsync(token);
        references.AddRange((await db.FileOperations.AsNoTracking().Where(operation => operation.State == FileOperationState.Pending && operation.SourceRelativePath != null)
            .Select(operation => operation.SourceRelativePath!).ToArrayAsync(token)));
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var retained = references.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.GetDirectoryName(Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(paths.Root, path)))!).ToHashSet(comparer);
        foreach (var candidate in Directory.GetDirectories(root))
        {
            var directory = ManagedPathGuard.ResolveUnder(root, candidate);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            if (retained.Contains(directory) || !Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) || _assets.ContainsKey(id)) continue;
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { AssetCleanupPending(logger, id, exception); }
        }
    }

    public string DownloadPath(Guid id) => ManagedPathGuard.ResolveUnder(paths.StagingPath,
        Path.Combine(paths.StagingPath, "mcp-downloads", id.ToString("N"), "content"));

    private void CleanupDownloads()
    {
        var directory = ManagedPathGuard.ResolveUnder(paths.StagingPath, Path.Combine(paths.StagingPath, "mcp-downloads"));
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { DownloadsCleanupPending(logger, exception); }
    }

    [LoggerMessage(4102, LogLevel.Warning, "Temporary MCP downloads could not be removed yet; cleanup will be retried when the Web app restarts.")]
    private static partial void DownloadsCleanupPending(ILogger logger, Exception exception);

    [LoggerMessage(4101, LogLevel.Warning, "MCP temporary asset {AssetId} could not be removed yet.")]
    private static partial void AssetCleanupPending(ILogger logger, Guid assetId, Exception exception);

    private async Task<string> FingerprintAsync(CancellationToken token)
    {
        string[] tables = ["Owners", "Activities", "ActivityStreams", "ActivityLaps", "ActivityMetrics", "SourceFiles", "Routes", "Segments", "SegmentLinks", "SegmentEfforts", "Tags", "ActivityTags", "SavedSearches", "Goals", "GoalDefinitionRevisions", "WatchedFolders", "ApplicationSettings"];
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var table in tables)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(table));
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = table == "WatchedFolders"
                ? "SELECT \"Id\", \"OwnerId\", \"Path\", \"Enabled\" FROM \"WatchedFolders\" ORDER BY rowid"
                : $"SELECT * FROM \"{table}\" ORDER BY rowid";
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            while (await reader.ReadAsync(token))
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    var value = reader.GetValue(index);
                    hash.AppendData([(byte)(value is DBNull ? 0 : value is byte[]? 1 : value is string ? 2 : value is long ? 3 : 4)]);
                    var bytes = value is byte[] binary ? binary : Encoding.UTF8.GetBytes(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
                    hash.AppendData(BitConverter.GetBytes(bytes.Length));
                    hash.AppendData(bytes);
                }
        }
        await transaction.CommitAsync(token);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static T Read<T>(JsonElement args, string name) => args.TryGetProperty(name, out var value)
        ? value.Deserialize<T>(McpChangeJson.Options) ?? throw new ArgumentException($"{name} is required.")
        : throw new ArgumentException($"{name} is required.");
    private static T? Optional<T>(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
        ? value.Deserialize<T>(McpChangeJson.Options) : default;
}

public sealed partial class McpOperationWorker(McpWriteService writes, ILogger<McpOperationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!writes.Enabled) return;
        await writes.RecoverAsync(stoppingToken);
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
