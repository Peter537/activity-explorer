using System.Globalization;
using System.Security;
using Microsoft.Extensions.Logging;

namespace ActivityExplorer.Web.Services;

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private readonly string _directory;
    private readonly string _dataRoot;
    private readonly object _gate = new();
    private bool _disposed;

    public RollingFileLoggerProvider(string directory)
    {
        _directory = Path.GetFullPath(directory);
        _dataRoot = Directory.GetParent(_directory)?.FullName ?? _directory;
        TryFileOperation(() => Directory.CreateDirectory(_directory));
    }

    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(this, categoryName);
    public void Dispose() => _disposed = true;

    private void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        if (_disposed || level == LogLevel.None) return;
        var combined = exception is null || message.Contains(exception.ToString(), StringComparison.Ordinal)
            ? message
            : $"{message} | {exception}";
        if (string.IsNullOrWhiteSpace(combined)) return;
        var safeCategory = Normalize(category);
        var safeMessage = Redact(Normalize(combined));
        if (safeMessage.Length > 64_000) safeMessage = safeMessage[..64_000] + " [truncated]";
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:O} {level,-11} {safeCategory} [{eventId.Id}] {safeMessage}{Environment.NewLine}");
        lock (_gate)
        {
            TryFileOperation(() =>
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(CurrentPath(), line);
                Cleanup();
            });
        }
    }

    private string CurrentPath()
    {
        var date = DateTimeOffset.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        for (var index = 0; index < 100; index++)
        {
            var path = Path.Combine(_directory,
                string.Create(CultureInfo.InvariantCulture, $"activity-explorer-{date}-{index:00}.log"));
            if (!File.Exists(path) || new FileInfo(path).Length < MaxBytes) return path;
        }
        return Path.Combine(_directory, $"activity-explorer-{date}-overflow.log");
    }

    private void Cleanup()
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "activity-explorer-*.log")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(7))
        {
            TryFileOperation(() => File.Delete(file));
        }
    }

    private string Redact(string value)
    {
        var result = value.Replace(_dataRoot, "<data>", PathComparison);
        var userPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userPath)) result = result.Replace(userPath, "<user>", PathComparison);
        return result;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Normalize(string value) => value
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\u2028", "\\u2028", StringComparison.Ordinal)
        .Replace("\u2029", "\\u2029", StringComparison.Ordinal);

    private static void TryFileOperation(Action operation)
    {
        try { operation(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
        catch (NotSupportedException) { }
    }

    private sealed class RollingFileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write(category, logLevel, eventId, formatter(state, exception), exception);
        }
    }
}
