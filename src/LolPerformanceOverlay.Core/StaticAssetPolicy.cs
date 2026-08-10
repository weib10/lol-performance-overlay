namespace LolPerformanceOverlay.Core;

/// <summary>
/// Constrains remote static-data identifiers before they become URI segments or local file names.
/// </summary>
public static class StaticAssetPolicy
{
    public static bool IsChampionKey(string? value) =>
        IsIdentifier(value, maximumLength: 64, allowDot: false, allowUnderscore: true);

    public static bool IsVersion(string? value) =>
        IsIdentifier(value, maximumLength: 32, allowDot: true, allowUnderscore: false);

    public static bool TryResolveChildPath(
        string rootDirectory,
        string fileName,
        out string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var root = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, fileName));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            fullPath = string.Empty;
            return false;
        }

        fullPath = candidate;
        return true;
    }

    // Data Dragon ships underscore champion ids (the Jade_* set), so rejecting '_' silently drops
    // them from every name/id lookup and from the icon path. Underscore is safe in both a URI
    // segment and a file name; escaping the cache root stays blocked by TryResolveChildPath.
    private static bool IsIdentifier(string? value, int maximumLength, bool allowDot, bool allowUnderscore)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character) ||
                (allowDot && character == '.') ||
                (allowUnderscore && character == '_'))
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
