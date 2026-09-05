namespace ActivityExplorer.Infrastructure.Import;

internal static class RowingLabels
{
    public static bool IsRowing(string? text)
    {
        var normalized = text?.Trim().Replace(" ", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized is "row" or "rowing" or "indoorrow" or "indoorrowing" or
            "virtualrow" or "virtualrowing" or "outdoorrow" or "outdoorrowing";
    }
}
