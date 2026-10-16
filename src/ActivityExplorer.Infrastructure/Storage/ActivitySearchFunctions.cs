using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace ActivityExplorer.Infrastructure.Storage;

internal static class ActivitySearchFunctions
{
    public static bool Contains(string? value, string term) =>
        value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;

    public static bool EqualsName(string? value, string term) =>
        string.Equals(value?.Trim(), term.Trim(), StringComparison.OrdinalIgnoreCase);

    public static void Register(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite) return;
        sqlite.CreateFunction<string?, string, bool>("activity_contains", ActivitySearchFunctions.Contains, isDeterministic: true);
        sqlite.CreateFunction<string?, string, bool>("activity_equals_name", ActivitySearchFunctions.EqualsName, isDeterministic: true);
    }
}
