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
    bool IsAnonymous);

public sealed record RawItemState(int ItemId, int Count, int GoldValue);

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
    PerformanceConfidence? Confidence);

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

internal sealed record MetricVector(
    double Economy,
    double KillShare,
    double Participation,
    double Survival,
    double KdaEfficiency,
    double Development);

internal sealed record ArchetypeWeights(
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
