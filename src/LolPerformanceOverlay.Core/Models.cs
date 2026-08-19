namespace LolPerformanceOverlay.Core;

public enum LeaguePhase
{
    None,
    Lobby,
    Matchmaking,
    ChampSelect,
    Loading,
    InGame,
    EndOfGame
}

public enum OverlayMode
{
    Dot,
    Compact,
    Expanded
}

public enum PerformanceConfidence
{
    Low,
    Medium,
    High
}

public enum ChampionArchetype
{
    Marksman,
    Assassin,
    Mage,
    Fighter,
    Tank,
    Support
}

public sealed record ChampionDescriptor(
    int Id,
    string Key,
    string Name,
    IReadOnlyList<ChampionArchetype> Archetypes,
    string? IconPath = null);

public sealed record ChampSelectMember(
    string StableKey,
    string? RiotId,
    int Team,
    int ChampionId,
    string ChampionName,
    string? ChampionIconPath,
    bool IsAnonymous,
    int? PickOrder = null);

public sealed record RawItemState(int ItemId, int Count, int GoldValue);

/// <summary>
/// Already-formatted official rank display data for one row of the Expanded panel --
/// following the same convention as <see cref="OverlayPlayer.PerformanceLabel"/>, the
/// formatting (tier letter, division digit) happens once in Core, never in the WPF adapter.
/// Only the short code exists today; it is a positional record with room for trailing
/// optional parameters so a later status-wording field and a later tooltip/source/fetched-at
/// group can be added without reshaping callers, the same way <see cref="OverlayPlayer"/>
/// itself grew <c>PickOrder</c> and <c>ItemGold</c>.
/// </summary>
public sealed record OfficialRankDisplay(string ShortCode);

public sealed record RawPlayerState(
    string StableKey,
    string RiotId,
    int Team,
    string ChampionKey,
    string ChampionName,
    string? ChampionIconPath,
    IReadOnlyList<ChampionArchetype> Archetypes,
    int Kills,
    int Deaths,
    int Assists,
    int CreepScore,
    int Level,
    IReadOnlyList<RawItemState> Items);

public sealed record LeagueSessionFrame(
    LeaguePhase Phase,
    DateTimeOffset CapturedAt,
    double GameTimeSeconds,
    string GameMode,
    int QueueId,
    string? ActiveRiotId,
    IReadOnlyList<ChampSelectMember> ChampSelectMembers,
    IReadOnlyList<RawPlayerState> LivePlayers,
    string? StatusMessage = null,
    string? PlatformRegion = null);

public sealed record OverlayPlayer(
    string StableKey,
    string DisplayName,
    int Team,
    string ChampionName,
    string? ChampionIconPath,
    bool IsAnonymous,
    double? PerformanceScore,
    string? PerformanceLabel,
    PerformanceConfidence? Confidence,
    // Champ select only: this cell's place in the pick sequence, so a position swap
    // does not lose who picked before whom. Null when the client did not report it.
    int? PickOrder = null,
    // Live game only: the summed shop value of the equipment this player is carrying.
    // Derived from what the in-game scoreboard already shows plus static Data Dragon
    // prices; it is not the player's unspent gold, which the client does not expose
    // for anyone but the local player. Aggregate only -- never the raw array.
    int? ItemGold = null,
    // Live game only: this player's official rank in the current queue, already formatted
    // for display. Null until the asynchronous history lookup resolves (see
    // OfficialRankAttachment.Attach in the Presentation namespace) and always null for
    // anonymous players -- the lookup never runs for them. Champ select never sets it.
    OfficialRankDisplay? OfficialRank = null);

public sealed record OverlayTeam(
    int Team,
    string DisplayName,
    double? PerformanceScore,
    IReadOnlyList<OverlayPlayer> Players);

public sealed record OverlaySnapshot(
    LeaguePhase Phase,
    DateTimeOffset CapturedAt,
    string Header,
    string Summary,
    string? ActiveRiotId,
    int? ActiveTeam,
    int? LeadingTeam,
    double? TeamGap,
    PerformanceConfidence? Confidence,
    IReadOnlyList<OverlayTeam> Teams,
    string? StatusMessage = null)
{
    public static OverlaySnapshot Empty(string? status = null) =>
        new(
            LeaguePhase.None,
            DateTimeOffset.Now,
            "LoL 即時表現",
            "等待 League Client",
            null,
            null,
            null,
            null,
            null,
            Array.Empty<OverlayTeam>(),
            status);
}

internal readonly record struct MetricVector(
    double Economy,
    double KillShare,
    double Participation,
    double Survival,
    double KdaEfficiency,
    double Development);

internal readonly record struct ArchetypeWeights(
    double Economy,
    double KillShare,
    double Participation,
    double Survival,
    double KdaEfficiency,
    double Development)
{
    public double Apply(MetricVector metric) =>
        Economy * metric.Economy +
        KillShare * metric.KillShare +
        Participation * metric.Participation +
        Survival * metric.Survival +
        KdaEfficiency * metric.KdaEfficiency +
        Development * metric.Development;
}
