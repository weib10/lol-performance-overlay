namespace LolPerformanceOverlay.Core.Presentation;

/// <summary>
/// Joins the latest fetched official ranks onto a snapshot's players. This is the seam
/// between the asynchronous, IO-bound history lookup (HistoricalProfileCoordinator) and
/// the synchronous diff/reduce pipeline (VisibleSnapshot, OverlayUpdateReducer): everything
/// here is pure, in-memory computation over data that has already been fetched, so it can
/// run on the session loop thread -- or a late-arriving lookup's continuation -- without
/// blocking either, and it can re-run against the same inputs any number of times with no
/// side effects.
/// </summary>
public static class OfficialRankAttachment
{
    // The rank column is 25px wide (see OverlayWindow.CreatePlayerRow) and the row is a fixed
    // 34px tall, so the cell cannot carry a sentence, or even a distinct word per failure
    // reason -- ten near-identical rows of an obscure single character (one per player, since
    // these failures are almost always roster-wide: no key, offline, quota gone) reads as the
    // app being broken, not as information. So the CELL collapses to just three visual states
    // -- a resolved rank code, "未" for a player with no rank in the current queue, Solo, or
    // Flex, or a neutral "nothing to show" marker for every failure -- while every state keeps
    // its own distinct, friend-facing sentence in OfficialRankDisplay.StatusText for issue #9's
    // tooltip to read. A resolved rank code can carry a fourth, orthogonal signal on top of
    // these three -- OfficialRankDisplay.IsFromDifferentQueue, rendered as a dotted underline
    // -- when it is a Solo/Flex fallback shown in the *other* ranked queue's game; see
    // FormatRank below.
    private const string StaleSuffix = "*";
    private const string FailureMarker = "—";

    // The one sentence that makes AGENTS.md rule 9 (產品誠實性) unmistakable in the row
    // tooltip (see OverlayWindow.UpdateRowTooltip, which appends TooltipText beneath the
    // existing name/champion/score block): the rank above this line is Riot's own data, the
    // score elsewhere in the same tooltip is this program's own reading of the current game,
    // and the two are never combined into one number. Appended to every state's TooltipText --
    // including every failure -- so a missing or stale rank can never read as "folded into the
    // score instead". No MMR/ELO/win-rate wording, ever; see
    // NoTooltipOrStatusTextEverMentionsMmrEloOrWinRateWording.
    private const string HonestyNote =
        "牌位是 Riot 官方資料，分數是本場相對表現，兩者分開呈現，不會合併或換算成單一數值。";

    /// <summary>
    /// Returns a snapshot with each visible, non-anonymous player's <see cref="OverlayPlayer.OfficialRank"/>
    /// set from <paramref name="profiles"/> where a match exists, joined by
    /// <see cref="OverlayPlayer.StableKey"/> against <see cref="RevealedPlayerIdentity.StableKey"/> --
    /// never by display name or Riot ID text. Every matched entry now produces a display, not
    /// just the ones with a resolved rank: unranked and every lookup failure each get a display
    /// (see <see cref="Describe"/>), so a row is never blank once a lookup result exists for it.
    /// When nothing would change, the exact same <paramref name="snapshot"/> instance is
    /// returned so callers never manufacture a false diff.
    /// </summary>
    public static OverlaySnapshot Attach(OverlaySnapshot snapshot, HistoricalProfilesResult? profiles)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (profiles is null || profiles.Entries.Count == 0)
        {
            return snapshot;
        }

        var displaysByStableKey = BuildDisplayLookup(profiles);
        if (displaysByStableKey.Count == 0)
        {
            return snapshot;
        }

        var teamsChanged = false;
        var updatedTeams = new OverlayTeam[snapshot.Teams.Count];
        for (var teamIndex = 0; teamIndex < snapshot.Teams.Count; teamIndex++)
        {
            var team = snapshot.Teams[teamIndex];
            var updatedTeam = AttachToTeam(team, displaysByStableKey);
            updatedTeams[teamIndex] = updatedTeam;
            teamsChanged |= !ReferenceEquals(updatedTeam, team);
        }

        return teamsChanged ? snapshot with { Teams = updatedTeams } : snapshot;
    }

    private static OverlayTeam AttachToTeam(
        OverlayTeam team,
        IReadOnlyDictionary<string, OfficialRankDisplay> displaysByStableKey)
    {
        var playersChanged = false;
        var updatedPlayers = new OverlayPlayer[team.Players.Count];
        for (var playerIndex = 0; playerIndex < team.Players.Count; playerIndex++)
        {
            var player = team.Players[playerIndex];
            // Anonymous seats never get a RevealedPlayerIdentity in the first place (see
            // App.TryCreateRevealedIdentity), so this lookup should never hit for one -- the
            // explicit check just keeps the rule true even if a StableKey were ever to collide.
            if (!player.IsAnonymous &&
                displaysByStableKey.TryGetValue(player.StableKey, out var display) &&
                display != player.OfficialRank)
            {
                updatedPlayers[playerIndex] = player with { OfficialRank = display };
                playersChanged = true;
            }
            else
            {
                updatedPlayers[playerIndex] = player;
            }
        }

        return playersChanged ? team with { Players = updatedPlayers } : team;
    }

    private static Dictionary<string, OfficialRankDisplay> BuildDisplayLookup(HistoricalProfilesResult profiles)
    {
        // A stale-roster entry (an identity no longer on the current roster) simply never
        // matches any player's StableKey below -- Attach does not need its own roster-generation
        // check, the join itself already refuses to attach it to a new occupant.
        var lookup = new Dictionary<string, OfficialRankDisplay>(StringComparer.Ordinal);
        foreach (var entry in profiles.Entries)
        {
            lookup[entry.Identity.StableKey] = Describe(entry);
        }

        return lookup;
    }

    /// <summary>
    /// Every <see cref="HistoricalProfileEntry"/> becomes a display, never a skip: a resolved
    /// rank, an honest "no rank to show, and here is why" for unranked/failure, or a stale
    /// marker layered on top of either of those. The split below mirrors the entry's own
    /// invariants -- <see cref="HistoricalProfileEntry.WithProfile"/> only allows
    /// Available/Partial/Stale, <see cref="HistoricalProfileEntry.Failure"/> only allows the
    /// other eight -- so whether Profile is null is itself the reliable signal, not Availability
    /// read on its own.
    /// A resolved profile with no <see cref="HistoricalProfile.OfficialRank"/> always means the
    /// same thing now, regardless of which queue is being played:
    /// RiotHistoricalProfileTransport.FetchAsync searches the queue actually being played (when
    /// it has a ranked ladder of its own), then Solo, then Flex, before giving up -- so "no
    /// rank" here means the player genuinely holds no Solo or Flex rank, full stop. A queue
    /// with no ladder of its own (ARAM and the like) no longer gets a different reading the way
    /// it used to: there is no ladder-specific rank left to be missing, only the two real ones,
    /// and this profile already reflects that search having come up empty.
    /// </summary>
    private static OfficialRankDisplay Describe(HistoricalProfileEntry entry)
    {
        var isStale = entry.Availability == HistoricalProfileAvailability.Stale;
        if (entry.Profile?.OfficialRank is { } rank)
        {
            return FormatRank(entry.Profile, rank, isStale);
        }

        if (entry.Profile is not null)
        {
            return Unranked(entry.Profile, isStale);
        }

        // Stale never reaches here -- HistoricalProfileEntry.Failure's invariant forbids it --
        // so a failure entry is never marked stale; there is no cached data behind it to be old.
        return Failure(entry.Availability);
    }

    // "D4", "E2", "G1" -- tier initial plus division digit, the shorthand players already
    // use for themselves. The three apex tiers have no division the way I-IV mean it for
    // everyone else, so their code is the tier letters alone.
    private static OfficialRankDisplay FormatRank(HistoricalProfile profile, OfficialRank rank, bool isStale)
    {
        var tierCode = TierCode(rank.Tier);
        var shortCode = IsApexTier(rank.Tier) ? tierCode : WithDivision(tierCode, rank.Division);
        // The rank's own queue (rank.Queue) can now legitimately differ from the queue actually
        // being played (profile.Queue) -- RiotHistoricalProfileTransport falls back to Solo or
        // Flex when the player has no rank in the current queue. queueMismatch drives the
        // tooltip's explicit note unconditionally (a player must always be able to see which
        // queue a rank came from); isFromDifferentQueue additionally gates the row cell's mark
        // and StatusText sentence to only the case that could actually be mistaken for a
        // same-queue rank -- a ranked current queue (Solo or Flex) showing the other one's
        // rank. A no-ladder current queue (ARAM) never sets it: every rank shown there is a
        // fallback by construction, so a mark on every row would be pure clutter, not
        // information -- the tooltip still names the true queue either way.
        var queueMismatch = rank.Queue.QueueId != profile.Queue.QueueId;
        var isFromDifferentQueue = queueMismatch && profile.Queue.IsRankedLadder;
        var crossQueueNote = queueMismatch ? CrossQueueNote(rank.Queue, profile.Queue) : null;
        // A fresh, same-queue rank needs no further explanation -- the code already says
        // everything. Staleness and a cross-queue fallback each add their own plain-language
        // sentence (and combine when both are true), so neither fact is ever left implicit in
        // a marker alone.
        var statusText = isStale
            ? (isFromDifferentQueue
                ? $"顯示的是較舊的快取牌位，不是最新資料；{crossQueueNote}"
                : "顯示的是較舊的快取牌位，不是最新資料")
            : (isFromDifferentQueue ? crossQueueNote! : string.Empty);
        var tooltipText = BuildTooltipText($"官方牌位：{FullRankText(rank)}", profile, isStale, crossQueueNote);
        return new OfficialRankDisplay(
            WithStaleSuffix(shortCode, isStale),
            statusText,
            isStale,
            tooltipText,
            isFromDifferentQueue);
    }

    // "這是彈性積分的牌位，不是單雙排的牌位" -- names the queue the rank actually belongs to and
    // says plainly that it is not the queue currently being played, so a fallback rank can never
    // be read as this game's own. Used both for the tooltip (always, on any mismatch) and for
    // StatusText (only when the row cell also carries the mark -- see FormatRank).
    private static string CrossQueueNote(HistoricalQueue trueQueue, HistoricalQueue currentQueue) =>
        $"這是{trueQueue.DisplayName}的牌位，不是{currentQueue.DisplayName}的牌位";

    private static string WithDivision(string tierCode, string division)
    {
        var divisionDigit = DivisionDigit(division);
        return divisionDigit is null ? tierCode : $"{tierCode}{divisionDigit}";
    }

    // "鑽石 IV · 42 LP" -- the full tier name and division a tooltip reader actually wants,
    // not the row cell's terse shorthand. Apex tiers have no division the way I-IV mean it for
    // everyone else, so the tier name stands alone. LP is left out entirely, not shown as
    // "0 LP", when Riot did not report it -- OfficialRank.LeaguePoints is nullable for exactly
    // that reason.
    private static string FullRankText(OfficialRank rank)
    {
        var tierName = FullTierName(rank.Tier);
        var body = IsApexTier(rank.Tier) ? tierName : $"{tierName} {rank.Division.Trim().ToUpperInvariant()}";
        return rank.LeaguePoints is { } leaguePoints ? $"{body} · {leaguePoints} LP" : body;
    }

    // zh-tw is the client's own vocabulary for the nine tiers plus Emerald, matching the
    // examples in issue #9's own description (鑽石 IV, 翡翠 II, 大師) -- unlike TierCode below,
    // this is never abbreviated, because the tooltip is exactly the place a player asked for
    // the full name instead of the row cell's shorthand.
    private static string FullTierName(string tier) => tier.Trim().ToUpperInvariant() switch
    {
        // 鐵/銅/銀/金, not 黑鐵/青銅/白銀/黃金: the latter are the Chinese-server names, and
        // this build ships to Taiwan players (the replay fixture is a TW2 one). Mixing them
        // with 宗師/菁英 below -- which are Taiwan-only names -- would put two servers'
        // vocabularies in one tooltip.
        "IRON" => "鐵",
        "BRONZE" => "銅",
        "SILVER" => "銀",
        "GOLD" => "金",
        "PLATINUM" => "白金",
        "EMERALD" => "翡翠",
        "DIAMOND" => "鑽石",
        "MASTER" => "大師",
        "GRANDMASTER" => "宗師",
        "CHALLENGER" => "菁英",
        _ => tier.Trim()
    };

    // 未定位 is standard zh-tw LoL vocabulary and it is real information about the player --
    // distinct from every failure below, which is information about the app, not the player --
    // so it keeps its own marker rather than folding into FailureMarker. The wording is
    // deliberately queue-agnostic ("this player currently has no Solo or Flex rank") rather
    // than naming the queue being played: RiotHistoricalProfileTransport.FetchAsync already
    // searched the current queue (when it has a ladder), Solo, and Flex before reaching this
    // state, so it is the same fact about the player -- no rank in either real ladder --
    // whether they are mid-ARAM or mid-Solo when it is shown; see
    // UnrankedIsTheSameFactWhetherTheCurrentQueueHasALadderOrNot.
    private static OfficialRankDisplay Unranked(HistoricalProfile profile, bool isStale)
    {
        const string statusText = "這位玩家目前沒有單雙排或彈性積分的官方牌位，尚未定位";
        var tooltipText = BuildTooltipText($"官方牌位：{statusText}", profile, isStale);
        return new OfficialRankDisplay(WithStaleSuffix("未", isStale), statusText, isStale, tooltipText);
    }

    private static string WithStaleSuffix(string code, bool isStale) => isStale ? code + StaleSuffix : code;

    /// <summary>
    /// Composes the row tooltip's official-rank block (issue #9). <paramref name="rankLine"/>
    /// already carries its own "官方牌位：" label; below it, when <paramref name="profile"/> is
    /// available, the queue this reading is for, who supplied it, and when -- straight off
    /// <see cref="HistoricalProfile.Queue"/>/<see cref="HistoricalProfile.Source"/>/
    /// <see cref="HistoricalProfile.FetchedAt"/>, no re-derivation. <paramref name="crossQueueNote"/>
    /// (see <see cref="CrossQueueNote"/>) adds an explicit sentence naming the rank's true
    /// queue whenever it differs from the one being played -- including in a no-ladder queue
    /// like ARAM, where every rank shown is a fallback, even though the row cell itself never
    /// carries a mark for that case (see <see cref="FormatRank"/>). <paramref name="isStale"/>
    /// adds a plain-language line of its own -- staleness must be stated in words, not implied
    /// by <see cref="StaleSuffix"/> alone, since the row cell can be too terse to carry it.
    /// <see cref="HonestyNote"/> closes every path without exception.
    /// </summary>
    private static string BuildTooltipText(
        string rankLine,
        HistoricalProfile? profile,
        bool isStale,
        string? crossQueueNote = null)
    {
        var lines = new List<string> { rankLine };
        if (profile is not null)
        {
            lines.Add(
                $"{profile.Queue.DisplayName} · 來源：{profile.Source.DisplayName} · " +
                $"查詢時間 {profile.FetchedAt.ToLocalTime():MM/dd HH:mm}");
        }

        if (crossQueueNote is not null)
        {
            lines.Add(crossQueueNote);
        }

        if (isStale)
        {
            lines.Add("這是較舊的快取資料，不是最新查詢結果。");
        }

        lines.Add(HonestyNote);
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Every lookup failure collapses to the same neutral "nothing to show" marker in the cell
    /// -- ten rows of a distinct, obscure single character (mostly indistinguishable without
    /// hovering) reads as the app being broken, and because these failures are almost always
    /// roster-wide (no key, offline, quota gone), that is exactly the screen a player would see.
    /// The specific reason is not lost, though: it stays a fully distinct, friend-facing
    /// sentence in StatusText -- plain language throughout, no PUUID, LEAGUE-V4, rate limit, or
    /// transport wording, per AGENTS.md's readability rule -- for issue #9's tooltip. "Not
    /// configured" and "policy disabled" share one sentence because they share one signal:
    /// shipping composition (see HistoricalProfileProviders.CreateShippingDefault) routes a
    /// blank key to the exact same PolicyDisabled/PolicyNotApproved pair as an explicit policy
    /// refusal, so there is nothing here that could tell the two apart even if the wording
    /// wanted to.
    /// </summary>
    private static OfficialRankDisplay Failure(HistoricalProfileAvailability availability)
    {
        var statusText = FailureStatusText(availability);
        var tooltipText = BuildTooltipText($"官方牌位：{statusText}", profile: null, isStale: false);
        return new OfficialRankDisplay(FailureMarker, statusText, IsStale: false, tooltipText);
    }

    private static string FailureStatusText(HistoricalProfileAvailability availability) => availability switch
    {
        HistoricalProfileAvailability.Offline => "目前沒有網路連線，查不到牌位",
        HistoricalProfileAvailability.PolicyDisabled => "還沒有設定官方牌位查詢功能",
        HistoricalProfileAvailability.NotFound => "查無這位玩家的官方牌位紀錄",
        HistoricalProfileAvailability.RateLimited => "查詢額度已經用完，稍後會恢復",
        HistoricalProfileAvailability.ServerError => "官方牌位來源暫時故障，稍後再試",
        HistoricalProfileAvailability.Timeout => "查詢逾時，稍後再試",
        HistoricalProfileAvailability.Malformed => "收到的牌位資料無法辨識",
        HistoricalProfileAvailability.Unavailable => "官方牌位來源暫時忙碌，稍後再試",
        // Available/Partial/Stale never reach here -- HistoricalProfileEntry.Failure's own
        // invariant forbids constructing a failure entry with any of the three. This arm only
        // keeps the switch exhaustive against future enum growth; it should never execute.
        _ => "官方牌位暫時無法取得"
    };

    private static string TierCode(string tier) => tier.Trim().ToUpperInvariant() switch
    {
        "IRON" => "I",
        "BRONZE" => "B",
        "SILVER" => "S",
        "GOLD" => "G",
        "PLATINUM" => "P",
        "EMERALD" => "E",
        "DIAMOND" => "D",
        "MASTER" => "M",
        "GRANDMASTER" => "GM",
        "CHALLENGER" => "C",
        _ => tier.Length > 0 ? tier[..1].ToUpperInvariant() : "?"
    };

    private static bool IsApexTier(string tier) =>
        tier.Trim().ToUpperInvariant() is "MASTER" or "GRANDMASTER" or "CHALLENGER";

    private static int? DivisionDigit(string division) => division.Trim().ToUpperInvariant() switch
    {
        "I" => 1,
        "II" => 2,
        "III" => 3,
        "IV" => 4,
        _ => null
    };
}
