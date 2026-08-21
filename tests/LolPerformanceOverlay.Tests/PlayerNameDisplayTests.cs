using LolPerformanceOverlay.Core;
using LolPerformanceOverlay.Core.Presentation;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class PlayerNameDisplayTests
{
    // "測試玩家" + "#TEST" -- visibly-synthetic Riot ID shape, same convention already used by
    // ReplaySessionSource's fixtures (see eng/package-config.json's syntheticGameNamePrefixes /
    // syntheticTagLines), never a real player's identity.
    private const string SyntheticRiotId = "測試玩家01#TEST";

    private static OverlayPlayer MakePlayer(
        string displayName,
        string championName = "Ahri",
        bool isAnonymous = false) =>
        new(
            StableKey: "100:seat1",
            DisplayName: displayName,
            Team: 100,
            ChampionName: championName,
            ChampionIconPath: null,
            IsAnonymous: isAnonymous,
            PerformanceScore: null,
            PerformanceLabel: null,
            Confidence: null);

    [Fact]
    public void ResolveShowsTheChampionNameInChampionMode()
    {
        var player = MakePlayer(SyntheticRiotId, championName: "Ahri");

        Assert.Equal("Ahri", PlayerNameDisplay.Resolve(player, PlayerNameDisplayMode.ChampionName));
    }

    [Fact]
    public void ResolveShowsTheRiotIdInRiotIdMode()
    {
        var player = MakePlayer(SyntheticRiotId, championName: "Ahri");

        Assert.Equal(SyntheticRiotId, PlayerNameDisplay.Resolve(player, PlayerNameDisplayMode.RiotId));
    }

    [Fact]
    public void ResolveNeverRevealsAnAnonymousPlayerEvenInRiotIdMode()
    {
        // An anonymous seat's DisplayName is already a placeholder upstream (see
        // PerformanceScorer.BuildChampSelectSnapshot), but Resolve must not rely on that alone
        // -- it is the one place AGENTS.md/SECURITY.md's "never restore an anonymous identity"
        // promise is pinned for the display-mode setting, so it re-checks IsAnonymous itself
        // even when DisplayName happens to hold something Riot-ID-shaped.
        var player = MakePlayer(SyntheticRiotId, championName: "Lux", isAnonymous: true);

        Assert.Equal("Lux", PlayerNameDisplay.Resolve(player, PlayerNameDisplayMode.RiotId));
    }

    [Fact]
    public void ResolveInChampionModeIgnoresAnonymityBecauseItAlreadyShowsTheChampion()
    {
        var player = MakePlayer(SyntheticRiotId, championName: "Lux", isAnonymous: true);

        Assert.Equal("Lux", PlayerNameDisplay.Resolve(player, PlayerNameDisplayMode.ChampionName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveFallsBackToTheChampionNameWhenTheRiotIdIsBlank(string blankDisplayName)
    {
        var player = MakePlayer(blankDisplayName, championName: "Jinx");

        Assert.Equal("Jinx", PlayerNameDisplay.Resolve(player, PlayerNameDisplayMode.RiotId));
    }

    [Fact]
    public void ResolveThrowsForANullPlayer()
    {
        Assert.Throws<ArgumentNullException>(
            () => PlayerNameDisplay.Resolve(null!, PlayerNameDisplayMode.ChampionName));
    }

    [Fact]
    public void ColumnHeaderNamesTheChampionColumnInChampionMode()
    {
        Assert.Equal("英雄", PlayerNameDisplay.ColumnHeader(PlayerNameDisplayMode.ChampionName));
    }

    [Fact]
    public void ColumnHeaderNamesTheRiotIdColumnInRiotIdMode()
    {
        Assert.Equal("Riot ID", PlayerNameDisplay.ColumnHeader(PlayerNameDisplayMode.RiotId));
    }
}
