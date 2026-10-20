using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Storage;

public sealed class ReadOnlyDatabaseValidator(IDbContextFactory<ExplorerDbContext> factory)
{
    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        foreach (var table in db.Model.GetRelationalModel().Tables)
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info($table)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$table";
            parameter.Value = table.Name;
            command.Parameters.Add(parameter);
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(0));
            if (table.Columns.Any(column => !columns.Contains(column.Name)))
                throw new InvalidDataException("The database schema is incompatible. Open it with the matching Activity Explorer web application to initialize or upgrade it.");
        }
    }
}
