namespace LolPerformanceOverlay.Core;

/// <summary>Maps Riot client region codes to the public platform identifiers used by providers.</summary>
public static class PlatformRegionMapper
{
    private static readonly IReadOnlyDictionary<string, string> PlatformIds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BR"] = "br1",
            ["BR1"] = "br1",
            ["EUN"] = "eun1",
            ["EUNE"] = "eun1",
            ["EUN1"] = "eun1",
            ["EUW"] = "euw1",
            ["EUW1"] = "euw1",
            ["JP"] = "jp1",
            ["JP1"] = "jp1",
            ["KR"] = "kr",
            ["LA1"] = "la1",
            ["LAN"] = "la1",
            ["LA2"] = "la2",
            ["LAS"] = "la2",
            ["NA"] = "na1",
            ["NA1"] = "na1",
            ["OC"] = "oc1",
            ["OCE"] = "oc1",
            ["OC1"] = "oc1",
            ["PH"] = "ph2",
            ["PH2"] = "ph2",
            ["RU"] = "ru",
            ["SG"] = "sg2",
            ["SG2"] = "sg2",
            ["TH"] = "th2",
            ["TH2"] = "th2",
            ["TR"] = "tr1",
            ["TR1"] = "tr1",
            ["TW"] = "tw2",
            ["TW2"] = "tw2",
            ["VN"] = "vn2",
            ["VN2"] = "vn2"
        };

    // account-v1 recognizes exactly these three continents; SEA has no account-v1 route of
    // its own. Riot's own guidance for the platforms that launched under the newer SEA
    // match-v5 grouping (oc1, ph2, sg2, th2, tw2, vn2) is to use asia for account-v1 instead.
    // Source: developer.riotgames.com/apis (account-v1 page) and Riot Games Developer
    // Relations' SEA-launch announcement, cross-checked 2026-08-17.
    private static readonly IReadOnlyDictionary<string, string> AccountRegionalRoutes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["na1"] = "americas",
            ["br1"] = "americas",
            ["la1"] = "americas",
            ["la2"] = "americas",
            ["kr"] = "asia",
            ["jp1"] = "asia",
            ["oc1"] = "asia",
            ["ph2"] = "asia",
            ["sg2"] = "asia",
            ["th2"] = "asia",
            ["tw2"] = "asia",
            ["vn2"] = "asia",
            ["eun1"] = "europe",
            ["euw1"] = "europe",
            ["tr1"] = "europe",
            ["ru"] = "europe"
        };

    /// <summary>Every platform ID this mapper can resolve a client region code to.</summary>
    public static IReadOnlyCollection<string> KnownPlatformIds { get; } =
        PlatformIds.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string? TryMap(string? clientRegion)
    {
        if (string.IsNullOrWhiteSpace(clientRegion))
        {
            return null;
        }

        return PlatformIds.GetValueOrDefault(clientRegion.Trim());
    }

    /// <summary>Maps an already-resolved platform ID (e.g. "tw2") to its account-v1 continent.</summary>
    public static string? TryMapAccountRegionalRoute(string? platformId)
    {
        if (string.IsNullOrWhiteSpace(platformId))
        {
            return null;
        }

        return AccountRegionalRoutes.GetValueOrDefault(platformId.Trim());
    }
}
