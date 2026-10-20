using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ActivityExplorer.Infrastructure.Storage;

internal sealed class ReadSnapshot(SqliteTransaction transaction, IDbContextTransaction enlisted) : IAsyncDisposable
{
    public static async Task<ReadSnapshot> BeginAsync(ExplorerDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
        try
        {
            var enlisted = await db.Database.UseTransactionAsync(transaction, cancellationToken);
            return new(transaction, enlisted!);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await enlisted.DisposeAsync();
        await transaction.DisposeAsync();
    }
}
