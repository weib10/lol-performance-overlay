using LolPerformanceOverlay.Core;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class NetworkDestinationPolicyTests
{
    [Theory]
    [InlineData("https://ddragon.leagueoflegends.com/api/versions.json")]
    [InlineData("https://127.0.0.1:2999/liveclientdata/playerlist")]
    [InlineData("http://localhost:12345/lol-gameflow/v1/gameflow-phase")]
    public void RuntimeDataAllowsOnlyDataDragonAndLoopback(string destination) =>
        Assert.True(NetworkDestinationPolicy.IsAllowed(
            new Uri(destination),
            NetworkDestinationPurpose.RuntimeData));

    [Theory]
    [InlineData("wss://ddragon.leagueoflegends.com/socket")]
    [InlineData("http://ddragon.leagueoflegends.com/api/versions.json")]
    [InlineData("https://op.gg/lol/summoners/tw/example-safe")]
    public void RuntimeDataRejectsEveryOtherDestination(string destination) =>
        Assert.False(NetworkDestinationPolicy.IsAllowed(
            new Uri(destination),
            NetworkDestinationPurpose.RuntimeData));

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
