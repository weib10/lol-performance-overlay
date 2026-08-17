namespace LolPerformanceOverlay.Core;

public enum NetworkDestinationPurpose
{
    RuntimeData,
    UserInitiatedBrowser
}

/// <summary>
/// Runtime enforcement for the package network allowlist. Source scanning is only a secondary
/// guard; every Internet URI must pass this policy immediately before use.
/// </summary>
public static class NetworkDestinationPolicy
{
    private const string DataDragonHost = "ddragon.leagueoflegends.com";
    private const string OpGgHost = "op.gg";
    private const string LoopbackIpv4Host = "127.0.0.1";
    private const string LoopbackDnsHost = "localhost";
    private const string RiotApiHostSuffix = ".api.riotgames.com";

    // Riot's own official API gateway hosts, derived from the same platform/continent codes
    // PlatformRegionMapper already knows -- not a wildcard on the suffix, so a host that merely
    // resembles "*.api.riotgames.com" but isn't one of these exact, known routing values is
    // still rejected.
    private static readonly IReadOnlySet<string> RiotApiHosts = PlatformRegionMapper.KnownPlatformIds
        .Concat(["americas", "asia", "europe"])
        .Select(code => code + RiotApiHostSuffix)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsAllowed(Uri destination, NetworkDestinationPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.IsAbsoluteUri)
        {
            return false;
        }

        return purpose switch
        {
            NetworkDestinationPurpose.RuntimeData =>
                (destination.IsLoopback &&
                 (string.Equals(destination.IdnHost, LoopbackIpv4Host, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(destination.IdnHost, LoopbackDnsHost, StringComparison.OrdinalIgnoreCase)) &&
                 (destination.Scheme == Uri.UriSchemeHttp ||
                  destination.Scheme == Uri.UriSchemeHttps)) ||
                (destination.Scheme == Uri.UriSchemeHttps &&
                 (string.Equals(destination.IdnHost, DataDragonHost, StringComparison.OrdinalIgnoreCase) ||
                  RiotApiHosts.Contains(destination.IdnHost))),
            NetworkDestinationPurpose.UserInitiatedBrowser =>
                destination.Scheme == Uri.UriSchemeHttps &&
                string.Equals(destination.IdnHost, OpGgHost, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public static Uri RequireAllowed(Uri destination, NetworkDestinationPurpose purpose) =>
        IsAllowed(destination, purpose)
            ? destination
            : throw new InvalidOperationException(
                $"Network destination is not allowed for {purpose}: {destination.GetLeftPart(UriPartial.Authority)}");

    /// <summary>
    /// The League loopback APIs use ephemeral self-signed certificates. Certificate bypass is never
    /// valid for Data Dragon, browser links, alternate loopback addresses, or non-HTTPS requests.
    /// </summary>
    public static bool AllowsLoopbackCertificateBypass(Uri? destination) =>
        destination is not null &&
        destination.IsAbsoluteUri &&
        destination.Scheme == Uri.UriSchemeHttps &&
        destination.IsLoopback &&
        (string.Equals(destination.IdnHost, LoopbackIpv4Host, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(destination.IdnHost, LoopbackDnsHost, StringComparison.OrdinalIgnoreCase));
}
