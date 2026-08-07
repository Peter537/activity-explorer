using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class MapSettingsService(IDbContextFactory<ExplorerDbContext> contextFactory) : IMapSettingsService
{
    private const string Key = "maps.mode";
    private int _cachedMode = -1;

    public async Task<MapPrivacyMode> GetModeAsync(CancellationToken cancellationToken = default)
    {
        var cached = Volatile.Read(ref _cachedMode);
        if (cached >= 0) return (MapPrivacyMode)cached;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var value = await db.ApplicationSettings.AsNoTracking()
            .Where(x => x.Key == Key)
            .Select(x => x.Value)
            .SingleOrDefaultAsync(cancellationToken);
        var mode = Enum.TryParse<MapPrivacyMode>(value, true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : MapPrivacyMode.Blank;
        Volatile.Write(ref _cachedMode, (int)mode);
        return mode;
    }

    public async Task SetModeAsync(MapPrivacyMode mode, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.ApplicationSettings.SingleOrDefaultAsync(x => x.Key == Key, cancellationToken);
        if (setting is null)
        {
            setting = new ApplicationSetting { Key = Key };
            db.ApplicationSettings.Add(setting);
        }
        setting.Value = mode.ToString();
        setting.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        Volatile.Write(ref _cachedMode, (int)mode);
    }
}
