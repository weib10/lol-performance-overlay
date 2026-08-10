using LolPerformanceOverlay.Core;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class StaticAssetPolicyTests
{
    [Theory]
    [InlineData("FiddleSticks", true)]
    [InlineData("Kaisa", true)]
    [InlineData("../outside", false)]
    [InlineData("A/B", false)]
    [InlineData("A\\B", false)]
    [InlineData("", false)]
    public void ChampionKeyUsesFileSafeDataDragonEnvelope(string value, bool expected) =>
        Assert.Equal(expected, StaticAssetPolicy.IsChampionKey(value));

    [Theory]
    [InlineData("26.15.1", true)]
    [InlineData("../26.15.1", false)]
    [InlineData("26.15.1/path", false)]
    public void VersionUsesSafeUriSegmentEnvelope(string value, bool expected) =>
        Assert.Equal(expected, StaticAssetPolicy.IsVersion(value));

    [Fact]
    public void ChildPathCannotEscapeRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "lol-overlay-synthetic-root");

        Assert.True(StaticAssetPolicy.TryResolveChildPath(root, "Ashe.png", out var safe));
        Assert.StartsWith(Path.GetFullPath(root), safe, StringComparison.OrdinalIgnoreCase);
        Assert.False(StaticAssetPolicy.TryResolveChildPath(root, $"..{Path.DirectorySeparatorChar}outside.png", out _));
    }
}
