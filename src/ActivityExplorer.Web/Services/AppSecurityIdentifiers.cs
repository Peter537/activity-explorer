using System.Security.Cryptography;
using System.Text;

namespace ActivityExplorer.Web.Services;

internal static class AppSecurityIdentifiers
{
    internal const string DataProtectionApplicationName = "ActivityExplorer";
    internal const string AntiforgeryCookiePrefix = ".ActivityExplorer.Antiforgery.";

    internal static string GetAntiforgeryCookieName(string appDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDataRoot));
        if (OperatingSystem.IsWindows()) canonicalRoot = canonicalRoot.ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRoot));
        return AntiforgeryCookiePrefix + Convert.ToHexString(digest.AsSpan(0, 6)).ToLowerInvariant();
    }
}
