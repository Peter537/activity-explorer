using System.IO.Compression;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;

namespace ActivityExplorer.Infrastructure.Import;

public sealed class ArchiveActivityImporter(
    AppDataPaths paths,
    FitActivityImporter fit,
    XmlActivityImporter xml) : IActivityImporter, IImporterDiagnosticsSource
{
    private const int MaxEntries = 50_000;
    private const long MaxEntryBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxExpandedBytes = 20L * 1024 * 1024 * 1024;
    private const int MaxDepth = 3;
    private ImporterDiagnostics _lastDiagnostics = new(0, 0, null);

    public string Name => "Garmin/Strava archive";

    public bool CanImport(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gz", StringComparison.OrdinalIgnoreCase);
    }


    public ImporterDiagnostics ConsumeDiagnostics()
    {
        var result = _lastDiagnostics;
        _lastDiagnostics = new ImporterDiagnostics(0, 0, null);
        return result;
    }
    public async Task<IReadOnlyList<ImportCandidate>> ReadAsync(
        string path,
        SourceKind sourceKind,
        CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var extractionRoot = Path.Combine(Path.GetDirectoryName(path) ?? paths.StagingPath, $"expanded-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractionRoot);

        var state = new ExtractionState();
        await ExpandAsync(path, extractionRoot, Path.GetFileName(path), 0, state, cancellationToken);

        var metadata = sourceKind == SourceKind.StravaArchive
            ? ReadStravaMetadata(state.Files)
            : StravaMetadata.Empty;

        var result = new List<ImportCandidate>();
        var supportedFiles = state.Files
            .Where(candidate => fit.CanImport(candidate.PhysicalPath) || xml.CanImport(candidate.PhysicalPath))
            .ToArray();
        var hasGarminLayout = sourceKind == SourceKind.GarminArchive &&
            supportedFiles.Any(candidate => IsGarminUploadedFile(candidate.LogicalPath));
        var hasStravaLayout = sourceKind == SourceKind.StravaArchive && metadata.HasActivitiesCsv;
        var activityFiles = sourceKind switch
        {
            SourceKind.GarminArchive when hasGarminLayout =>
                supportedFiles.Where(candidate => IsGarminUploadedFile(candidate.LogicalPath)).ToArray(),
            SourceKind.StravaArchive when hasStravaLayout =>
                supportedFiles.Where(metadata.IsActivityFile).ToArray(),
            _ => supportedFiles
        };
        var unsupported = hasGarminLayout
            ? state.Files.Count(file => IsGarminUploadedFile(file.LogicalPath) &&
                !fit.CanImport(file.PhysicalPath) && !xml.CanImport(file.PhysicalPath))
            : hasStravaLayout
                ? state.Files.Count(file => metadata.IsActivityFile(file) &&
                    !fit.CanImport(file.PhysicalPath) && !xml.CanImport(file.PhysicalPath))
                : 0;
        var corrupt = state.CorruptEntries;
        foreach (var file in activityFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var importer = fit.CanImport(file.PhysicalPath) ? (IActivityImporter)fit : xml;
            try
            {
                var candidates = await importer.ReadAsync(file.PhysicalPath, sourceKind, cancellationToken);
                foreach (var candidate in candidates)
                {
                    var logicalName = ArchiveFileName(file.LogicalPath);
                    var logicalCandidate = candidate with { OriginalName = logicalName };
                    var meta = metadata.Find(file);
                    var enriched = meta is null ? logicalCandidate : Enrich(logicalCandidate, meta);
                    var provider = sourceKind switch
                    {
                        SourceKind.GarminArchive => SourceProvider.Garmin,
                        SourceKind.StravaArchive => SourceProvider.Strava,
                        _ => enriched.Provider
                    };
                    result.Add(enriched with { Provider = provider, AcquisitionMethod = AcquisitionMethod.AccountExport });
                }
            }
            catch (UnsupportedActivityException)
            {
                unsupported++;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or System.Xml.XmlException)
            {
                corrupt++;
            }
        }

        if (result.Count == 0 && unsupported == 0 && corrupt == 0) unsupported = 1;
        var messages = new List<string>();
        if (unsupported > 0) messages.Add($"Skipped {unsupported} unsupported or unrecognized archive entr{(unsupported == 1 ? "y" : "ies")}.");
        if (corrupt > 0) messages.Add($"Skipped {corrupt} corrupt activity file{(corrupt == 1 ? string.Empty : "s")}.");
        _lastDiagnostics = new ImporterDiagnostics(unsupported, corrupt, messages.Count == 0 ? null : string.Join(" ", messages));
        return result;
    }

    private static async Task ExpandAsync(
        string archivePath,
        string destination,
        string logicalArchivePath,
        int depth,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        if (depth > MaxDepth)
        {
            throw new UnsafeArchiveException($"Archive nesting exceeds the supported depth of {MaxDepth}.");
        }

        var extension = Path.GetExtension(archivePath);
        if (extension.Equals(".gz", StringComparison.OrdinalIgnoreCase))
        {
            var outputName = Path.GetFileNameWithoutExtension(archivePath);
            var output = SafePath(destination, outputName);
            Directory.CreateDirectory(destination);
            await using (var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
            await using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            await using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                await CopyLimitedAsync(gzip, target, state, cancellationToken);
            }

            var logicalOutputPath = RemoveGzipSuffix(NormalizeArchivePath(logicalArchivePath));
            if (Path.GetExtension(output).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await ExpandAsync(
                    output,
                    Path.Combine(Path.GetDirectoryName(output)!, $"nested-{Guid.NewGuid():N}"),
                    logicalOutputPath,
                    depth + 1,
                    state,
                    cancellationToken);
            }
            else
            {
                state.Files.Add(new ExtractedFile(
                    output,
                    logicalOutputPath,
                    NormalizeArchivePath(logicalArchivePath)));
            }

            return;
        }

        var logicalDirectory = ArchiveDirectoryName(NormalizeArchivePath(logicalArchivePath));
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.EntryCount++;
            if (state.EntryCount > MaxEntries)
            {
                throw new UnsafeArchiveException($"Archive contains more than {MaxEntries:N0} entries.");
            }

            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            {
                throw new UnsafeArchiveException("Archive contains a symbolic link.");
            }

            if (entry.Length > MaxEntryBytes)
            {
                throw new UnsafeArchiveException("Archive contains an oversized entry.");
            }

            var normalizedEntryPath = NormalizeArchivePath(entry.FullName);
            var logicalEntryPath = CombineArchivePath(logicalDirectory, normalizedEntryPath);
            var output = SafePath(destination, entry.FullName);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(output);
                continue;
            }

            var fileCheckpoint = state.Files.Count;
            string? nestedDestination = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await using (var input = entry.Open())
                await using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                {
                    await CopyLimitedAsync(input, target, state, cancellationToken);
                }

                var nestedExtension = Path.GetExtension(output);
                if (nestedExtension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                    || nestedExtension.Equals(".gz", StringComparison.OrdinalIgnoreCase))
                {
                    nestedDestination = Path.Combine(Path.GetDirectoryName(output)!, $"nested-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(nestedDestination);
                    await ExpandAsync(
                        output,
                        nestedDestination,
                        logicalEntryPath,
                        depth + 1,
                        state,
                        cancellationToken);
                }
                else
                {
                    state.Files.Add(new ExtractedFile(output, logicalEntryPath, logicalEntryPath));
                }
            }
            catch (InvalidDataException)
            {
                state.CorruptEntries++;
                if (state.Files.Count > fileCheckpoint)
                {
                    state.Files.RemoveRange(fileCheckpoint, state.Files.Count - fileCheckpoint);
                }

                TryDeleteDirectory(nestedDestination);
                TryDeleteFile(output);
            }
        }
    }

    private static async Task CopyLimitedAsync(Stream source, Stream destination, ExtractionState state, CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            state.ExpandedBytes += read;
            if (state.ExpandedBytes > MaxExpandedBytes)
            {
                throw new UnsafeArchiveException("Archive expands beyond the configured safety limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static string SafePath(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsafeArchiveException("Archive entry escapes the staging directory.");
        }

        return candidate;
    }

    private static StravaMetadata ReadStravaMetadata(IReadOnlyList<ExtractedFile> files)
    {
        var csv = files
            .Where(file => ArchiveFileName(file.LogicalPath).Equals("activities.csv", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.LogicalPath.Equals("activities.csv", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
        if (csv is null)
        {
            return StravaMetadata.Empty;
        }

        var result = new StravaMetadata(ArchiveDirectoryName(csv.LogicalPath));
        foreach (var row in CsvTable.Read(csv.PhysicalPath))
        {
            var filename = Value(row, "Filename");
            if (!string.IsNullOrWhiteSpace(filename))
            {
                result.Add(filename, row);
            }
        }

        return result;
    }

    private static ImportCandidate Enrich(ImportCandidate candidate, IReadOnlyDictionary<string, string> metadata)
    {
        var source = candidate.Parsed;
        var sport = ParseSport(Value(metadata, "Activity Type")) ?? source.Sport;
        var isIndoor = ParseIndoor(Value(metadata, "Activity Type")) ?? source.IsIndoor;
        var title = Value(metadata, "Activity Name");
        var description = Value(metadata, "Activity Description");
        var externalId = Value(metadata, "Activity ID");

        var parsed = new ParsedActivity
        {
            Sport = sport,
            IsIndoor = isIndoor,
            Title = string.IsNullOrWhiteSpace(title) ? source.Title : title,
            Description = string.IsNullOrWhiteSpace(description) ? source.Description : description,
            DeviceName = source.DeviceName,
            GearName = Value(metadata, "Activity Gear") ?? source.GearName,
            ExternalId = string.IsNullOrWhiteSpace(externalId) ? source.ExternalId : externalId,
            StartTimeUtc = source.StartTimeUtc,
            OriginalUtcOffset = source.OriginalUtcOffset,
            DistanceMeters = source.DistanceMeters,
            MovingTimeSeconds = source.MovingTimeSeconds,
            TimerTimeSeconds = source.TimerTimeSeconds,
            MovingTimeSource = source.MovingTimeSource,
            ElapsedTimeSeconds = source.ElapsedTimeSeconds,
            ElevationGainMeters = source.ElevationGainMeters,
            ElevationLossMeters = source.ElevationLossMeters,
            MinElevationMeters = source.MinElevationMeters,
            MaxElevationMeters = source.MaxElevationMeters,
            Calories = source.Calories,
            RestingCalories = source.RestingCalories,
            ActiveCalories = source.ActiveCalories,
            AverageSpeedMetersPerSecond = source.AverageSpeedMetersPerSecond,
            MaxSpeedMetersPerSecond = source.MaxSpeedMetersPerSecond,
            AverageHeartRate = source.AverageHeartRate,
            MaxHeartRate = source.MaxHeartRate,
            AverageCadence = source.AverageCadence,
            MaxCadence = source.MaxCadence,
            PedalRevolutions = source.PedalRevolutions,
            AveragePowerWatts = source.AveragePowerWatts,
            MaxPowerWatts = source.MaxPowerWatts,
            NormalizedPowerWatts = source.NormalizedPowerWatts,
            Kilojoules = source.Kilojoules,
            AverageTemperatureCelsius = source.AverageTemperatureCelsius,
            MinTemperatureCelsius = source.MinTemperatureCelsius,
            MaxTemperatureCelsius = source.MaxTemperatureCelsius,
            AverageRespirationRate = source.AverageRespirationRate,
            MinRespirationRate = source.MinRespirationRate,
            MaxRespirationRate = source.MaxRespirationRate,
            AerobicTrainingEffect = source.AerobicTrainingEffect,
            AnaerobicTrainingEffect = source.AnaerobicTrainingEffect,
            TrainingLoad = source.TrainingLoad,
            Metrics = source.Metrics,

            Points = source.Points,
            Laps = source.Laps
        };

        return candidate with { Parsed = parsed, ExternalId = parsed.ExternalId };
    }

    private static bool IsGarminUploadedFile(string path)
    {
        var normalized = path.Replace('\\', '/').Replace('_', '-').Replace(' ', '-').ToLowerInvariant();
        return normalized.Contains("connect-fitness-uploaded-files", StringComparison.Ordinal) ||
               normalized.Contains("connect-uploaded-files", StringComparison.Ordinal);
    }

    private static SportKind? ParseSport(string? value)
    {
        var normalized = value?.ToLowerInvariant();
        if (normalized?.Contains("ride") == true || normalized?.Contains("cycl") == true) return SportKind.Cycling;
        if (normalized?.Contains("run") == true) return SportKind.Running;
        if (normalized?.Contains("walk") == true || normalized?.Contains("hik") == true) return SportKind.Walking;
        return null;
    }

    private static bool? ParseIndoor(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        if (normalized.Contains("indoor", StringComparison.Ordinal) ||
            normalized.Contains("virtual", StringComparison.Ordinal) ||
            normalized.Contains("treadmill", StringComparison.Ordinal) ||
            normalized.Contains("trainer", StringComparison.Ordinal) ||
            normalized.Contains("spin", StringComparison.Ordinal)) return true;
        return normalized.Contains("outdoor", StringComparison.Ordinal) ? false : null;
    }

    private static string? Value(IReadOnlyDictionary<string, string> row, string key) =>
        row.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string NormalizeArchivePath(string path) =>
        string.Join('/', path.Trim().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != "."));

    private static string RemoveGzipSuffix(string path) =>
        path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;

    private static string ArchiveFileName(string path)
    {
        var normalized = NormalizeArchivePath(path);
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? normalized : normalized[(separator + 1)..];
    }

    private static string ArchiveDirectoryName(string path)
    {
        var normalized = NormalizeArchivePath(path);
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? string.Empty : normalized[..separator];
    }

    private static string CombineArchivePath(string left, string right) =>
        string.IsNullOrEmpty(left)
            ? NormalizeArchivePath(right)
            : $"{NormalizeArchivePath(left)}/{NormalizeArchivePath(right)}";

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class ExtractionState
    {
        public int EntryCount { get; set; }
        public long ExpandedBytes { get; set; }
        public int CorruptEntries { get; set; }
        public List<ExtractedFile> Files { get; } = [];
    }

    private sealed record ExtractedFile(string PhysicalPath, string LogicalPath, string SourcePath);

    private sealed class StravaMetadata
    {
        private readonly Dictionary<string, List<IReadOnlyDictionary<string, string>>> _byPath =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<IReadOnlyDictionary<string, string>>> _byName =
            new(StringComparer.OrdinalIgnoreCase);

        public static StravaMetadata Empty { get; } = new(string.Empty, hasActivitiesCsv: false);

        public StravaMetadata(string rootPath) : this(rootPath, hasActivitiesCsv: true)
        {
        }

        private StravaMetadata(string rootPath, bool hasActivitiesCsv)
        {
            RootPath = rootPath;
            HasActivitiesCsv = hasActivitiesCsv;
        }

        public string RootPath { get; }
        public bool HasActivitiesCsv { get; }

        public void Add(string filename, IReadOnlyDictionary<string, string> row)
        {
            var normalized = NormalizeArchivePath(filename);
            AddAlias(_byPath, normalized, row);
            AddAlias(_byName, ArchiveFileName(normalized), row);
            if (normalized.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                var decompressed = RemoveGzipSuffix(normalized);
                AddAlias(_byPath, decompressed, row);
                AddAlias(_byName, ArchiveFileName(decompressed), row);
            }
        }

        public bool IsActivityFile(ExtractedFile file)
        {
            if (!HasActivitiesCsv) return false;
            var relative = RelativeToRoot(file.LogicalPath);
            return relative.StartsWith("activities/", StringComparison.OrdinalIgnoreCase);
        }

        public IReadOnlyDictionary<string, string>? Find(ExtractedFile file)
        {
            if (!HasActivitiesCsv) return null;
            foreach (var path in new[]
                     {
                         RelativeToRoot(file.SourcePath),
                         RelativeToRoot(file.LogicalPath)
                     }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (_byPath.TryGetValue(path, out var rows)) return rows.Count == 1 ? rows[0] : null;
            }

            foreach (var name in new[]
                     {
                         ArchiveFileName(file.SourcePath),
                         ArchiveFileName(file.LogicalPath)
                     }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (_byName.TryGetValue(name, out var rows)) return rows.Count == 1 ? rows[0] : null;
            }

            return null;
        }

        private string RelativeToRoot(string path)
        {
            var normalized = NormalizeArchivePath(path);
            if (string.IsNullOrEmpty(RootPath)) return normalized;
            var prefix = RootPath + "/";
            return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? normalized[prefix.Length..]
                : normalized;
        }

        private static void AddAlias(
            IDictionary<string, List<IReadOnlyDictionary<string, string>>> index,
            string alias,
            IReadOnlyDictionary<string, string> row)
        {
            if (!index.TryGetValue(alias, out var rows))
            {
                rows = [];
                index[alias] = rows;
            }

            rows.Add(row);
        }
    }
}
