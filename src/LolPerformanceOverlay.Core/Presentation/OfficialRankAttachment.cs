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
    // -- a resolved rank code, "未" for unranked, or a neutral "nothing to show" marker for
    // every failure -- while every state keeps its own distinct, friend-facing sentence in
    // OfficialRankDisplay.StatusText for issue #9's tooltip to read.
    private const string StaleSuffix = "*";
    private const string FailureMarker = "—";

    /// <summary>
    /// Returns a snapshot with each visible, non-anonymous player's <see cref="OverlayPlayer.OfficialRank"/>
    /// set from <paramref name="profiles"/> where a match exists, joined by
    /// <see cref="OverlayPlayer.StableKey"/> against <see cref="RevealedPlayerIdentity.StableKey"/> --
    /// never by display name or Riot ID text. Every matched entry now produces a display, not
    /// just the ones with a resolved rank: unranked, no-ladder-queue, and every lookup failure
    /// each get a display (see <see cref="Describe"/>), so a row is never blank once a lookup
    /// result exists for it. When nothing would change, the exact same <paramref name="snapshot"/>
    /// instance is returned so callers never manufacture a false diff.
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
    /// rank, an honest "no rank to show, and here is why" for unranked/no-ladder/failure, or a
    /// stale marker layered on top of either of those. The split below mirrors the entry's own
    /// invariants -- <see cref="HistoricalProfileEntry.WithProfile"/> only allows
    /// Available/Partial/Stale, <see cref="HistoricalProfileEntry.Failure"/> only allows the
    /// other eight -- so whether Profile is null is itself the reliable signal, not Availability
    /// read on its own. "No ladder" can arrive either way: the synthetic/replay fixture reaches
    /// it with a Profile present and OfficialRank null (see HistoricalTestData.Profile), while
    /// the live transport reaches it as a failure carrying
    /// <see cref="HistoricalFailureReason.NoRankedLadder"/> (see
    /// RiotHistoricalProfileTransport.FetchAsync) -- both must land on the same wording.
    /// </summary>
    private static OfficialRankDisplay Describe(HistoricalProfileEntry entry)
    {
        var isStale = entry.Availability == HistoricalProfileAvailability.Stale;
        if (entry.Profile?.OfficialRank is { } rank)
        {
            return FormatRank(rank, isStale);
        }

        if (entry.Profile is not null)
        {
            // A resolved profile with no OfficialRank means one of two very different things,
            // and only the queue tells them apart: a queue that HAS a ladder (420/440) but this
            // player has not climbed it yet ("unranked"), versus a queue with no ladder concept
            // at all (ARAM/450 and anything else), where calling the player "unranked" would
            // falsely imply a ladder exists for them to be unplaced on.
            return IsRankedQueue(entry.Profile.Queue.QueueId) ? Unranked(isStale) : NoLadder(isStale);
        }

        // Stale never reaches here -- HistoricalProfileEntry.Failure's invariant forbids it --
        // so a failure entry is never marked stale; there is no cached data behind it to be old.
        // NoRankedLadder is checked ahead of the generic Availability switch below: it is a
        // failure by shape (Profile is null, no HTTP call was ever made) but not a failure by
        // meaning, and it must land on exactly the same wording as the profile-present branch
        // above, not on the generic "something is wrong" marker every other failure gets.
        return entry.FailureReason == HistoricalFailureReason.NoRankedLadder
            ? NoLadder(isStale: false)
            : Failure(entry.Availability);
    }

    private static bool IsRankedQueue(int queueId) =>
        queueId == HistoricalQueue.RankedSolo.QueueId || queueId == HistoricalQueue.RankedFlex.QueueId;

    // "D4", "E2", "G1" -- tier initial plus division digit, the shorthand players already
    // use for themselves. The three apex tiers have no division the way I-IV mean it for
    // everyone else, so their code is the tier letters alone.
    private static OfficialRankDisplay FormatRank(OfficialRank rank, bool isStale)
    {
        var tierCode = TierCode(rank.Tier);
        var shortCode = IsApexTier(rank.Tier) ? tierCode : WithDivision(tierCode, rank.Division);
        // A fresh rank needs no further explanation -- the code already says everything. A
        // stale one gets the marker below plus a sentence, so a three-hour-old cache is never
        // mistaken for this instant's rank.
        var statusText = isStale ? "顯示的是較舊的快取牌位，不是最新資料" : string.Empty;
        return new OfficialRankDisplay(WithStaleSuffix(shortCode, isStale), statusText, isStale);
    }

    private static string WithDivision(string tierCode, string division)
    {
        var divisionDigit = DivisionDigit(division);
        return divisionDigit is null ? tierCode : $"{tierCode}{divisionDigit}";
    }

    // 未定位 is standard zh-tw LoL vocabulary and it is real information about the player --
    // distinct from every failure below, which is information about the app, not the player --
    // so it keeps its own marker rather than folding into FailureMarker.
    private static OfficialRankDisplay Unranked(bool isStale) => new(
        WithStaleSuffix("未", isStale),
        "這個模式目前還沒有牌位，這位玩家尚未定位",
        isStale);

    // ShortCode is empty on purpose, not a marker: in a queue with no ranked ladder (ARAM and
    // the like) the concept can never have a value, on any player, in any game played in that
    // queue. A glyph here would be clutter repeated on every row of every ARAM game, not
    // information -- see OverlayWindow.UpdatePlayerRank, which already collapses the cell
    // whenever the text is empty. IsStale is still recorded honestly (for #9's tooltip) even
    // though the cell stays empty either way -- there is no base marker for a "*" to attach to.
    private static OfficialRankDisplay NoLadder(bool isStale) => new(
        string.Empty,
        "這個模式沒有排位天梯，查不到官方牌位",
        isStale);

    private static string WithStaleSuffix(string code, bool isStale) => isStale ? code + StaleSuffix : code;

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
    private static OfficialRankDisplay Failure(HistoricalProfileAvailability availability) => availability switch
    {
        HistoricalProfileAvailability.Offline => new OfficialRankDisplay(
            FailureMarker, "目前沒有網路連線，查不到牌位"),
        HistoricalProfileAvailability.PolicyDisabled => new OfficialRankDisplay(
            FailureMarker, "還沒有設定官方牌位查詢功能"),
        HistoricalProfileAvailability.NotFound => new OfficialRankDisplay(
            FailureMarker, "查無這位玩家的官方牌位紀錄"),
        HistoricalProfileAvailability.RateLimited => new OfficialRankDisplay(
            FailureMarker, "查詢額度已經用完，稍後會恢復"),
        HistoricalProfileAvailability.ServerError => new OfficialRankDisplay(
            FailureMarker, "官方牌位來源暫時故障，稍後再試"),
        HistoricalProfileAvailability.Timeout => new OfficialRankDisplay(
            FailureMarker, "查詢逾時，稍後再試"),
        HistoricalProfileAvailability.Malformed => new OfficialRankDisplay(
            FailureMarker, "收到的牌位資料無法辨識"),
        HistoricalProfileAvailability.Unavailable => new OfficialRankDisplay(
            FailureMarker, "官方牌位來源暫時忙碌，稍後再試"),
        // Available/Partial/Stale never reach here -- HistoricalProfileEntry.Failure's own
        // invariant forbids constructing a failure entry with any of the three. This arm only
        // keeps the switch exhaustive against future enum growth; it should never execute.
        _ => new OfficialRankDisplay(FailureMarker, "官方牌位暫時無法取得")
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
