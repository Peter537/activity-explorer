using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ActivityExplorer.Infrastructure.Storage;

internal sealed class SqliteReadMapping(SqliteConnection connection, long previousBytes) : IAsyncDisposable
{
    internal const long LimitBytes = 1_073_741_824;

    // Use this outside the read transaction so the pager can release the mapping on exit.
    internal static async Task<SqliteReadMapping?> BeginAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA main.mmap_size";
        var previous = await command.ExecuteScalarAsync(cancellationToken);
        if (previous is not long previousBytes || previousBytes < 0 || previousBytes >= LimitBytes)
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        return await SetLimitAsync(connection, LimitBytes) is long effectiveBytes && effectiveBytes > previousBytes
            ? new(connection, previousBytes) : null;
    }

    public ValueTask DisposeAsync() => new(SetLimitAsync(connection, previousBytes));

    private static async Task<object?> SetLimitAsync(SqliteConnection connection, long bytes)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA main.mmap_size = {bytes}");
        return await command.ExecuteScalarAsync();
    }
}
