using System.Reflection;
using LolPerformanceOverlay.Core;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class PerformanceScorerTests
{
    [Fact]
    public void ZeroStateRemainsNeutralAtGameStart()
    {
        var frame = Frame(60, Enumerable.Range(0, 10)
            .Select(index => Player(index, index < 5 ? 100 : 200, ChampionArchetype.Fighter))
            .ToArray());

        var result = new PerformanceScorer().Evaluate(frame);

        Assert.All(result.Teams.SelectMany(team => team.Players), player =>
            Assert.Equal(50d, player.PerformanceScore));
        Assert.Equal(PerformanceConfidence.Low, result.Confidence);
    }

    [Fact]
    public void StrongMarksmanScoresAboveWeakMarksmanAfterConfidenceRamp()
    {
        var players = Enumerable.Range(0, 10)
            .Select(index => Player(index, index < 5 ? 100 : 200, ChampionArchetype.Marksman))
            .ToArray();
        players[0] = Player(
            0,
            100,
            ChampionArchetype.Marksman,
            kills: 12,
            deaths: 1,
            assists: 8,
            creep: 90,
            level: 15,
            gold: 12000);
        players[9] = Player(
            9,
            200,
            ChampionArchetype.Marksman,
            kills: 0,
            deaths: 10,
            assists: 1,
            creep: 10,
            level: 8,
            gold: 2500);

        var result = new PerformanceScorer().Evaluate(Frame(600, players));
        var strong = result.Teams.SelectMany(team => team.Players).Single(player => player.StableKey == "p0");
        var weak = result.Teams.SelectMany(team => team.Players).Single(player => player.StableKey == "p9");

        Assert.True(strong.PerformanceScore > 75d);
        Assert.True(weak.PerformanceScore < 25d);
        Assert.Equal("強勢", strong.PerformanceLabel);
        Assert.Equal("明顯落後", weak.PerformanceLabel);
    }

    [Fact]
    public void SupportTemplateRewardsParticipationAndSurvival()
    {
        var players = Enumerable.Range(0, 10)
            .Select(index => Player(index, index < 5 ? 100 : 200, ChampionArchetype.Fighter))
            .ToArray();
        players[0] = Player(
            0,
            100,
            ChampionArchetype.Support,
            kills: 0,
            deaths: 1,
            assists: 19,
            creep: 2,
            level: 12,
            gold: 5000);
        players[1] = Player(
            1,
            100,
            ChampionArchetype.Marksman,
            kills: 3,
            deaths: 8,
            assists: 2,
            creep: 65,
            level: 11,
            gold: 7500);

        var result = new PerformanceScorer().Evaluate(Frame(600, players));
        var support = result.Teams.SelectMany(team => team.Players).Single(player => player.StableKey == "p0");
        var marksman = result.Teams.SelectMany(team => team.Players).Single(player => player.StableKey == "p1");

        Assert.True(support.PerformanceScore > marksman.PerformanceScore);
    }

    [Theory]
    [InlineData(119, 0)]
    [InlineData(300, 0.5)]
    [InlineData(480, 1)]
    public void AramConfidenceUsesTwoToEightMinuteRamp(double seconds, double expected)
    {
        var frame = Frame(seconds, Array.Empty<RawPlayerState>()) with { QueueId = 450, GameMode = "ARAM" };
        Assert.Equal(expected, PerformanceScorer.ConfidenceValue(frame), 3);
    }

    [Fact]
    public void HiddenChampSelectPlayerStaysAnonymous()
    {
        var frame = Frame(0, Array.Empty<RawPlayerState>()) with
        {
            Phase = LeaguePhase.ChampSelect,
            ChampSelectMembers =
            [
                new("cell-0", "測試玩家02#TEST", 100, 22, "Ashe", null, false),
                new("cell-1", null, 200, 0, "", null, true)
            ]
        };

        var result = new PerformanceScorer().Evaluate(frame);
        var anonymous = result.Teams.SelectMany(team => team.Players).Single(player => player.IsAnonymous);

        Assert.Equal("匿名玩家", anonymous.DisplayName);
        Assert.Null(anonymous.PerformanceScore);
    }

    [Fact]
    public void OverlayModelsDoNotExposeRawScoreboardFields()
    {
        var forbidden = new[] { "Kills", "Deaths", "Assists", "Level", "CreepScore", "Respawn", "ItemGold" };
        var publicOverlayTypes = new[]
        {
            typeof(OverlaySnapshot),
            typeof(OverlayTeam),
            typeof(OverlayPlayer)
        };

        var propertyNames = publicOverlayTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, name =>
            forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    private static LeagueSessionFrame Frame(double seconds, IReadOnlyList<RawPlayerState> players) =>
        new(
            LeaguePhase.InGame,
            DateTimeOffset.Now,
            seconds,
            "ARAM",
            450,
            "Player 0#TW2",
            Array.Empty<ChampSelectMember>(),
            players);

    private static RawPlayerState Player(
        int index,
        int team,
        ChampionArchetype archetype,
        int kills = 2,
        int deaths = 2,
        int assists = 3,
        int creep = 25,
        int level = 10,
        int gold = 6000) =>
        new(
            $"p{index}",
            $"Player {index}#TW2",
            team,
            $"Champion{index}",
            $"Champion {index}",
            null,
            [archetype],
            kills,
            deaths,
            assists,
            creep,
            level,
            [new RawItemState(1000 + index, 1, gold)]);
}
