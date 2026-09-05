using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Web.Components.Shared;

public static class Format
{
    public static IReadOnlyList<SportKind> Sports { get; } = Enum.GetValues<SportKind>();
    public static string SupportedSports => string.Join(", ", Sports);
    public static string SpeedLabel(SportKind sport) => sport == SportKind.Cycling ? "Speed" : "Pace";
    public static string SpeedUnit(SportKind sport) => sport switch
    {
        SportKind.Cycling => "km/h",
        SportKind.Rowing => "min/500 m",
        _ => "min/km"
    };
    public static string CadenceLabel(SportKind sport) => sport == SportKind.Rowing ? "Stroke rate" : "Cadence";
    public static string CadenceUnit(SportKind sport) => sport == SportKind.Rowing ? "spm" : "rpm";
    public static double? SpeedOrPace(double? speed, SportKind sport) => speed is > 0 && double.IsFinite(speed.Value)
        ? sport == SportKind.Cycling ? speed * 3.6 : (sport == SportKind.Rowing ? 500d : 1000d) / speed / 60d
        : null;
    public static string Distance(double meters) => meters >= 1000 ? $"{meters / 1000:N1} km" : $"{meters:N0} m";
    public static string Duration(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1 ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : $"{value.Minutes}:{value.Seconds:00}";
    }
    public static string Speed(double? metersPerSecond, SportKind sport)
    {
        if (metersPerSecond is not > 0 || !double.IsFinite(metersPerSecond.Value)) return "--";
        if (sport is SportKind.Running or SportKind.Walking or SportKind.Rowing)
        {
            var pace = TimeSpan.FromSeconds((sport == SportKind.Rowing ? 500 : 1000) / metersPerSecond.Value);
            return $"{(int)pace.TotalMinutes}:{pace.Seconds:00} {(sport == SportKind.Rowing ? "/500 m" : "/km")}";
        }
        return $"{metersPerSecond * 3.6:N1} km/h";
    }
}
