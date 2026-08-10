namespace LolPerformanceOverlay.Core;

public interface ILeagueSessionSource : IAsyncDisposable
{
    IAsyncEnumerable<LeagueSessionFrame> WatchAsync(CancellationToken cancellationToken);
}

public interface IReplaySource : ILeagueSessionSource
{
    string ReplayName { get; }
}

public interface IStaticGameDataProvider
{
    Task InitializeAsync(CancellationToken cancellationToken);
    ChampionDescriptor ResolveChampion(string championName, int championId = 0);
    ValueTask<string?> EnsureChampionIconAsync(ChampionDescriptor champion, CancellationToken cancellationToken);
    int GetItemGoldValue(int itemId);
}

public interface IPerformanceScorer
{
    OverlaySnapshot Evaluate(LeagueSessionFrame frame);
    void Reset();
}
