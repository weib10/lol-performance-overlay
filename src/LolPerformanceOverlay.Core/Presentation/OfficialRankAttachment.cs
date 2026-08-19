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
    /// <summary>
    /// Returns a snapshot with each visible, non-anonymous player's <see cref="OverlayPlayer.OfficialRank"/>
    /// set from <paramref name="profiles"/> where a match exists, joined by
    /// <see cref="OverlayPlayer.StableKey"/> against <see cref="RevealedPlayerIdentity.StableKey"/> --
    /// never by display name or Riot ID text. When nothing would change, the exact same
    /// <paramref name="snapshot"/> instance is returned so callers never manufacture a false diff.
    /// </summary>
    public static OverlaySnapshot Attach(OverlaySnapshot snapshot, HistoricalProfilesResult? profiles)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (profiles is null || profiles.Entries.Count == 0)
        {
            return snapshot;
        }

        var ranksByStableKey = BuildRankLookup(profiles);
        if (ranksByStableKey.Count == 0)
        {
            return snapshot;
        }

        var teamsChanged = false;
        var updatedTeams = new OverlayTeam[snapshot.Teams.Count];
        for (var teamIndex = 0; teamIndex < snapshot.Teams.Count; teamIndex++)
        {
            var team = snapshot.Teams[teamIndex];
            var updatedTeam = AttachToTeam(team, ranksByStableKey);
            updatedTeams[teamIndex] = updatedTeam;
            teamsChanged |= !ReferenceEquals(updatedTeam, team);
        }

        return teamsChanged ? snapshot with { Teams = updatedTeams } : snapshot;
    }

    private static OverlayTeam AttachToTeam(
        OverlayTeam team,
        IReadOnlyDictionary<string, OfficialRankDisplay> ranksByStableKey)
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
                ranksByStableKey.TryGetValue(player.StableKey, out var rank) &&
                rank != player.OfficialRank)
            {
                updatedPlayers[playerIndex] = player with { OfficialRank = rank };
                playersChanged = true;
            }
            else
            {
                updatedPlayers[playerIndex] = player;
            }
        }

        return playersChanged ? team with { Players = updatedPlayers } : team;
    }

    private static Dictionary<string, OfficialRankDisplay> BuildRankLookup(HistoricalProfilesResult profiles)
    {
        // A stale entry (an identity no longer on the current roster) simply never matches
        // any player's StableKey below -- Attach does not need its own roster-generation
        // check, the join itself already refuses to attach it to a new occupant.
        var lookup = new Dictionary<string, OfficialRankDisplay>(StringComparer.Ordinal);
        foreach (var entry in profiles.Entries)
        {
            if (entry.Profile?.OfficialRank is { } rank)
            {
                lookup[entry.Identity.StableKey] = FormatShortCode(rank);
            }
        }

        return lookup;
    }

    // "D4", "E2", "G1" -- tier initial plus division digit, the shorthand players already
    // use for themselves. The three apex tiers have no division the way I-IV mean it for
    // everyone else, so their code is the tier letters alone.
    private static OfficialRankDisplay FormatShortCode(OfficialRank rank)
    {
        var tierCode = TierCode(rank.Tier);
        if (IsApexTier(rank.Tier))
        {
            return new OfficialRankDisplay(tierCode);
        }

        var divisionDigit = DivisionDigit(rank.Division);
        var shortCode = divisionDigit is null ? tierCode : $"{tierCode}{divisionDigit}";
        return new OfficialRankDisplay(shortCode);
    }

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
