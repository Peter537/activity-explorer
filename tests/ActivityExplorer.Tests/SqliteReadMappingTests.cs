using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace ActivityExplorer.Tests;

public sealed class SqliteReadMappingTests
{
    private const long RequestedLimit = 1024L * 1024 * 1024;

    [Fact]
    public async Task Mapped_WAL_reader_keeps_its_snapshot_and_restores_the_limit_before_pool_reuse()
    {
        var connectionString = FileConnectionString();
        await using var reader = new SqliteConnection(connectionString);
        try
        {
            await reader.OpenAsync();
            await CreateDatabaseAsync(reader);
            var defaults = await LimitAsync(reader);
            var (before, available) = await PrepareLimitsAsync(reader);
            await using (var mapping = await SqliteReadMapping.BeginAsync(reader, default))
            {
                await AssertMappedAsync(reader, mapping, before, available);
                await using var snapshot = reader.BeginTransaction(deferred: true);
                Assert.Equal(1L, await ReadValueAsync(reader, snapshot));

                await using var writer = new SqliteConnection(connectionString);
                await writer.OpenAsync();
                await using var update = writer.CreateCommand();
                update.CommandText = "UPDATE Sample SET Value = 2";
                Assert.Equal(1, await update.ExecuteNonQueryAsync());

                Assert.Equal(1L, await ReadValueAsync(reader, snapshot));
                await snapshot.CommitAsync();
            }

            Assert.Equal(before, await LimitAsync(reader));
            Assert.Equal(2L, await ReadValueAsync(reader));
            await reader.CloseAsync();
            await using var pooled = new SqliteConnection(connectionString);
            await pooled.OpenAsync();
            var pooledLimit = await LimitAsync(pooled);
            Assert.True(pooledLimit == before || pooledLimit == defaults,
                $"The pooled connection has a mapping limit of {pooledLimit} bytes after scope cleanup.");
            Assert.Equal(2L, await ReadValueAsync(pooled));
        }
        finally
        {
            SqliteConnection.ClearPool(reader);
        }
    }

    [Fact]
    public async Task Canceled_read_body_restores_the_mapping_and_leaves_the_connection_usable()
    {
        await using var connection = new SqliteConnection(FileConnectionString());
        try
        {
            await connection.OpenAsync();
            await CreateDatabaseAsync(connection);
            var (before, available) = await PrepareLimitsAsync(connection);
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await using var mapping = await SqliteReadMapping.BeginAsync(connection, cancellation.Token);
                await AssertMappedAsync(connection, mapping, before, available);
                await using var snapshot = connection.BeginTransaction(deferred: true);
                Assert.Equal(1L, await ReadValueAsync(connection, snapshot));
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            });

            Assert.Equal(before, await LimitAsync(connection));
            Assert.Equal(1L, await ReadValueAsync(connection));
        }
        finally
        {
            SqliteConnection.ClearPool(connection);
        }
    }

    [Fact]
    public async Task In_memory_connection_uses_no_mapping_scope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        await connection.OpenAsync();
        var before = await LimitAsync(connection);
        await using var mapping = await SqliteReadMapping.BeginAsync(connection, default);
        Assert.Null(mapping);
        Assert.Equal(before, await LimitAsync(connection));
    }

    private static string FileConnectionString() => new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(TestSupport.NewDirectory(), "mapping.db"),
        Cache = SqliteCacheMode.Private,
        Pooling = true,
        DefaultTimeout = 3
    }.ToString();

    private static async Task CreateDatabaseAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL";
        Assert.Equal("wal", await command.ExecuteScalarAsync());
        command.CommandText = "CREATE TABLE Sample (Value INTEGER NOT NULL); INSERT INTO Sample VALUES (1);";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(long? Before, long? Available)> PrepareLimitsAsync(SqliteConnection connection)
    {
        // Probe the bundled native VFS directly so an unsupported build still tests cleanup and snapshot semantics.
        var available = await LimitAsync(connection, RequestedLimit);
        var before = await LimitAsync(connection, 4096);
        return (before, available);
    }

    private static async Task AssertMappedAsync(SqliteConnection connection, IAsyncDisposable? mapping, long? before, long? available)
    {
        if (available > before)
        {
            Assert.NotNull(mapping);
            Assert.Equal(available, await LimitAsync(connection));
        }
        else
        {
            Assert.Null(mapping);
            Assert.Equal(before, await LimitAsync(connection));
        }
    }

    private static async Task<long?> LimitAsync(SqliteConnection connection, long? requested = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = requested switch
        {
            RequestedLimit => "PRAGMA main.mmap_size = 1073741824",
            4096 => "PRAGMA main.mmap_size = 4096",
            _ => "PRAGMA main.mmap_size"
        };
        return await command.ExecuteScalarAsync() is long value ? value : null;
    }

    private static async Task<long> ReadValueAsync(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Value FROM Sample";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
