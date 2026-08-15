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
        Assert.Equal("本場較高", strong.PerformanceLabel);
        Assert.Equal("本場較低", weak.PerformanceLabel);
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

    [Fact]
    public void TeamSummaryLabelsDescriptiveMetricRatherThanGameAdvantage()
    {
        var players = Enumerable.Range(0, 10)
            .Select(index => index < 5
                ? Player(index, 100, ChampionArchetype.Fighter, kills: 10, deaths: 1, assists: 10, gold: 12_000)
                : Player(index, 200, ChampionArchetype.Fighter, kills: 0, deaths: 10, assists: 0, gold: 2_000))
            .ToArray();

        var result = new PerformanceScorer().Evaluate(Frame(900, players));

        Assert.Contains("我方本場指標較高", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("領先", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("落後", result.Summary, StringComparison.Ordinal);
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

    [Fact]
    public void SortedPercentileRanksMatchOriginalTieSemantics()
    {
        var values = new[] { 8d, 1d, 4d, 4d, 12d, 4.0000005d, -2d };
        var expected = values.Select(value => OriginalPercentile(values, value)).ToArray();

        var actual = PerformanceScorer.PercentileRanksForTesting(values);

        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index], actual[index], 10);
        }
    }

    [Fact]
    public void SortedPercentileRanksMatchQuadraticReferenceAcrossDeterministicCorpora()
    {
        var random = new Random(0x5C0E);
        for (var corpus = 0; corpus < 100; corpus++)
        {
            var values = Enumerable.Range(0, random.Next(2, 65))
                .Select(_ => random.Next(-20, 21) + random.Next(0, 3) * 0.0000004d)
                .ToArray();
            var expected = values.Select(value => OriginalPercentile(values, value)).ToArray();

            var actual = PerformanceScorer.PercentileRanksForTesting(values);

            Assert.Equal(expected.Length, actual.Length);
            for (var index = 0; index < expected.Length; index++)
            {
                Assert.Equal(expected[index], actual[index], 10);
            }
        }
    }

    [Fact]
    public void RepeatedSessionsAndRosterChangesKeepOnlyCurrentPlayerState()
    {
        var scorer = new PerformanceScorer();
        for (var match = 0; match < 200; match++)
        {
            var players = Enumerable.Range(0, 10)
                .Select(index => Player(match * 10 + index, index < 5 ? 100 : 200, ChampionArchetype.Fighter))
                .ToArray();
            scorer.Evaluate(Frame(600, players) with
            {
                Phase = LeaguePhase.InGame,
                CapturedAt = DateTimeOffset.UnixEpoch.AddMinutes(match)
            });
            Assert.InRange(scorer.RetainedScoreCount, 0, 10);
            scorer.Evaluate(Frame(0, Array.Empty<RawPlayerState>()) with { Phase = LeaguePhase.EndOfGame });
            Assert.Equal(0, scorer.RetainedScoreCount);
        }
    }

    [Fact]
    public void StartingNextMatchWithoutChampSelectDoesNotReusePreviousEma()
    {
        var scorer = new PerformanceScorer();
        var players = Enumerable.Range(0, 10)
            .Select(index => Player(index, index < 5 ? 100 : 200, ChampionArchetype.Fighter))
            .ToArray();
        players[0] = Player(0, 100, ChampionArchetype.Fighter, kills: 20, deaths: 0, assists: 10);
        var first = scorer.Evaluate(Frame(600, players));
        scorer.Evaluate(Frame(0, Array.Empty<RawPlayerState>()) with { Phase = LeaguePhase.Lobby });

        var neutralPlayers = Enumerable.Range(0, 10)
            .Select(index => Player(index, index < 5 ? 100 : 200, ChampionArchetype.Fighter))
            .ToArray();
        var next = scorer.Evaluate(Frame(60, neutralPlayers) with { Phase = LeaguePhase.Loading });

        Assert.NotEqual(
            first.Teams.SelectMany(team => team.Players).Single(player => player.StableKey == "p0").PerformanceScore,
            next.Teams.SelectMany(team => team.Players).Single(player => player.StableKey == "p0").PerformanceScore);
        Assert.Equal(10, scorer.RetainedScoreCount);
    }

    [Fact]
    public void GameClockRestartWithinLivePhaseClearsPreviousMatchEma()
    {
        var scorer = new PerformanceScorer();
        var dominantPlayers = Enumerable.Range(0, 10)
            .Select(index => Player(index, index < 5 ? 100 : 200, ChampionArchetype.Fighter))
            .ToArray();
        dominantPlayers[0] = Player(0, 100, ChampionArchetype.Fighter, kills: 20, deaths: 0, assists: 10);
        _ = scorer.Evaluate(Frame(1_200, dominantPlayers));

        var neutralPlayers = Enumerable.Range(0, 10)
            .Select(index => Player(index, index < 5 ? 100 : 200, ChampionArchetype.Fighter))
            .ToArray();
        var reusedScorerResult = scorer.Evaluate(Frame(30, neutralPlayers));
        var freshScorerResult = new PerformanceScorer().Evaluate(Frame(30, neutralPlayers));

        Assert.Equal(
            freshScorerResult.Teams.SelectMany(team => team.Players).Single(player => player.StableKey == "p0").PerformanceScore,
            reusedScorerResult.Teams.SelectMany(team => team.Players).Single(player => player.StableKey == "p0").PerformanceScore);
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

    [Fact]
    public void LiveSnapshotPublishesItemValueQuantisedToItsDisplayedPrecision()
    {
        var scorer = new PerformanceScorer();
        var players = Enumerable.Range(0, 10)
            .Select(index => Player(
                index,
                index < 5 ? 100 : 200,
                ChampionArchetype.Fighter,
                gold: 6_249 + index))
            .ToArray();

        var snapshot = scorer.Score(Frame(600d, players));
        var carried = snapshot.Teams.SelectMany(team => team.Players).ToArray();

        // 6,249..6,258 all render as "6.2k", so they must land on one view-model value
        // and never make the overlay redraw for a change the player cannot see.
        Assert.All(carried, player => Assert.Equal(6_200, player.ItemGold));
        Assert.All(carried, player => Assert.Null(player.PickOrder));
    }

    [Fact]
    public void ChampSelectSnapshotCarriesPickOrderWithoutResortingTheRoster()
    {
        var scorer = new PerformanceScorer();
        var members = new[]
        {
            new ChampSelectMember("cell-0-100", "Player 0#TW2", 100, 1, "Annie", null, false, 3),
            new ChampSelectMember("cell-1-100", "Player 1#TW2", 100, 2, "Olaf", null, false, 1)
        };
        var frame = new LeagueSessionFrame(
            LeaguePhase.ChampSelect,
            DateTimeOffset.UnixEpoch,
            0d,
            "CLASSIC",
            420,
            "Player 0#TW2",
            members,
            Array.Empty<RawPlayerState>());

        var players = scorer.Score(frame).Teams.Single().Players;

        Assert.Equal(["cell-0-100", "cell-1-100"], players.Select(player => player.StableKey));
        Assert.Equal([3, 1], players.Select(player => player.PickOrder));
        Assert.All(players, player => Assert.Null(player.ItemGold));
    }

    private static double OriginalPercentile(IReadOnlyList<double> values, double value)
    {
        const double epsilon = 0.000001d;
        var less = values.Count(candidate => candidate < value - epsilon);
        var equal = values.Count(candidate => Math.Abs(candidate - value) <= epsilon);
        return Math.Clamp((less + 0.5d * Math.Max(equal - 1, 0)) / (values.Count - 1d), 0d, 1d);
    }
}
