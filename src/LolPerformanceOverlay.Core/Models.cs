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
/// formatting (tier letter, division digit, and now plain-language status wording) happens
/// once in Core, never in the WPF adapter.
/// <see cref="ShortCode"/> is deliberately terse -- the rank column is only 25px wide (see
/// OverlayWindow.CreatePlayerRow) -- and collapses many different states into a handful of
/// visual ones so ten rows of a distinct, obscure marker never reads as the app being broken:
/// a real rank code ("D4", "GM"), "未" for a player with no rank in the current queue, Solo,
/// or Flex, or a neutral "—" for every lookup failure. A trailing "*" on a non-empty code
/// means the data is stale: a second, non-colour signal, because AGENTS.md forbids marking
/// state by colour alone.
/// <see cref="IsFromDifferentQueue"/> is a third, independent non-colour signal: true only
/// when the current queue itself has a ranked ladder (Solo or Flex) and the resolved rank
/// above came from the *other* one -- e.g. a Flex rank shown while playing Solo, because the
/// player has no Solo rank of their own. OverlayWindow renders it as a dotted underline on the
/// rank cell (a shape distinction, not a colour one) so a fallback rank is never mistaken for
/// a genuine same-queue rank sitting next to it on the same board. It is deliberately false
/// for a queue with no ladder of its own (e.g. ARAM): every rank shown there is a fallback by
/// construction, so a mark on every row would be the same one-glyph-for-everything clutter
/// issue #8 already collapsed away -- the tooltip still names the true queue in that case, the
/// row just does not carry a mark for it. See OfficialRankAttachment.FormatRank.
/// <see cref="StatusText"/> is the fuller, friend-facing sentence behind that marker and stays
/// fully distinct per state even where ShortCode does not -- empty only for a fresh resolved
/// rank from the current queue (or a same-source fallback in a no-ladder queue), which needs
/// no further explanation.
/// <see cref="TooltipText"/> is the full row tooltip's official-rank block (issue #9): full
/// tier name, LP when reported, the queue the rank belongs to, the source's display name, the
/// fetch time, an explicit note when that queue is not the one currently being played, and
/// staleness stated in words when <see cref="IsStale"/> -- composed once here in Core, never
/// in the WPF adapter, so it is an observable string tests can assert directly. It always ends
/// with an explicit sentence separating this rank (Riot's official data) from the row's score
/// (this program's own reading of the current game), because that is exactly the distinction a
/// player must never be able to blend together -- see AGENTS.md rule 9.
/// It is a positional record with room for further trailing optional parameters so it can keep
/// growing without reshaping callers, the same way <see cref="OverlayPlayer"/> itself grew
/// <c>PickOrder</c> and <c>ItemGold</c>.
/// </summary>
public sealed record OfficialRankDisplay(
    string ShortCode,
    string StatusText = "",
    bool IsStale = false,
    string TooltipText = "",
    bool IsFromDifferentQueue = false);

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
