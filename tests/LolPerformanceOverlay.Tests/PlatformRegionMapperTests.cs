using LolPerformanceOverlay.Core;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class PlatformRegionMapperTests
{
    [Theory]
    [InlineData("TW", "tw2")]
    [InlineData("na", "na1")]
    [InlineData("EUW1", "euw1")]
    [InlineData("LAN", "la1")]
    public void MapsClientRegionsToPlatformIds(string clientRegion, string expected) =>
        Assert.Equal(expected, PlatformRegionMapper.TryMap(clientRegion));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UNKNOWN")]
    public void UnknownRegionDoesNotGuess(string? clientRegion) =>
        Assert.Null(PlatformRegionMapper.TryMap(clientRegion));

    // account-v1 recognizes only americas/asia/europe; the platforms that shipped under the
    // newer SEA match-v5 grouping have no account-v1 route of their own and must fall back to
    // asia, per Riot's own account-v1 API page and SEA-launch announcement.
    [Theory]
    [InlineData("na1", "americas")]
    [InlineData("br1", "americas")]
    [InlineData("la1", "americas")]
    [InlineData("la2", "americas")]
    [InlineData("kr", "asia")]
    [InlineData("jp1", "asia")]
    [InlineData("oc1", "asia")]
    [InlineData("ph2", "asia")]
    [InlineData("sg2", "asia")]
    [InlineData("th2", "asia")]
    [InlineData("tw2", "asia")]
    [InlineData("vn2", "asia")]
    [InlineData("eun1", "europe")]
    [InlineData("euw1", "europe")]
    [InlineData("tr1", "europe")]
    [InlineData("ru", "europe")]
    public void MapsEveryKnownPlatformToItsAccountV1Continent(string platformId, string expectedContinent) =>
        Assert.Equal(expectedContinent, PlatformRegionMapper.TryMapAccountRegionalRoute(platformId));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sea")]
    [InlineData("unknown-platform")]
    public void AccountV1RouteIsNullWithoutSeaOrAnUnknownPlatform(string? platformId) =>
        Assert.Null(PlatformRegionMapper.TryMapAccountRegionalRoute(platformId));

    [Fact]
    public void EveryKnownPlatformIdHasAnAccountV1Route()
    {
        // Every platform the client-region mapper can resolve must also resolve to an
        // account-v1 continent, or a Riot lookup for that region would silently no-op.
        Assert.All(
            PlatformRegionMapper.KnownPlatformIds,
            platformId => Assert.NotNull(PlatformRegionMapper.TryMapAccountRegionalRoute(platformId)));
    }
}
