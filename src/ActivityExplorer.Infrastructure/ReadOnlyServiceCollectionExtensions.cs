using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ActivityExplorer.Infrastructure;

public static class ReadOnlyServiceCollectionExtensions
{
    public static IServiceCollection AddActivityExplorerReadOnly(this IServiceCollection services, string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 5
        }.ToString();
        services.AddPooledDbContextFactory<ExplorerDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IProfileReadService, ProfileReadService>();
        services.AddSingleton<IActivityReadService, ActivityReadService>();
        services.AddSingleton<IStatisticsReadService, StatisticsReadService>();
        services.AddSingleton<ISegmentReadService, SegmentReadService>();
        services.AddSingleton<IRouteReadService, RouteReadService>();
        services.AddSingleton<ReadOnlyDatabaseValidator>();
        return services;
    }
}
