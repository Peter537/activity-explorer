using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ActivityExplorer.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddActivityExplorer(this IServiceCollection services)
    {
        services.AddSingleton<AppDataPaths>();
        services.AddSingleton<IAppDataPaths>(provider => provider.GetRequiredService<AppDataPaths>());
        services.AddSingleton<IOriginalStore, OriginalStore>();
        services.AddSingleton<IOwnerMutationLock, OwnerMutationLock>();
        services.AddSingleton<IFileOperationCoordinator, FileOperationCoordinator>();
        services.AddPooledDbContextFactory<ExplorerDbContext>((provider, options) =>
        {
            var paths = provider.GetRequiredService<AppDataPaths>();
            paths.EnsureCreated();
            options.UseSqlite($"Data Source={paths.DatabasePath};Cache=Shared");
        });

        services.AddSingleton<FitActivityImporter>();
        services.AddSingleton<XmlActivityImporter>();
        services.AddSingleton<ArchiveActivityImporter>();
        services.AddSingleton<GpxRouteReader>();
        services.AddSingleton<IActivityImporter>(provider => provider.GetRequiredService<ArchiveActivityImporter>());
        services.AddSingleton<IActivityImporter>(provider => provider.GetRequiredService<FitActivityImporter>());
        services.AddSingleton<IActivityImporter>(provider => provider.GetRequiredService<XmlActivityImporter>());

        services.AddSingleton<ImportQueue>();
        services.AddSingleton<IImportQueue>(provider => provider.GetRequiredService<ImportQueue>());
        services.AddSingleton<IImportProcessor, ImportProcessor>();
        services.AddHostedService<ImportWorker>();
        services.AddHostedService<WatchedFolderWorker>();

        services.AddSingleton<IActivityQueryService, ActivityQueryService>();
        services.AddHostedService<WatchedFolderSignalWorker>();
        services.AddSingleton<ISegmentMatcher, SegmentMatcher>();
        services.AddSingleton<ISegmentService, SegmentService>();
        services.AddSingleton<IRouteService, RouteService>();
        services.AddSingleton<IStatisticsService, StatisticsService>();
        services.AddHostedService<StatisticsRepairWorker>();
        services.AddSingleton<IMapFeatureService, MapFeatureService>();
        services.AddSingleton<IMapSettingsService, MapSettingsService>();
        services.AddSingleton<IProfileService, ProfileService>();
        services.AddSingleton<IImportHistoryService, ImportHistoryService>();
        services.AddSingleton<IWatchedFolderService, WatchedFolderService>();
        services.AddSingleton<DatabaseInitializer>();
        return services;
    }
}
