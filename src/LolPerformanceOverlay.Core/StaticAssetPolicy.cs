namespace LolPerformanceOverlay.Core;

/// <summary>
/// Constrains remote static-data identifiers before they become URI segments or local file names.
/// </summary>
public static class StaticAssetPolicy
{
    public static bool IsChampionKey(string? value) =>
        IsIdentifier(value, maximumLength: 64, allowDot: false);

    public static bool IsVersion(string? value) =>
        IsIdentifier(value, maximumLength: 32, allowDot: true);

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

    private static bool IsIdentifier(string? value, int maximumLength, bool allowDot)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && (!allowDot || character != '.'))
            {
                return false;
            }
        }

        return true;
    }
}
