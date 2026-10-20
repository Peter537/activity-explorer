using System.Diagnostics;
using System.Text.Json;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Mcp.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace ActivityExplorer.Mcp;

public sealed partial class ToolExecutionPolicy(McpOptions options, IProfileReadService profiles, TimeProvider clock, ILogger<ToolExecutionPolicy> logger) : IDisposable
{
    private readonly SemaphoreSlim _calls = new(options.MaxConcurrentCalls);
    private readonly SemaphoreSlim _analyses = new(options.MaxConcurrentAnalyses);

    public async Task<CallToolResult> RunAsync<T>(string name, Guid? profileId,
        Func<DateTimeOffset, CancellationToken, Task<T?>> action, CancellationToken cancellationToken) where T : class
    {
        var asOfUtc = clock.GetUtcNow();
        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        var acquiredCall = false;
        var acquiredAnalysis = false;
        CallToolResult result;
        try
        {
            if (!ToolCatalog.Names.Contains(name)) return ToolResultFactory.Error("read_only", "This operation is not available in the read-only server.", asOfUtc);
            if (profileId.HasValue && (profileId == Guid.Empty || options.AllowedProfileIds is not null && !options.AllowedProfileIds.Contains(profileId.Value)))
                return ToolResultFactory.Error("not_found", "The requested profile or entity is unavailable.", asOfUtc);
            acquiredCall = await _calls.WaitAsync(TimeSpan.FromSeconds(options.CapacityWaitSeconds), timeout.Token);
            if (!acquiredCall) return ToolResultFactory.Error("database_busy", "The server is busy. Retry shortly.", asOfUtc, true);
            if (ToolCatalog.Analyses.Contains(name))
            {
                acquiredAnalysis = await _analyses.WaitAsync(TimeSpan.FromSeconds(options.CapacityWaitSeconds), timeout.Token);
                if (!acquiredAnalysis) return ToolResultFactory.Error("database_busy", "Another analysis is running. Retry shortly.", asOfUtc, true);
            }
            if (profileId.HasValue && !await profiles.ExistsAsync(profileId.Value, timeout.Token))
                return ToolResultFactory.Error("not_found", "The requested profile or entity is unavailable.", asOfUtc);
            var data = await action(asOfUtc, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            result = data is null ? ToolResultFactory.Error("not_found", "The requested profile or entity is unavailable.", asOfUtc)
                : ToolResultFactory.Create(data, null, asOfUtc);
            if (JsonSerializer.SerializeToUtf8Bytes(result).Length > options.MaxResponseBytes)
                result = ToolResultFactory.Error("response_too_large", "The response exceeds the size limit. Reduce the page size or narrow the request.", asOfUtc);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = ToolResultFactory.Error("timeout", "The request timed out. Narrow the request and try again.", asOfUtc, true);
        }
        catch (OperationCanceledException) { throw; }
        catch (ArgumentException exception) { result = ToolResultFactory.Error("invalid_argument", exception.Message, asOfUtc); }
        catch (SqliteException exception)
        {
            var (code, message, retryable) = exception.SqliteErrorCode switch
            {
                5 or 6 => ("database_busy", "The database is busy. Retry shortly.", true),
                8 => ("read_only", "The operation was rejected by the read-only database.", false),
                1 or 11 or 26 => ("database_incompatible", "The database cannot be read by this application version.", false),
                _ => ("database_unavailable", "The database is unavailable.", true)
            };
            result = ToolResultFactory.Error(code, message, asOfUtc, retryable);
        }
        catch (Exception exception)
        {
            Failure(logger, name, exception);
            result = ToolResultFactory.Error("internal_error", "The request could not be completed.", asOfUtc);
        }
        finally
        {
            if (acquiredAnalysis) _analyses.Release();
            if (acquiredCall) _calls.Release();
        }
        Completed(logger, name, Stopwatch.GetElapsedTime(started).TotalMilliseconds, result.IsError == true);
        return result;
    }

    public void Dispose() { _calls.Dispose(); _analyses.Dispose(); }

    [LoggerMessage(1001, LogLevel.Error, "MCP tool {ToolName} failed.")]
    private static partial void Failure(ILogger logger, string toolName, Exception exception);
    [LoggerMessage(1002, LogLevel.Information, "MCP tool {ToolName} completed in {ElapsedMilliseconds} ms; error={IsError}.")]
    private static partial void Completed(ILogger logger, string toolName, double elapsedMilliseconds, bool isError);
}
