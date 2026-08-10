namespace LolPerformanceOverlay.Core;

public sealed class PerformanceScorer : IPerformanceScorer
{
    private const double EmaAlpha = 0.35;
    private const double PercentileEpsilon = 0.000001d;
    private readonly Dictionary<string, double> _smoothedScores = new(StringComparer.Ordinal);
    private LeaguePhase _lastPhase = LeaguePhase.None;
    private double _lastGameTimeSeconds;

    private static readonly IReadOnlyDictionary<ChampionArchetype, ArchetypeWeights> Weights =
        new Dictionary<ChampionArchetype, ArchetypeWeights>
        {
            [ChampionArchetype.Marksman] = new(0.30, 0.20, 0.15, 0.10, 0.15, 0.10),
            [ChampionArchetype.Assassin] = new(0.25, 0.25, 0.15, 0.10, 0.20, 0.05),
            [ChampionArchetype.Mage] = new(0.25, 0.15, 0.25, 0.10, 0.15, 0.10),
            [ChampionArchetype.Fighter] = new(0.25, 0.15, 0.20, 0.20, 0.15, 0.05),
            [ChampionArchetype.Tank] = new(0.15, 0.05, 0.30, 0.25, 0.15, 0.10),
            [ChampionArchetype.Support] = new(0.10, 0.05, 0.35, 0.25, 0.20, 0.05)
        };

    private static readonly IReadOnlyDictionary<string, ArchetypeBlend>
        ChampionOverrides =
            new Dictionary<string, ArchetypeBlend>(StringComparer.OrdinalIgnoreCase)
            {
                ["Senna"] = new(ChampionArchetype.Marksman, 0.60, ChampionArchetype.Support, 0.40),
                ["Pyke"] = new(ChampionArchetype.Assassin, 0.50, ChampionArchetype.Support, 0.50),
                ["Karma"] = new(ChampionArchetype.Mage, 0.50, ChampionArchetype.Support, 0.50),
                ["Seraphine"] = new(ChampionArchetype.Mage, 0.50, ChampionArchetype.Support, 0.50),
                ["TahmKench"] = new(ChampionArchetype.Tank, 0.70, ChampionArchetype.Support, 0.30),
                ["Rakan"] = new(ChampionArchetype.Support, 0.70, ChampionArchetype.Tank, 0.30),
                ["Bard"] = new(ChampionArchetype.Support, 0.70, ChampionArchetype.Mage, 0.30),
                ["Ivern"] = new(ChampionArchetype.Support, 0.60, ChampionArchetype.Mage, 0.40),
                ["Milio"] = new(ChampionArchetype.Support, 1.0),
                ["Lulu"] = new(ChampionArchetype.Support, 1.0),
                ["Janna"] = new(ChampionArchetype.Support, 1.0),
                ["Soraka"] = new(ChampionArchetype.Support, 1.0),
                ["Yuumi"] = new(ChampionArchetype.Support, 1.0),
                ["Nami"] = new(ChampionArchetype.Support, 1.0),
                ["Sona"] = new(ChampionArchetype.Support, 1.0)
            };

    internal int RetainedScoreCount => _smoothedScores.Count;

    public OverlaySnapshot Evaluate(LeagueSessionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        PrepareSessionState(frame);
        var snapshot = frame.Phase switch
        {
            LeaguePhase.ChampSelect => BuildChampSelectSnapshot(frame),
            LeaguePhase.InGame or LeaguePhase.Loading when frame.LivePlayers.Count > 0 =>
                BuildLiveSnapshot(frame),
            LeaguePhase.EndOfGame => OverlaySnapshot.Empty("對局已結束") with
            {
                Phase = LeaguePhase.EndOfGame,
                CapturedAt = frame.CapturedAt,
                Summary = "對局已結束"
            },
            _ => OverlaySnapshot.Empty(frame.StatusMessage) with
            {
                Phase = frame.Phase,
                CapturedAt = frame.CapturedAt,
                Summary = frame.StatusMessage ?? "等待遊戲資料"
            }
        };
        _lastPhase = frame.Phase;
        return snapshot;
    }

    public void Reset()
    {
        _smoothedScores.Clear();
        _lastPhase = LeaguePhase.None;
        _lastGameTimeSeconds = 0d;
    }

    private static OverlaySnapshot BuildChampSelectSnapshot(LeagueSessionFrame frame)
    {
        var teams = frame.ChampSelectMembers
            .GroupBy(member => member.Team)
            .OrderBy(group => group.Key)
            .Select(group => new OverlayTeam(
                group.Key,
                TeamName(group.Key),
                null,
                group.Select(member => new OverlayPlayer(
                    member.StableKey,
                    member.IsAnonymous ? "匿名玩家" : member.RiotId ?? "已識別玩家",
                    member.Team,
                    string.IsNullOrWhiteSpace(member.ChampionName) ? "尚未選擇" : member.ChampionName,
                    member.ChampionIconPath,
                    member.IsAnonymous,
                    null,
                    null,
                    null)).ToArray()))
            .ToArray();

        var visible = frame.ChampSelectMembers.Count(member => !member.IsAnonymous);
        var total = frame.ChampSelectMembers.Count;
        return new OverlaySnapshot(
            LeaguePhase.ChampSelect,
            frame.CapturedAt,
            "選角偵測中",
            $"{visible}/{Math.Max(total, 10)} 位玩家可見",
            frame.ActiveRiotId,
            null,
            null,
            null,
            null,
            teams,
            frame.StatusMessage);
    }

    private OverlaySnapshot BuildLiveSnapshot(LeagueSessionFrame frame)
    {
        var players = frame.LivePlayers;
        var minutes = Math.Max(frame.GameTimeSeconds / 60d, 1d / 60d);
        var teamKills = new Dictionary<int, int>();
        for (var index = 0; index < players.Count; index++)
        {
            var player = players[index];
            teamKills[player.Team] = teamKills.GetValueOrDefault(player.Team) + player.Kills;
        }

        var economy = new double[players.Count];
        var killShare = new double[players.Count];
        var participation = new double[players.Count];
        var survival = new double[players.Count];
        var kda = new double[players.Count];
        var levels = new double[players.Count];
        var creepRate = new double[players.Count];
        for (var index = 0; index < players.Count; index++)
        {
            var player = players[index];
            var teamKillCount = Math.Max(teamKills.GetValueOrDefault(player.Team), 1d);
            economy[index] = ItemGold(player);
            killShare[index] = player.Kills / teamKillCount;
            participation[index] = (player.Kills + player.Assists) / teamKillCount;
            survival[index] = -player.Deaths / minutes;
            kda[index] = (player.Kills + player.Assists) / Math.Max(player.Deaths, 1d);
            levels[index] = player.Level;
            creepRate[index] = player.CreepScore / minutes;
        }

        var rankBuffer = new RankedValue[players.Count];
        ReplaceWithPercentileRanks(economy, rankBuffer);
        ReplaceWithPercentileRanks(killShare, rankBuffer);
        ReplaceWithPercentileRanks(participation, rankBuffer);
        ReplaceWithPercentileRanks(survival, rankBuffer);
        ReplaceWithPercentileRanks(kda, rankBuffer);
        ReplaceWithPercentileRanks(levels, rankBuffer);
        ReplaceWithPercentileRanks(creepRate, rankBuffer);

        var confidenceValue = ConfidenceValue(frame);
        var confidence = ConfidenceLabel(confidenceValue);
        var overlayPlayers = new List<OverlayPlayer>(players.Count);

        for (var index = 0; index < players.Count; index++)
        {
            var metric = new MetricVector(
                economy[index],
                killShare[index],
                participation[index],
                survival[index],
                kda[index],
                0.70 * levels[index] + 0.30 * creepRate[index]);

            var rawScore = ResolveWeights(players[index]).Apply(metric) * 100d;
            var confidenceAdjusted = 50d + confidenceValue * (rawScore - 50d);
            var stableKey = players[index].StableKey;
            var smoothed = _smoothedScores.TryGetValue(stableKey, out var previous)
                ? previous + EmaAlpha * (confidenceAdjusted - previous)
                : confidenceAdjusted;
            smoothed = Math.Clamp(smoothed, 0d, 100d);
            _smoothedScores[stableKey] = smoothed;

            overlayPlayers.Add(new OverlayPlayer(
                players[index].StableKey,
                players[index].RiotId,
                players[index].Team,
                players[index].ChampionName,
                players[index].ChampionIconPath,
                false,
                Math.Round(smoothed, 1),
                ScoreLabel(smoothed),
                confidence));
        }

        PruneScoresToCurrentRoster(players);

        var teams = overlayPlayers
            .GroupBy(player => player.Team)
            .OrderBy(group => group.Key)
            .Select(group => new OverlayTeam(
                group.Key,
                TeamName(group.Key),
                Math.Round(group.Average(player => player.PerformanceScore ?? 50d), 1),
                group.ToArray()))
            .ToArray();

        var activeTeam = players.FirstOrDefault(player =>
            !string.IsNullOrWhiteSpace(frame.ActiveRiotId) &&
            string.Equals(player.RiotId, frame.ActiveRiotId, StringComparison.OrdinalIgnoreCase))?.Team;
        var orderedTeams = teams
            .Where(team => team.PerformanceScore.HasValue)
            .OrderByDescending(team => team.PerformanceScore)
            .ToArray();
        var leadingTeam = orderedTeams.Length > 1 ? orderedTeams[0].Team : (int?)null;
        var teamGap = orderedTeams.Length > 1
            ? Math.Round(Math.Abs(orderedTeams[0].PerformanceScore!.Value - orderedTeams[1].PerformanceScore!.Value), 1)
            : (double?)null;

        var summary = BuildTeamSummary(teams, activeTeam, leadingTeam, teamGap);
        return new OverlaySnapshot(
            LeaguePhase.InGame,
            frame.CapturedAt,
            "本場即時表現",
            summary,
            frame.ActiveRiotId,
            activeTeam,
            leadingTeam,
            teamGap,
            confidence,
            teams,
            frame.StatusMessage);
    }

    private static double ItemGold(RawPlayerState player) =>
        player.Items.Sum(item => Math.Max(item.Count, 1) * Math.Max(item.GoldValue, 0));

    private static ArchetypeWeights ResolveWeights(RawPlayerState player)
    {
        if (ChampionOverrides.TryGetValue(player.ChampionKey, out var blend))
        {
            return BlendWeights(blend);
        }

        var primary = ChampionArchetype.Fighter;
        ChampionArchetype? secondary = null;
        var foundPrimary = false;
        for (var index = 0; index < player.Archetypes.Count; index++)
        {
            var candidate = player.Archetypes[index];
            if (!foundPrimary)
            {
                primary = candidate;
                foundPrimary = true;
            }
            else if (candidate != primary)
            {
                secondary = candidate;
                break;
            }
        }

        return BlendWeights(secondary.HasValue
            ? new ArchetypeBlend(primary, 0.70, secondary.Value, 0.30)
            : new ArchetypeBlend(primary, 1.0));
    }

    private static ArchetypeWeights BlendWeights(ArchetypeBlend blend)
    {
        var primary = Weights[blend.Primary];
        if (!blend.Secondary.HasValue || blend.SecondaryWeight <= 0)
        {
            return primary;
        }

        var secondary = Weights[blend.Secondary.Value];
        var total = blend.PrimaryWeight + blend.SecondaryWeight;
        return new ArchetypeWeights(
            (primary.Economy * blend.PrimaryWeight + secondary.Economy * blend.SecondaryWeight) / total,
            (primary.KillShare * blend.PrimaryWeight + secondary.KillShare * blend.SecondaryWeight) / total,
            (primary.Participation * blend.PrimaryWeight + secondary.Participation * blend.SecondaryWeight) / total,
            (primary.Survival * blend.PrimaryWeight + secondary.Survival * blend.SecondaryWeight) / total,
            (primary.KdaEfficiency * blend.PrimaryWeight + secondary.KdaEfficiency * blend.SecondaryWeight) / total,
            (primary.Development * blend.PrimaryWeight + secondary.Development * blend.SecondaryWeight) / total);
    }

    internal static double[] PercentileRanksForTesting(IReadOnlyList<double> values)
    {
        var result = values.ToArray();
        ReplaceWithPercentileRanks(result, new RankedValue[result.Length]);
        return result;
    }

    private static void ReplaceWithPercentileRanks(double[] values, RankedValue[] buffer)
    {
        if (values.Length <= 1)
        {
            if (values.Length == 1)
            {
                values[0] = 0.5d;
            }

            return;
        }

        for (var index = 0; index < values.Length; index++)
        {
            buffer[index] = new RankedValue(values[index], index);
        }

        Array.Sort(buffer, 0, values.Length, RankedValueComparer.Instance);
        var lower = 0;
        var upper = 0;
        for (var sortedIndex = 0; sortedIndex < values.Length; sortedIndex++)
        {
            var value = buffer[sortedIndex].Value;
            while (lower < values.Length && buffer[lower].Value < value - PercentileEpsilon)
            {
                lower++;
            }

            upper = Math.Max(upper, lower);
            while (upper < values.Length && buffer[upper].Value <= value + PercentileEpsilon)
            {
                upper++;
            }

            var equal = upper - lower;
            values[buffer[sortedIndex].OriginalIndex] = Math.Clamp(
                (lower + 0.5d * Math.Max(equal - 1, 0)) / (values.Length - 1d),
                0d,
                1d);
        }
    }

    private void PrepareSessionState(LeagueSessionFrame frame)
    {
        var isLive = frame.Phase is LeaguePhase.Loading or LeaguePhase.InGame;
        var wasActiveSession = _lastPhase is LeaguePhase.ChampSelect or LeaguePhase.Loading or LeaguePhase.InGame;
        var gameClockRestarted = isLive &&
                                 _lastPhase is LeaguePhase.Loading or LeaguePhase.InGame &&
                                 frame.GameTimeSeconds + 30d < _lastGameTimeSeconds;
        if ((frame.Phase == LeaguePhase.ChampSelect && _lastPhase != LeaguePhase.ChampSelect) ||
            (isLive && !wasActiveSession) ||
            gameClockRestarted ||
            (!isLive && frame.Phase != LeaguePhase.ChampSelect && _smoothedScores.Count > 0))
        {
            _smoothedScores.Clear();
        }

        _lastGameTimeSeconds = isLive ? Math.Max(frame.GameTimeSeconds, 0d) : 0d;
    }

    private void PruneScoresToCurrentRoster(IReadOnlyList<RawPlayerState> players)
    {
        if (_smoothedScores.Count <= players.Count)
        {
            return;
        }

        var currentKeys = new HashSet<string>(players.Select(player => player.StableKey), StringComparer.Ordinal);
        foreach (var key in _smoothedScores.Keys.Where(key => !currentKeys.Contains(key)).ToArray())
        {
            _smoothedScores.Remove(key);
        }
    }

    private readonly record struct ArchetypeBlend(
        ChampionArchetype Primary,
        double PrimaryWeight,
        ChampionArchetype? Secondary = null,
        double SecondaryWeight = 0);

    private readonly record struct RankedValue(double Value, int OriginalIndex);

    private sealed class RankedValueComparer : IComparer<RankedValue>
    {
        public static RankedValueComparer Instance { get; } = new();

        public int Compare(RankedValue left, RankedValue right)
        {
            var byValue = left.Value.CompareTo(right.Value);
            return byValue != 0 ? byValue : left.OriginalIndex.CompareTo(right.OriginalIndex);
        }
    }

    internal static double ConfidenceValue(LeagueSessionFrame frame)
    {
        var seconds = Math.Max(frame.GameTimeSeconds, 0d);
        var isAram = frame.QueueId is 450 or 2400 ||
                     frame.GameMode.Contains("ARAM", StringComparison.OrdinalIgnoreCase) ||
                     frame.GameMode.Contains("KIWI", StringComparison.OrdinalIgnoreCase);
        var start = isAram ? 120d : frame.GameMode.Contains("CLASSIC", StringComparison.OrdinalIgnoreCase) ? 240d : 180d;
        var end = isAram ? 480d : frame.GameMode.Contains("CLASSIC", StringComparison.OrdinalIgnoreCase) ? 840d : 600d;
        return Math.Clamp((seconds - start) / (end - start), 0d, 1d);
    }

    private static PerformanceConfidence ConfidenceLabel(double value) =>
        value < 0.35d
            ? PerformanceConfidence.Low
            : value < 0.75d
                ? PerformanceConfidence.Medium
                : PerformanceConfidence.High;

    private static string ScoreLabel(double score) =>
        score >= 75d
            ? "本場較高"
            : score >= 60d
                ? "本場偏高"
                : score >= 40d
                    ? "本場接近"
                    : score >= 25d
                        ? "本場偏低"
                        : "本場較低";

    private static string BuildTeamSummary(
        IReadOnlyList<OverlayTeam> teams,
        int? activeTeam,
        int? leadingTeam,
        double? gap)
    {
        if (teams.Count < 2 || !gap.HasValue || !leadingTeam.HasValue)
        {
            return "等待完整隊伍資料";
        }

        if (gap.Value < 3d)
        {
            return $"雙方接近 · 差 {gap.Value:0.0}";
        }

        var prefix = activeTeam.HasValue
            ? leadingTeam == activeTeam ? "我方本場指標較高" : "我方本場指標較低"
            : $"{TeamName(leadingTeam.Value)}本場指標較高";
        return $"{prefix} · 差 {gap.Value:0.0}";
    }

    private static string TeamName(int team) => team switch
    {
        100 => "藍方",
        200 => "紅方",
        1 => "藍方",
        2 => "紅方",
        _ => $"隊伍 {team}"
    };
}
