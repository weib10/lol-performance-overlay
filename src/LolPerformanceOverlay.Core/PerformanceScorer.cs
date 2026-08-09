using System.Collections.Concurrent;

namespace LolPerformanceOverlay.Core;

public sealed class PerformanceScorer : IPerformanceScorer
{
    private const double EmaAlpha = 0.35;
    private readonly ConcurrentDictionary<string, double> _smoothedScores = new(StringComparer.Ordinal);

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

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<(ChampionArchetype Type, double Weight)>>
        ChampionOverrides =
            new Dictionary<string, IReadOnlyList<(ChampionArchetype, double)>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Senna"] = [(ChampionArchetype.Marksman, 0.60), (ChampionArchetype.Support, 0.40)],
                ["Pyke"] = [(ChampionArchetype.Assassin, 0.50), (ChampionArchetype.Support, 0.50)],
                ["Karma"] = [(ChampionArchetype.Mage, 0.50), (ChampionArchetype.Support, 0.50)],
                ["Seraphine"] = [(ChampionArchetype.Mage, 0.50), (ChampionArchetype.Support, 0.50)],
                ["TahmKench"] = [(ChampionArchetype.Tank, 0.70), (ChampionArchetype.Support, 0.30)],
                ["Rakan"] = [(ChampionArchetype.Support, 0.70), (ChampionArchetype.Tank, 0.30)],
                ["Bard"] = [(ChampionArchetype.Support, 0.70), (ChampionArchetype.Mage, 0.30)],
                ["Ivern"] = [(ChampionArchetype.Support, 0.60), (ChampionArchetype.Mage, 0.40)],
                ["Milio"] = [(ChampionArchetype.Support, 1.0)],
                ["Lulu"] = [(ChampionArchetype.Support, 1.0)],
                ["Janna"] = [(ChampionArchetype.Support, 1.0)],
                ["Soraka"] = [(ChampionArchetype.Support, 1.0)],
                ["Yuumi"] = [(ChampionArchetype.Support, 1.0)],
                ["Nami"] = [(ChampionArchetype.Support, 1.0)],
                ["Sona"] = [(ChampionArchetype.Support, 1.0)]
            };

    public OverlaySnapshot Evaluate(LeagueSessionFrame frame)
    {
        return frame.Phase switch
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
    }

    public void Reset() => _smoothedScores.Clear();

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
        var teamKills = players
            .GroupBy(player => player.Team)
            .ToDictionary(group => group.Key, group => group.Sum(player => player.Kills));

        var economy = players.Select(ItemGold).ToArray();
        var killShare = players.Select(player =>
            player.Kills / Math.Max(teamKills.GetValueOrDefault(player.Team), 1d)).ToArray();
        var participation = players.Select(player =>
            (player.Kills + player.Assists) / Math.Max(teamKills.GetValueOrDefault(player.Team), 1d)).ToArray();
        var survival = players.Select(player => -player.Deaths / minutes).ToArray();
        var kda = players.Select(player =>
            (player.Kills + player.Assists) / Math.Max(player.Deaths, 1d)).ToArray();
        var levels = players.Select(player => (double)player.Level).ToArray();
        var creepRate = players.Select(player => player.CreepScore / minutes).ToArray();

        var confidenceValue = ConfidenceValue(frame);
        var confidence = ConfidenceLabel(confidenceValue);
        var overlayPlayers = new List<OverlayPlayer>(players.Count);

        for (var index = 0; index < players.Count; index++)
        {
            var metric = new MetricVector(
                Percentile(economy, economy[index]),
                Percentile(killShare, killShare[index]),
                Percentile(participation, participation[index]),
                Percentile(survival, survival[index]),
                Percentile(kda, kda[index]),
                0.70 * Percentile(levels, levels[index]) + 0.30 * Percentile(creepRate, creepRate[index]));

            var rawScore = ResolveWeights(players[index]).Apply(metric) * 100d;
            var confidenceAdjusted = 50d + confidenceValue * (rawScore - 50d);
            var smoothed = _smoothedScores.AddOrUpdate(
                players[index].StableKey,
                confidenceAdjusted,
                (_, previous) => previous + EmaAlpha * (confidenceAdjusted - previous));
            smoothed = Math.Clamp(smoothed, 0d, 100d);

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
        IReadOnlyList<(ChampionArchetype Type, double Weight)> components;
        if (ChampionOverrides.TryGetValue(player.ChampionKey, out var overridden))
        {
            components = overridden;
        }
        else
        {
            var tags = player.Archetypes
                .Distinct()
                .Take(2)
                .ToArray();
            components = tags.Length switch
            {
                0 => [(ChampionArchetype.Fighter, 1d)],
                1 => [(tags[0], 1d)],
                _ => [(tags[0], 0.70d), (tags[1], 0.30d)]
            };
        }

        var total = components.Sum(component => component.Weight);
        return new ArchetypeWeights(
            components.Sum(component => Weights[component.Type].Economy * component.Weight) / total,
            components.Sum(component => Weights[component.Type].KillShare * component.Weight) / total,
            components.Sum(component => Weights[component.Type].Participation * component.Weight) / total,
            components.Sum(component => Weights[component.Type].Survival * component.Weight) / total,
            components.Sum(component => Weights[component.Type].KdaEfficiency * component.Weight) / total,
            components.Sum(component => Weights[component.Type].Development * component.Weight) / total);
    }

    internal static double Percentile(IReadOnlyList<double> values, double value)
    {
        if (values.Count <= 1)
        {
            return 0.5d;
        }

        const double epsilon = 0.000001d;
        var less = values.Count(candidate => candidate < value - epsilon);
        var equal = values.Count(candidate => Math.Abs(candidate - value) <= epsilon);
        return Math.Clamp((less + 0.5d * Math.Max(equal - 1, 0)) / (values.Count - 1d), 0d, 1d);
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
            ? "強勢"
            : score >= 60d
                ? "領先"
                : score >= 40d
                    ? "持平"
                    : score >= 25d
                        ? "落後"
                        : "明顯落後";

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
            ? leadingTeam == activeTeam ? "我方領先" : "我方落後"
            : $"{TeamName(leadingTeam.Value)}領先";
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
