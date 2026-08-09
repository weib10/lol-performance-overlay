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

    public static string? TryMap(string? clientRegion)
    {
        if (string.IsNullOrWhiteSpace(clientRegion))
        {
            return null;
        }

        return PlatformIds.GetValueOrDefault(clientRegion.Trim());
    }
}
