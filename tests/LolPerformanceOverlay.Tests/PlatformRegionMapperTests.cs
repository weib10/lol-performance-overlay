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
}
