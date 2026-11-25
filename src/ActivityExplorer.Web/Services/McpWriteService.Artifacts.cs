using System.Collections.Concurrent;
using System.Text.Json;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Web.Services;

public sealed partial class McpWriteService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<Guid, Guid> _assetUploads = new();
    private string AssetRoot => ArtifactRoot("mcp-assets");
    private string DownloadRoot => ArtifactRoot("mcp-downloads");

    private string ArtifactRoot(string name) =>
        ManagedPathGuard.ResolveUnder(paths.StagingPath, Path.Combine(paths.StagingPath, name));

    public async Task<McpApprovedAsset> ApproveFileAsync(Guid profileId, string name, Stream input, long maximumBytes, CancellationToken token)
    {
        var fileName = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." || !new[] { ".fit", ".gpx", ".tcx", ".gz", ".zip", ".kml", ".geojson", ".json" }.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Select a supported activity or path file.");

        var id = Guid.NewGuid();
        var root = AssetRoot;
        var directory = await mutations.RunAsync(async () =>
        {
            await ValidateSelectionAsync(profileId, token);
            var reserved = ManagedStagingDirectory.Create(root, id);
            _assetUploads.TryAdd(id, profileId);
            return reserved;
        }, token);
        var target = Path.Combine(directory, fileName);
        try
        {
            long length = 0;
            // Profile deletion can unlink this private copy while a pending write unwinds.
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Delete))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    length += read;
                    if (length > maximumBytes) throw new InvalidDataException("The selected file exceeds the upload limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
            }
            var hash = await Fingerprint.Sha256Async(target, token);
            return await mutations.RunAsync(async () =>
            {
                await RequireExistingProfileAsync(profileId, token);
                var summary = new McpApprovedAsset(id, profileId, fileName, "file", length, clock.GetUtcNow().AddHours(1));
                _assetUploads.TryRemove(id, out _);
                _assets[id] = new(summary, target, hash);
                return summary;
            }, token);
        }
        catch
        {
            TryRemoveArtifact(root, directory, id);
            throw;
        }
        finally { _assetUploads.TryRemove(id, out _); }
    }

    public Task<McpApprovedAsset> ApproveFolderAsync(Guid profileId, string path, CancellationToken token) => mutations.RunAsync(async () =>
    {
        await ValidateSelectionAsync(profileId, token);
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Select an existing local folder, without a symbolic link.");
        var summary = new McpApprovedAsset(Guid.NewGuid(), profileId, new DirectoryInfo(fullPath).Name, "folder", 0, clock.GetUtcNow().AddHours(1));
        _assets[summary.Id] = new(summary, fullPath, "");
        return summary;
    }, token);

    private async Task ValidateSelectionAsync(Guid profileId, CancellationToken token)
    {
        if (!Enabled || !options.ProfileIds.Contains(profileId)) throw new UnauthorizedAccessException("Select a permitted profile.");
        if (Token.Length == 0) throw new InvalidOperationException("The Web write connection is starting. Try the selection again shortly.");
        var now = clock.GetUtcNow();
        if (_assetUploads.Count + _assets.Values.Count(asset => asset.Summary.ExpiresAt > now) >= options.MaxPendingPlans)
            throw new InvalidOperationException("Too many approved assets. Use existing selections or wait for them to expire.");
        await RequireExistingProfileAsync(profileId, token);
    }

    private async Task RequireExistingProfileAsync(Guid profileId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await db.Owners.AnyAsync(profile => profile.Id == profileId, token))
            throw new ArgumentException("The selected profile no longer exists.");
    }

    private Asset RequireAsset(Guid id, Guid profileId, string kind)
    {
        if (!_assets.TryGetValue(id, out var asset) || asset.Summary.ProfileId != profileId || asset.Summary.Kind != kind || asset.Summary.ExpiresAt <= clock.GetUtcNow())
            throw new ArgumentException("Select or approve the file or folder in the Web app again.");
        if (kind == "folder" && (!Directory.Exists(asset.Path) || (File.GetAttributes(asset.Path) & FileAttributes.ReparsePoint) != 0))
            throw new ArgumentException("The approved folder changed. Select it again in the Web app.");
        return asset;
    }

    internal async Task RunArtifactCleanupAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(CleanupInterval, clock);
        while (await timer.WaitForNextTickAsync(token))
            await CleanupArtifactsAsync(token);
    }

    internal async Task CleanupArtifactsAsync(CancellationToken token, bool discardDownloads = false, bool clearSelections = false)
    {
        try
        {
            await mutations.RunAsync(async () =>
            {
                var now = clock.GetUtcNow();
                Prune();
                foreach (var item in _assets)
                    if (clearSelections || item.Value.Summary.ExpiresAt <= now) _assets.TryRemove(item.Key, out _);
                await using var db = await factory.CreateDbContextAsync(token);
                await CleanupUnclaimedAssetsAsync(db, token);
                await CleanupDownloadsAsync(db, now, discardDownloads, deletedProfileId: null, token);
                return true;
            }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { CleanupSweepPending(logger, exception.GetType().Name); }
    }

    async Task<bool> IProfileDeletionCleanup.TryCleanupAsync(Guid ownerId, CancellationToken token)
    {
        try
        {
            var root = AssetRoot;
            var removed = true;
            foreach (var item in _assetUploads)
                if (item.Value == ownerId && _assetUploads.TryRemove(item.Key, out _))
                    removed &= TryRemoveArtifact(root, Path.Combine(root, item.Key.ToString("N")), item.Key);
            foreach (var item in _assets)
            {
                if (item.Value.Summary.ProfileId != ownerId || !_assets.TryRemove(item.Key, out var asset)) continue;
                if (asset.Summary.Kind == "file")
                    removed &= !IsPreparingAsset(item.Key) && TryRemoveArtifact(root, Path.GetDirectoryName(asset.Path)!, item.Key);
            }
            await using var db = await factory.CreateDbContextAsync(token);
            var downloadsRemoved = await CleanupDownloadsAsync(db, clock.GetUtcNow(), discard: true, ownerId, token);
            return removed && downloadsRemoved;
        }
        catch (Exception exception)
        {
            CleanupSweepPending(logger, exception.GetType().Name);
            return false;
        }
    }

    private async Task CleanupUnclaimedAssetsAsync(ExplorerDbContext db, CancellationToken token)
    {
        var root = AssetRoot;
        var unclaimed = ArtifactDirectories(root)
            .Where(item => !_assets.ContainsKey(item.Id) && !_assetUploads.ContainsKey(item.Id) && !IsPreparingAsset(item.Id))
            .ToArray();
        if (unclaimed.Length == 0) return;

        var references = await db.ImportBatches.AsNoTracking()
            .Where(batch => batch.Status == ImportStatus.Queued || batch.Status == ImportStatus.Running ||
                            batch.Status == ImportStatus.Interrupted || batch.Status == ImportStatus.ReviewRequired)
            .Select(batch => batch.StagedPath).ToListAsync(token);
        var operations = await db.FileOperations.AsNoTracking()
            .Where(operation => operation.State == FileOperationState.Pending || operation.State == FileOperationState.Prepared ||
                operation.State == FileOperationState.DatabaseCommitted)
            .Select(operation => new { operation.SourceRelativePath, operation.TargetRelativePath }).ToListAsync(token);
        references.AddRange(operations.SelectMany(operation => new[] { operation.SourceRelativePath, operation.TargetRelativePath }).OfType<string>());
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var retained = references.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetRelativePath(root, Path.GetFullPath(path, paths.Root))
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], 2)[0])
            .ToHashSet(comparer);
        foreach (var item in unclaimed)
            if (!retained.Contains(Path.GetFileName(item.Directory))) TryRemoveArtifact(root, item.Directory, item.Id);
    }

    private async Task<bool> CleanupDownloadsAsync(ExplorerDbContext db, DateTimeOffset now, bool discard, Guid? deletedProfileId, CancellationToken token)
    {
        var root = DownloadRoot;
        var directories = ArtifactDirectories(root).ToArray();
        if (directories.Length == 0) return true;
        var ids = directories.Select(item => item.Id).ToArray();
        var receipts = await db.McpOperations.AsNoTracking().Where(item => ids.Contains(item.Id) &&
            (item.Action == "export_profile" || item.Action == "export_route" || item.Action == "download_original"))
            .ToDictionaryAsync(item => item.Id, token);
        HashSet<Guid> owners = discard ? [] : (await db.Owners.Select(owner => owner.Id).ToListAsync(token)).ToHashSet();
        var removed = true;
        foreach (var item in directories)
        {
            receipts.TryGetValue(item.Id, out var receipt);
            if (deletedProfileId.HasValue &&
                (receipt is null || !JsonSerializer.Deserialize<Guid[]>(receipt.ProfileIdsJson)!.Contains(deletedProfileId.Value))) continue;
            if (!discard && receipt is not null && ReadDownloadResult(receipt) is { } result &&
                result.ExpiresAt > now && owners.Contains(result.ProfileId)) continue;
            removed &= TryRemoveArtifact(root, item.Directory, item.Id, legacyDownload: receipt is not null);
        }
        return removed;
    }

    private static IEnumerable<(Guid Id, string Directory)> ArtifactDirectories(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (Guid.TryParseExact(name, "N", out var id) && name == id.ToString("N"))
                yield return (id, directory);
        }
    }

    private bool TryRemoveArtifact(string root, string directory, Guid id, bool legacyDownload = false)
    {
        try
        {
            if (!ManagedStagingDirectory.DeleteOwned(root, directory) && legacyDownload && Directory.Exists(directory))
            {
                // Pre-marker releases used exactly one payload path per download receipt.
                File.Delete(ManagedPathGuard.ResolveUnder(root, Path.Combine(directory, "content")));
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ArtifactCleanupPending(logger, id, exception.GetType().Name);
            return false;
        }
    }

    private static bool IsDownloadAction(string action) => action is "export_profile" or "export_route" or "download_original";

    private sealed record DownloadResult(string DownloadUrl, Guid ProfileId, string FileName, string ContentType, DateTimeOffset ExpiresAt, string Message);
    internal sealed record OpenDownload(FileStream Contents, string ContentType, string FileName);

    private static DownloadResult? ReadDownloadResult(McpOperation operation)
    {
        if (operation.Status != McpExecutionState.Completed || operation.ResultJson is null) return null;
        try
        {
            var result = JsonSerializer.Deserialize<DownloadResult>(operation.ResultJson, McpChangeJson.Options);
            return result is not null && result.ProfileId != Guid.Empty &&
                !string.IsNullOrWhiteSpace(result.ContentType) && !string.IsNullOrWhiteSpace(result.FileName) ? result : null;
        }
        catch (JsonException) { return null; }
    }

    internal async Task<OpenDownload?> OpenDownloadAsync(Guid id, CancellationToken token)
    {
        if (!Enabled) return null;
        await using var db = await factory.CreateDbContextAsync(token);
        var receipt = await db.McpOperations.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
        if (receipt is null || !IsDownloadAction(receipt.Action) || ReadDownloadResult(receipt) is not { } result ||
            result.ExpiresAt <= clock.GetUtcNow()) return null;
        AuthorizeReceipt(receipt);
        FileStream? stream = null;
        try
        {
            var root = DownloadRoot;
            var path = ManagedPathGuard.ResolveUnder(root, Path.Combine(root, id.ToString("N"), "content"));
            // Unlinking an expired download can proceed while its current response finishes reading.
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous);
            // Opening the handle first closes the race with removal without waiting behind unrelated imports.
            if (!await db.Owners.AnyAsync(profile => profile.Id == result.ProfileId, token) || result.ExpiresAt <= clock.GetUtcNow()) return null;
            var download = new OpenDownload(stream, result.ContentType, result.FileName);
            stream = null;
            return download;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or InvalidDataException)
        {
            return null;
        }
        finally
        {
            if (stream is not null) await stream.DisposeAsync();
        }
    }

    private void CleanupUnpublishedDownload(Guid id)
    {
        try
        {
            var root = DownloadRoot;
            TryRemoveArtifact(root, Path.Combine(root, id.ToString("N")), id);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ArtifactCleanupPending(logger, id, exception.GetType().Name);
        }
    }

    [LoggerMessage(4101, LogLevel.Warning, "Temporary MCP artifact {ArtifactId} could not be removed ({ErrorType}). Cleanup will retry.")]
    private static partial void ArtifactCleanupPending(ILogger logger, Guid artifactId, string errorType);

    [LoggerMessage(4102, LogLevel.Warning, "Temporary MCP artifact cleanup could not finish ({ErrorType}). Cleanup will retry.")]
    private static partial void CleanupSweepPending(ILogger logger, string errorType);
}
