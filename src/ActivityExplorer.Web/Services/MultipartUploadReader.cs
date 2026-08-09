using ActivityExplorer.Infrastructure.Storage;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace ActivityExplorer.Web.Services;

public sealed record StagedUpload(string DirectoryPath, string FilePath, string FileName, long Length);

public sealed class UploadTooLargeException(long maximumBytes)
    : IOException($"The upload exceeds the configured {maximumBytes:N0}-byte limit.")
{
    public long MaximumBytes { get; } = maximumBytes;
}

public sealed class MultipartUploadReader(AppDataPaths paths, IConfiguration configuration)
{
    public long ImportLimit => configuration.GetValue("Imports:MaxUploadBytes", 10L * 1024 * 1024 * 1024);
    public long RouteLimit => configuration.GetValue("Routes:MaxGpxUploadBytes", 50L * 1024 * 1024);
    public long SegmentLimit => configuration.GetValue("Segments:MaxPathUploadBytes", 50L * 1024 * 1024);

    public async Task<StagedUpload> ReadSingleFileAsync(
        HttpRequest request,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        if (maximumBytes <= 0) throw new InvalidOperationException("The configured upload limit must be positive.");
        if (request.ContentLength.HasValue && request.ContentLength.Value > maximumBytes + 1024 * 1024)
            throw new UploadTooLargeException(maximumBytes);
        if (string.IsNullOrWhiteSpace(request.ContentType) ||
            !MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
            !string.Equals(contentType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The request must use multipart/form-data.");

        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 256)
            throw new InvalidDataException("The multipart boundary is missing or invalid.");

        var directory = ManagedPathGuard.ResolveUnder(paths.StagingPath, Path.Combine(paths.StagingPath, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            var reader = new MultipartReader(boundary, request.Body)
            {
                BodyLengthLimit = maximumBytes,
                HeadersCountLimit = 16,
                HeadersLengthLimit = 16 * 1024
            };
            StagedUpload? staged = null;
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                    !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase))
                    continue;

                var fieldName = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                var submittedName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value
                                    ?? HeaderUtilities.RemoveQuotes(disposition.FileName).Value;
                if (!string.Equals(fieldName, "file", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(submittedName))
                    continue;
                if (staged is not null) throw new InvalidDataException("Upload exactly one file per request.");

                var fileName = Path.GetFileName(submittedName);
                if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 260 ||
                    fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidDataException("The uploaded file name is invalid.");
                var filePath = ManagedPathGuard.ResolveUnder(directory, Path.Combine(directory, fileName));
                var length = 0L;
                await using (var output = new FileStream(
                                 filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = await section.Body.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        length += read;
                        if (length > maximumBytes) throw new UploadTooLargeException(maximumBytes);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    await output.FlushAsync(cancellationToken);
                }
                if (length == 0) throw new InvalidDataException("Choose a non-empty file.");
                staged = new StagedUpload(directory, filePath, fileName, length);
            }

            return staged ?? throw new InvalidDataException("Choose a file to upload.");
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase))
        {
            TryCleanup(directory);
            throw new UploadTooLargeException(maximumBytes);
        }
        catch
        {
            TryCleanup(directory);
            throw;
        }
    }

    public void Cleanup(StagedUpload staged) => TryCleanup(staged.DirectoryPath);

    private void TryCleanup(string directory)
    {
        try
        {
            var safe = ManagedPathGuard.ResolveUnder(paths.StagingPath, directory);
            if (Directory.Exists(safe)) Directory.Delete(safe, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Startup reconciliation will report any staging residue. Cleanup failures do not mask the request result.
        }
    }
}
