using LolPerformanceOverlay.Core;
using LolPerformanceOverlay.Core.Presentation;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class OfficialRankAttachmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TenAvailableProfilesGiveAllTenPlayersAShortCode()
    {
        var snapshot = TenPlayerSnapshot();
        var profiles = ProfilesResult(Enumerable.Range(1, 10).Select(number => AvailableEntry(number, "GOLD", "IV")));

        var attached = OfficialRankAttachment.Attach(snapshot, profiles);

        var players = attached.Teams.SelectMany(team => team.Players).ToArray();
        Assert.Equal(10, players.Length);
        Assert.All(players, player => Assert.False(string.IsNullOrEmpty(player.OfficialRank?.ShortCode)));
    }

    [Fact]
    public void JoinIsByStableKeyNotDisplayText()
    {
        var snapshot = TenPlayerSnapshot();
        // HistoricalTestData.Player(1) resolves to game name "Synthetic Player 01" / tag
        // "FAKE01" -- deliberately different from the "Synthetic Row 01#FAKE01" DisplayName
        // the matching OverlayPlayer carries below, so a match here can only have happened
        // through StableKey, never a name comparison.
        var profiles = ProfilesResult([AvailableEntry(1, "DIAMOND", "IV")]);

        var attached = OfficialRankAttachment.Attach(snapshot, profiles);

        var player = attached.Teams
            .SelectMany(team => team.Players)
            .Single(candidate => candidate.StableKey == HistoricalTestData.Player(1).StableKey);
        Assert.Equal("D4", player.OfficialRank?.ShortCode);
    }

    [Fact]
    public void NoProfilesReturnsTheSameSnapshotInstance()
    {
        var snapshot = TenPlayerSnapshot();

        Assert.Same(snapshot, OfficialRankAttachment.Attach(snapshot, null));
        Assert.Same(snapshot, OfficialRankAttachment.Attach(snapshot, ProfilesResult([])));
    }

    [Fact]
    public void EntryForAStableKeyNotInTheSnapshotIsIgnoredWithoutThrowing()
    {
        var snapshot = TenPlayerSnapshot();
        // Player 11 was never part of the ten-player roster built by TenPlayerSnapshot.
        var profiles = ProfilesResult([AvailableEntry(11, "GOLD", "IV")]);

        var exception = Record.Exception(() => OfficialRankAttachment.Attach(snapshot, profiles));
        Assert.Null(exception);

        var attached = OfficialRankAttachment.Attach(snapshot, profiles);
        Assert.Same(snapshot, attached);
        Assert.All(attached.Teams.SelectMany(team => team.Players), player => Assert.Null(player.OfficialRank));
    }

    [Fact]
    public void AttachingTheSameProfilesTwiceProducesNoDiffOrReducerUpdate()
    {
        var profiles = ProfilesResult(Enumerable.Range(1, 10).Select(number => AvailableEntry(number, "GOLD", "IV")));
        var first = OfficialRankAttachment.Attach(TenPlayerSnapshot(Now), profiles);
        var second = OfficialRankAttachment.Attach(TenPlayerSnapshot(Now.AddSeconds(1)), profiles);

        var diff = VisibleSnapshot.Diff(first, second);
        Assert.False(diff.HasChanges);

        var reducer = new OverlayUpdateReducer(TimeSpan.Zero);
        Assert.NotNull(reducer.Offer(first));
        Assert.Null(reducer.Offer(second));
    }

    [Fact]
    public void RankChangeFlagsOnlyTheRankFieldNotScoreChampionOrIcon()
    {
        var before = OfficialRankAttachment.Attach(TenPlayerSnapshot(), ProfilesResult([AvailableEntry(1, "GOLD", "IV")]));
        var after = OfficialRankAttachment.Attach(TenPlayerSnapshot(), ProfilesResult([AvailableEntry(1, "DIAMOND", "II")]));

        var diff = VisibleSnapshot.Diff(before, after);

        var teamDiff = Assert.Single(diff.Teams);
        var playerDiff = Assert.Single(teamDiff.Players);
        Assert.Equal(HistoricalTestData.Player(1).StableKey, playerDiff.StableKey);
        Assert.Equal(OverlayPlayerFields.OfficialRank, playerDiff.Fields);
    }

    [Fact]
    public void RosterChangeStopsAStaleEntryFromAttachingToTheNewOccupant()
    {
        var oldRoster = SingleOccupantSnapshot(playerNumber: 1);
        var profiles = ProfilesResult([AvailableEntry(1, "DIAMOND", "IV")]);
        var attachedOld = OfficialRankAttachment.Attach(oldRoster, profiles);
        Assert.NotNull(attachedOld.Teams[0].Players[0].OfficialRank);

        // A different player now occupies the same seat after a roster change; the profiles
        // result still only describes the departed player 1.
        var newRoster = SingleOccupantSnapshot(playerNumber: 2);

        var attachedNew = OfficialRankAttachment.Attach(newRoster, profiles);

        Assert.Same(newRoster, attachedNew);
        Assert.Null(attachedNew.Teams[0].Players[0].OfficialRank);
    }

    [Fact]
    public void AnonymousPlayersNeverReceiveAnOfficialRank()
    {
        var identity = HistoricalTestData.Player(1);
        var anonymous = new OverlayPlayer(
            identity.StableKey,
            "匿名玩家",
            100,
            "尚未選擇",
            null,
            true,
            null,
            null,
            null);
        var snapshot = new OverlaySnapshot(
            LeaguePhase.InGame,
            Now,
            "本場即時表現",
            "雙方接近",
            null,
            null,
            null,
            null,
            PerformanceConfidence.High,
            [new OverlayTeam(100, "藍方", null, [anonymous])]);
        var profiles = ProfilesResult([AvailableEntry(1, "DIAMOND", "IV")]);

        var attached = OfficialRankAttachment.Attach(snapshot, profiles);

        Assert.Same(snapshot, attached);
        Assert.Null(attached.Teams[0].Players[0].OfficialRank);
    }

    private static OverlaySnapshot TenPlayerSnapshot(DateTimeOffset? capturedAt = null) =>
        new(
            LeaguePhase.InGame,
            capturedAt ?? Now,
            "本場即時表現",
            "雙方接近",
            null,
            null,
            null,
            null,
            PerformanceConfidence.High,
            [
                new OverlayTeam(100, "藍方", 50, Enumerable.Range(1, 5).Select(RowPlayer).ToArray()),
                new OverlayTeam(200, "紅方", 50, Enumerable.Range(6, 5).Select(RowPlayer).ToArray())
            ]);

    private static OverlaySnapshot SingleOccupantSnapshot(int playerNumber) =>
        new(
            LeaguePhase.InGame,
            Now,
            "本場即時表現",
            "雙方接近",
            null,
            null,
            null,
            null,
            PerformanceConfidence.High,
            [new OverlayTeam(100, "藍方", 50, [RowPlayer(playerNumber)])]);

    private static OverlayPlayer RowPlayer(int number) =>
        new(
            HistoricalTestData.Player(number).StableKey,
            $"Synthetic Row {number:D2}#FAKE{number:D2}",
            number <= 5 ? 100 : 200,
            "Ashe",
            null,
            false,
            50,
            "持平",
            PerformanceConfidence.High);

    private static HistoricalProfileEntry AvailableEntry(int number, string tier, string division)
    {
        var identity = HistoricalTestData.Player(number);
        var profile = new HistoricalProfile(
            HistoricalQueue.RankedSolo,
            new OfficialRank(HistoricalQueue.RankedSolo, tier, division, 42),
            sampleCount: 20,
            fetchedAt: Now,
            HistoricalConfidence.High,
            Array.Empty<HistoricalChampionUsage>(),
            Array.Empty<HistoricalRoleUsage>(),
            playStyle: null,
            new HistoricalProfileSource(HistoricalSourceKind.Synthetic, "合成測試資料"));
        return HistoricalProfileEntry.WithProfile(identity, HistoricalProfileAvailability.Available, profile);
    }

    private static HistoricalProfilesResult ProfilesResult(IEnumerable<HistoricalProfileEntry> entries) =>
        new(HistoricalProfileAvailability.Available, entries, Now);
}
