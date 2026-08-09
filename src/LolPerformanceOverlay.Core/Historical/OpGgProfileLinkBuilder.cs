namespace LolPerformanceOverlay.Core;

public sealed record ExternalBrowserAction(Uri Destination, string Label, bool ReadsDataBack);

/// <summary>
/// Creates an ordinary public profile link only. It performs no request, parsing, cookie access,
/// browser automation, or data import.
/// </summary>
public static class OpGgProfileLinkBuilder
{
    private static readonly IReadOnlyDictionary<string, string> SupportedRegions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["br1"] = "br",
            ["eun1"] = "eune",
            ["euw1"] = "euw",
            ["jp1"] = "jp",
            ["kr"] = "kr",
            ["la1"] = "lan",
            ["la2"] = "las",
            ["na1"] = "na",
            ["oc1"] = "oce",
            ["ph2"] = "ph",
            ["ru"] = "ru",
            ["sg2"] = "sg",
            ["th2"] = "th",
            ["tr1"] = "tr",
            ["tw2"] = "tw",
            ["vn2"] = "vn"
        };

    public static bool TryBuild(
        RevealedPlayerIdentity player,
        out ExternalBrowserAction action)
    {
        ArgumentNullException.ThrowIfNull(player);
        action = null!;
        if (!SupportedRegions.TryGetValue(player.Region, out var region))
        {
            return false;
        }

        var identitySegment = $"{Uri.EscapeDataString(player.GameName)}-{Uri.EscapeDataString(player.TagLine)}";
        var destination = NetworkDestinationPolicy.RequireAllowed(
            new Uri($"https://op.gg/lol/summoners/{region}/{identitySegment}", UriKind.Absolute),
            NetworkDestinationPurpose.UserInitiatedBrowser);
        action = new ExternalBrowserAction(destination, "在瀏覽器開啟 OP.GG", ReadsDataBack: false);
        return true;
    }
}
