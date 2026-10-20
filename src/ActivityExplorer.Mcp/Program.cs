using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ActivityExplorer.Mcp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        IHost host;
        try { host = McpHost.Build(args); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
        {
            await Console.Error.WriteLineAsync("Invalid MCP configuration: " + exception.Message);
            return 2;
        }
        using (host)
        {
            try { await host.Services.GetRequiredService<ReadOnlyDatabaseValidator>().ValidateAsync(); }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
            {
                await Console.Error.WriteLineAsync("The Activity Explorer database is unavailable or incompatible. Set ACTIVITY_EXPLORER_DATA to an existing, initialized data directory. " + (exception is InvalidDataException ? exception.Message : "Check database access and compatibility."));
                return 3;
            }
            try { await host.RunAsync(); return 0; }
            catch (OperationCanceledException) { return 0; }
            catch (Exception)
            {
                await Console.Error.WriteLineAsync("The MCP server stopped unexpectedly.");
                return 1;
            }
        }
    }
}
