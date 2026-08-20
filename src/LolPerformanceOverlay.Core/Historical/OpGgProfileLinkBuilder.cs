namespace LolPerformanceOverlay.Core;

public sealed record ExternalBrowserAction(Uri Destination, string Label, bool ReadsDataBack);

/// <summary>
/// Creates ordinary public OP.GG links only -- a single player's profile page, or a multi-search
/// of every revealed player in the current game. Both hand a plain URL to the user's own
/// browser; neither performs a request, parsing, cookie access, browser automation, or data
/// import.
/// </summary>
public static class OpGgProfileLinkBuilder
{
    // A League match never seats more than ten players. The roster passed to
    // TryBuildMultiSearch is already that size in the shipping caller (see
    // OverlayWindow/App.xaml.cs's BeginHistoricalLookup), but this bounds the query string
    // defensively for any other caller rather than trusting that invariant silently.
    private const int MaxRosterSize = 10;

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

    /// <summary>
    /// Builds one OP.GG multi-search link covering every player in <paramref name="players"/> at
    /// once, so a friend does not have to open ten separate profile tabs mid-game. Every entry
    /// carries its tag line -- a bare game name is ambiguous on OP.GG's own site, and a link that
    /// resolves to the wrong person is worse than no link -- which <see cref="RevealedPlayerIdentity"/>
    /// already guarantees by construction. Returns false for an empty roster, when any player's
    /// region does not map to a known OP.GG shard, or when the players disagree on region (a
    /// single match always shares one platform, so disagreement means the input was not one
    /// game's roster).
    /// </summary>
    public static bool TryBuildMultiSearch(
        IReadOnlyList<RevealedPlayerIdentity> players,
        out ExternalBrowserAction action)
    {
        ArgumentNullException.ThrowIfNull(players);
        action = null!;
        if (players.Count == 0)
        {
            return false;
        }

        var roster = players.Count > MaxRosterSize ? players.Take(MaxRosterSize).ToArray() : players;

        string? region = null;
        foreach (var player in roster)
        {
            if (!SupportedRegions.TryGetValue(player.Region, out var mappedRegion))
            {
                return false;
            }

            if (region is null)
            {
                region = mappedRegion;
            }
            else if (!string.Equals(region, mappedRegion, StringComparison.Ordinal))
            {
                return false;
            }
        }

        var entries = string.Join(
            ",",
            roster.Select(player => Uri.EscapeDataString($"{player.GameName}#{player.TagLine}")));
        var destination = NetworkDestinationPolicy.RequireAllowed(
            new Uri($"https://op.gg/zh-tw/lol/multisearch/{region}?summoners={entries}", UriKind.Absolute),
            NetworkDestinationPurpose.UserInitiatedBrowser);
        action = new ExternalBrowserAction(
            destination,
            "在瀏覽器開啟本場所有玩家的 OP.GG",
            ReadsDataBack: false);
        return true;
    }
}
