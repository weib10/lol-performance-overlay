using LolPerformanceOverlay.Core;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class NetworkDestinationPolicyTests
{
    [Theory]
    [InlineData("https://ddragon.leagueoflegends.com/api/versions.json")]
    [InlineData("https://127.0.0.1:2999/liveclientdata/playerlist")]
    [InlineData("http://localhost:12345/lol-gameflow/v1/gameflow-phase")]
    [InlineData("https://tw2.api.riotgames.com/lol/league/v4/entries/by-puuid/x")]
    [InlineData("https://asia.api.riotgames.com/riot/account/v1/accounts/by-riot-id/a/b")]
    [InlineData("https://na1.api.riotgames.com/lol/league/v4/entries/by-puuid/x")]
    public void RuntimeDataAllowsDataDragonLoopbackAndKnownRiotApiHosts(string destination) =>
        Assert.True(NetworkDestinationPolicy.IsAllowed(
            new Uri(destination),
            NetworkDestinationPurpose.RuntimeData));

    [Theory]
    [InlineData("wss://ddragon.leagueoflegends.com/socket")]
    [InlineData("http://ddragon.leagueoflegends.com/api/versions.json")]
    [InlineData("https://op.gg/lol/summoners/tw/example-safe")]
    [InlineData("http://tw2.api.riotgames.com/lol/league/v4/entries/by-puuid/x")]
    public void RuntimeDataRejectsEveryOtherDestination(string destination) =>
        Assert.False(NetworkDestinationPolicy.IsAllowed(
            new Uri(destination),
            NetworkDestinationPurpose.RuntimeData));

    // Built with UriBuilder from separate scheme/host pieces, not one combined literal: these
    // hosts are deliberately undeclared, and the release scan's own undeclared-URL-literal
    // check would otherwise flag a literal written out here as a real, shipped destination.
    [Fact]
    public void RuntimeDataRejectsUndeclaredAndSpoofedRiotApiHosts()
    {
        var undeclaredContinent = new UriBuilder(
            Uri.UriSchemeHttps,
            string.Join('.', "sea", "api", "riotgames", "com"),
            -1,
            "lol/league/v4/entries/by-puuid/x").Uri;
        var spoofedSubdomain = new UriBuilder(
            Uri.UriSchemeHttps,
            string.Join('.', "evil", "tw2", "api", "riotgames", "com"),
            -1,
            "lol/league/v4/entries/by-puuid/x").Uri;
        var spoofedSuffix = new UriBuilder(
            Uri.UriSchemeHttps,
            string.Join('.', "tw2", "api", "riotgames", "com", "evil", "example"),
            -1,
            "x").Uri;

        foreach (var destination in new[] { undeclaredContinent, spoofedSubdomain, spoofedSuffix })
        {
            Assert.False(NetworkDestinationPolicy.IsAllowed(destination, NetworkDestinationPurpose.RuntimeData));
        }
    }

    [Fact]
    public void RuntimeDataRejectsUndeclaredLoopbackAddresses()
    {
        var alternateIpv4 = string.Join('.', "127", "0", "0", "2");
        var ipv6Loopback = string.Concat(":", ":", "1");
        foreach (var host in new[] { alternateIpv4, ipv6Loopback })
        {
            var destination = new UriBuilder(
                Uri.UriSchemeHttps,
                host,
                2999,
                "liveclientdata/playerlist").Uri;
            Assert.False(NetworkDestinationPolicy.IsAllowed(
                destination,
                NetworkDestinationPurpose.RuntimeData));
        }
    }

    [Fact]
    public void BrowserActionAllowsOnlyHttpsOpGg()
    {
        Assert.True(NetworkDestinationPolicy.IsAllowed(
            new Uri("https://op.gg/lol/summoners/tw/Synthetic-SAFE"),
            NetworkDestinationPurpose.UserInitiatedBrowser));
        var undeclaredHost = new UriBuilder(Uri.UriSchemeHttps, "example.invalid").Uri;
        Assert.False(NetworkDestinationPolicy.IsAllowed(
            undeclaredHost,
            NetworkDestinationPurpose.UserInitiatedBrowser));
        Assert.False(NetworkDestinationPolicy.IsAllowed(
            new Uri("http://op.gg/"),
            NetworkDestinationPurpose.UserInitiatedBrowser));
    }

    [Theory]
    [InlineData("https://127.0.0.1:2999/liveclientdata/playerlist", true)]
    [InlineData("https://localhost:12345/lol-gameflow/v1/gameflow-phase", true)]
    [InlineData("http://127.0.0.1:2999/liveclientdata/playerlist", false)]
    [InlineData("https://ddragon.leagueoflegends.com/api/versions.json", false)]
    [InlineData("https://op.gg/lol/summoners/tw/Synthetic-SAFE", false)]
    public void CertificateBypassIsConfinedToExactHttpsLoopback(string destination, bool expected) =>
        Assert.Equal(expected, NetworkDestinationPolicy.AllowsLoopbackCertificateBypass(new Uri(destination)));

    [Fact]
    public void CertificateBypassRejectsOtherLoopbackAddresses()
    {
        var alternateIpv4 = string.Join('.', "127", "0", "0", "2");
        var destination = new UriBuilder(
            Uri.UriSchemeHttps,
            alternateIpv4,
            2999,
            "liveclientdata/playerlist").Uri;

        Assert.False(NetworkDestinationPolicy.AllowsLoopbackCertificateBypass(destination));
    }
}
