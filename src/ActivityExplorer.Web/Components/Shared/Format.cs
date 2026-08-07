using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Web.Components.Shared;

public static class Format
{
    public static string Distance(double meters) => meters >= 1000 ? $"{meters / 1000:N1} km" : $"{meters:N0} m";
    public static string Duration(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1 ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : $"{value.Minutes}:{value.Seconds:00}";
    }
    public static string Speed(double? metersPerSecond, SportKind sport)
    {
        if (!metersPerSecond.HasValue || metersPerSecond <= 0) return "--";
        if (sport is SportKind.Running or SportKind.Walking)
        {
            var pace = TimeSpan.FromMinutes(1000 / metersPerSecond.Value / 60);
            return $"{(int)pace.TotalMinutes}:{pace.Seconds:00} /km";
        }
        return $"{metersPerSecond * 3.6:N1} km/h";
    }
}
