using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Infrastructure.Processing;

public static class Fingerprint
{
    public static string For(ParsedActivity activity)
    {
        var first = activity.Points.FirstOrDefault(x => x.Latitude.HasValue && x.Longitude.HasValue);
        var last = activity.Points.LastOrDefault(x => x.Latitude.HasValue && x.Longitude.HasValue);
        var raw = string.Join("|",
            activity.Sport,
            activity.StartTimeUtc.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            Math.Round(activity.ElapsedTimeSeconds),
            Math.Round(activity.DistanceMeters / 10d),
            first is null ? "-" : $"{Math.Round(first.Latitude!.Value, 4)},{Math.Round(first.Longitude!.Value, 4)}",
            last is null ? "-" : $"{Math.Round(last.Latitude!.Value, 4)},{Math.Round(last.Longitude!.Value, 4)}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(input, cancellationToken);
        return Convert.ToHexString(hash);
    }
}
