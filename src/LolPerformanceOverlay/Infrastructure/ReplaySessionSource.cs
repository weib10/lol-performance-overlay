using System.Runtime.CompilerServices;
using LolPerformanceOverlay.Core;

namespace LolPerformanceOverlay.Infrastructure;

public sealed class ReplaySessionSource : IReplaySource
{
    private readonly IStaticGameDataProvider _staticData;
    private readonly bool _loop;

    public ReplaySessionSource(IStaticGameDataProvider staticData, bool loop = true)
    {
        _staticData = staticData;
        _loop = loop;
    }

    public string ReplayName => "TW2 ARAM overlay fixture";

    public async IAsyncEnumerable<LeagueSessionFrame> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        do
        {
            for (var index = 0; index < 3; index++)
            {
                yield return await ChampSelectFrameAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            foreach (var stage in new[]
                     {
                         new ReplayStage(60, 0),
                         new ReplayStage(240, 1),
                         new ReplayStage(420, 2),
                         new ReplayStage(600, 3)
                     })
            {
                for (var repeat = 0; repeat < 4; repeat++)
                {
                    yield return await LiveFrameAsync(stage, cancellationToken);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
            }

            yield return new LeagueSessionFrame(
                LeaguePhase.EndOfGame,
                DateTimeOffset.Now,
                600,
                "ARAM",
                450,
                "測試玩家01#TEST",
                Array.Empty<ChampSelectMember>(),
                Array.Empty<RawPlayerState>(),
                "Replay 完成",
                "tw2");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        } while (_loop && !cancellationToken.IsCancellationRequested);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<LeagueSessionFrame> ChampSelectFrameAsync(CancellationToken cancellationToken)
    {
        var specs = Roster;
        var descriptors = new ChampionDescriptor[specs.Count];
        var iconRequests = new ValueTask<string?>[specs.Count];
        for (var index = 0; index < specs.Count; index++)
        {
            descriptors[index] = _staticData.ResolveChampion(specs[index].ChampionKey);
            iconRequests[index] = _staticData.EnsureChampionIconAsync(descriptors[index], cancellationToken);
        }

        var members = new List<ChampSelectMember>(specs.Count);
        for (var index = 0; index < specs.Count; index++)
        {
            var spec = specs[index];
            var descriptor = descriptors[index];
            var icon = await iconRequests[index];
            var anonymous = index is 7 or 9;
            members.Add(new ChampSelectMember(
                $"cell-{index}",
                anonymous ? null : spec.RiotId,
                spec.Team,
                descriptor.Id,
                descriptor.Name,
                icon,
                anonymous,
                // A real draft alternates sides and does not follow roster order, so give
                // Replay the standard 1-2-2-2-2-1 sequence. Otherwise the pick-order badge
                // cannot be checked without a live champ select.
                DraftPickOrder.ElementAtOrDefault(index) is > 0 and var order ? order : null));
        }

        return new LeagueSessionFrame(
            LeaguePhase.ChampSelect,
            DateTimeOffset.Now,
            0,
            string.Empty,
            450,
            "測試玩家01#TEST",
            members,
            Array.Empty<RawPlayerState>(),
            "離線 Replay",
            "tw2");
    }

    private async Task<LeagueSessionFrame> LiveFrameAsync(
        ReplayStage stage,
        CancellationToken cancellationToken)
    {
        var specs = Roster;
        var descriptors = new ChampionDescriptor[specs.Count];
        var iconRequests = new ValueTask<string?>[specs.Count];
        for (var index = 0; index < specs.Count; index++)
        {
            descriptors[index] = _staticData.ResolveChampion(specs[index].ChampionKey);
            iconRequests[index] = _staticData.EnsureChampionIconAsync(descriptors[index], cancellationToken);
        }

        var players = new List<RawPlayerState>(specs.Count);
        for (var index = 0; index < specs.Count; index++)
        {
            var spec = specs[index];
            var descriptor = descriptors[index];
            var icon = await iconRequests[index];
            var stat = spec.Stages[stage.Index];
            players.Add(new RawPlayerState(
                $"{spec.Team}:{spec.RiotId}",
                spec.RiotId,
                spec.Team,
                descriptor.Key,
                descriptor.Name,
                icon,
                descriptor.Archetypes,
                stat.Kills,
                stat.Deaths,
                stat.Assists,
                stat.CreepScore,
                stat.Level,
                [new RawItemState(900000 + index, 1, stat.ItemGold)]));
        }

        return new LeagueSessionFrame(
            LeaguePhase.InGame,
            DateTimeOffset.Now,
            stage.Seconds,
            "ARAM",
            450,
            "測試玩家01#TEST",
            Array.Empty<ChampSelectMember>(),
            players,
            "離線 Replay",
            "tw2");
    }

    /// Standard draft turn order by roster slot: blue 1, red 2-3, blue 4-5, red 6-7,
    /// blue 8-9, red 10. Indexed by the same order as <see cref="Roster"/>.
    private static readonly IReadOnlyList<int> DraftPickOrder =
        [1, 4, 5, 8, 9, 2, 3, 6, 7, 10];

    private static readonly IReadOnlyList<ReplayPlayerSpec> Roster =
    [
        new(
            "測試玩家01#TEST",
            100,
            "Mel",
            [S(0, 0, 0, 0, 1, 0), S(2, 2, 9, 12, 7, 4200), S(5, 3, 16, 24, 11, 7800), S(8, 4, 23, 38, 15, 11500)]),
        new(
            "測試玩家02#TEST",
            100,
            "Khazix",
            [S(0, 0, 0, 0, 1, 0), S(7, 2, 3, 8, 8, 5200), S(12, 4, 8, 18, 12, 8700), S(17, 5, 13, 30, 16, 13000)]),
        new(
            "測試玩家03#TEST",
            100,
            "Rakan",
            [S(0, 0, 0, 0, 1, 0), S(0, 4, 11, 2, 6, 3100), S(1, 5, 22, 4, 10, 5600), S(2, 6, 31, 7, 14, 8000)]),
        new(
            "測試玩家04#TEST",
            100,
            "Teemo",
            [S(0, 0, 0, 0, 1, 0), S(3, 1, 8, 14, 7, 4500), S(7, 3, 15, 29, 11, 8000), S(11, 4, 22, 45, 15, 11700)]),
        new(
            "測試玩家05#TEST",
            100,
            "Gangplank",
            [S(0, 0, 0, 0, 1, 0), S(1, 3, 5, 21, 7, 3900), S(4, 6, 10, 44, 11, 7200), S(7, 8, 15, 67, 15, 10600)]),
        new(
            "測試玩家06#TEST",
            200,
            "Ezreal",
            [S(0, 0, 0, 0, 1, 0), S(6, 2, 4, 19, 8, 5000), S(10, 4, 11, 38, 12, 8500), S(14, 6, 18, 60, 16, 12500)]),
        new(
            "測試玩家07#TEST",
            200,
            "Leona",
            [S(0, 0, 0, 0, 1, 0), S(0, 3, 10, 1, 6, 3000), S(1, 6, 18, 3, 10, 5400), S(2, 9, 26, 5, 14, 7600)]),
        new(
            "測試玩家08#TEST",
            200,
            "Morgana",
            [S(0, 0, 0, 0, 1, 0), S(4, 2, 7, 10, 7, 4300), S(7, 4, 15, 22, 11, 7700), S(10, 7, 22, 34, 15, 10900)]),
        new(
            "測試玩家09#TEST",
            200,
            "Senna",
            [S(0, 0, 0, 0, 1, 0), S(2, 3, 9, 6, 7, 4000), S(5, 5, 17, 13, 11, 7300), S(8, 7, 25, 22, 15, 10300)]),
        new(
            "測試玩家10#TEST",
            200,
            "Trundle",
            [S(0, 0, 0, 0, 1, 0), S(3, 5, 6, 17, 7, 4100), S(6, 8, 12, 34, 11, 7400), S(9, 11, 18, 52, 15, 10600)])
    ];

    private static ReplayStats S(
        int kills,
        int deaths,
        int assists,
        int creep,
        int level,
        int itemGold) =>
        new(kills, deaths, assists, creep, level, itemGold);

    private sealed record ReplayStage(double Seconds, int Index);

    private sealed record ReplayPlayerSpec(
        string RiotId,
        int Team,
        string ChampionKey,
        IReadOnlyList<ReplayStats> Stages);

    private sealed record ReplayStats(
        int Kills,
        int Deaths,
        int Assists,
        int CreepScore,
        int Level,
        int ItemGold);
}
