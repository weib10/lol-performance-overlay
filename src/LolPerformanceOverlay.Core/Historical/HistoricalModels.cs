namespace LolPerformanceOverlay.Core;

/// <summary>
/// An identity that Riot has already revealed through the normal client experience.
/// The absence of a public constructor keeps anonymous lobby slots out of the history API.
/// </summary>
public sealed record RevealedPlayerIdentity
{
    private const int MaximumStableKeyLength = 256;
    private const int MaximumGameNameLength = 128;
    private const int MaximumTagLineLength = 32;
    private const int MaximumRegionLength = 32;

    private RevealedPlayerIdentity(string stableKey, string gameName, string tagLine, string region)
    {
        StableKey = stableKey;
        GameName = gameName;
        TagLine = tagLine;
        Region = region;
    }

    public string StableKey { get; }
    public string GameName { get; }
    public string TagLine { get; }
    public string Region { get; }

    public static RevealedPlayerIdentity CreateNormallyRevealed(
        string stableKey,
        string gameName,
        string tagLine,
        string region)
    {
        if (!TryCreateNormallyRevealed(stableKey, gameName, tagLine, region, out var identity))
        {
            throw new ArgumentException("A normally revealed player identity requires a stable key, game name, tag line, and region.");
        }

        return identity;
    }

    public static bool TryCreateNormallyRevealed(
        string? stableKey,
        string? gameName,
        string? tagLine,
        string? region,
        out RevealedPlayerIdentity identity)
    {
        identity = null!;
        if (string.IsNullOrWhiteSpace(stableKey) ||
            string.IsNullOrWhiteSpace(gameName) ||
            string.IsNullOrWhiteSpace(tagLine) ||
            string.IsNullOrWhiteSpace(region) ||
            stableKey.Length > MaximumStableKeyLength ||
            gameName.Length > MaximumGameNameLength ||
            tagLine.Length > MaximumTagLineLength ||
            region.Length > MaximumRegionLength)
        {
            return false;
        }

        identity = new RevealedPlayerIdentity(
            stableKey.Trim(),
            gameName.Trim(),
            tagLine.Trim(),
            region.Trim().ToLowerInvariant());
        return true;
    }
}

public sealed record HistoricalQueue
{
    private const int MaximumModeLength = 64;
    private const int MaximumDisplayNameLength = 128;

    public HistoricalQueue(int queueId, string mode, string displayName)
    {
        if (queueId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (mode.Length > MaximumModeLength)
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (displayName.Length > MaximumDisplayNameLength)
        {
            throw new ArgumentOutOfRangeException(nameof(displayName));
        }

        QueueId = queueId;
        // Normalize once at the model boundary; cache-key creation is a recurring lookup and
        // should not allocate a second uppercase string for every request.
        Mode = mode.Trim().ToUpperInvariant();
        DisplayName = displayName.Trim();
    }

    public int QueueId { get; }
    public string Mode { get; }
    public string DisplayName { get; }

    // The only two queues a rank can ever genuinely belong to -- RiotHistoricalProfileTransport
    // only ever searches Solo and Flex entries (see FindPreferredEntry), whatever queue is
    // actually being played. Shared by HistoricalProfileCoordinator.IsValid (an OfficialRank
    // must point at one of these two, though not necessarily the profile's own Queue now that
    // fallback exists -- see the "OfficialRank From..." coordinator tests) and by
    // OfficialRankAttachment (only a ranked current queue -- one where this is true -- ever
    // gets the cross-queue cell mark; a no-ladder queue like ARAM never does, because every
    // rank shown there is a fallback by construction).
    public bool IsRankedLadder => QueueId == RankedSolo.QueueId || QueueId == RankedFlex.QueueId;

    public static HistoricalQueue RankedSolo { get; } = new(420, "CLASSIC", "單雙排");
    public static HistoricalQueue RankedFlex { get; } = new(440, "CLASSIC", "彈性積分");
    public static HistoricalQueue Aram { get; } = new(450, "ARAM", "隨機單中");
}

public enum HistoricalProfileAvailability
{
    Available,
    Partial,
    Stale,
    Offline,
    Unavailable,
    PolicyDisabled,
    NotFound,
    RateLimited,
    ServerError,
    Timeout,
    Malformed
}

public enum HistoricalFailureReason
{
    None,
    IncompleteSourceData,
    CachedAfterSourceFailure,
    NetworkOffline,
    ProviderUnavailable,
    PolicyNotApproved,
    RecordNotFound,
    RequestThrottled,
    UpstreamFailure,
    RequestTimedOut,
    InvalidResponse
    // NoRankedLadder (added for issue #8, removed here) used to mark "this queue has no
    // ranked ladder at all" as distinct from ProviderUnavailable. It stopped being
    // reachable the moment RiotHistoricalProfileTransport learned to fall back to Solo/Flex
    // for a queue with no ladder of its own (e.g. ARAM) instead of failing outright -- the
    // transport now always attempts a lookup, so "no ladder" alone is never again a reason a
    // fetch cannot happen. What used to be a distinct "no ladder" cell state in
    // OfficialRankAttachment folded into plain Unranked for the same reason: once the
    // transport searches Solo and Flex regardless of the queue being played, "no rank in a
    // queue with no ladder of its own" and "no rank in a queue that does have one" are the
    // same fact about the player (no Solo or Flex rank exists), not two different ones. Kept
    // out of the enum rather than left as a value nothing can ever produce -- see
    // AGENTS.md rule 8 (可維護性與測試性) and the fallback-rank work item this removal is
    // part of.
}

public enum HistoricalConfidence
{
    InsufficientSample,
    Low,
    Medium,
    High
}

public enum HistoricalSourceKind
{
    Synthetic,
    LiveBackend,
    None
}

public enum HistoricalStyleBand
{
    VeryLow,
    Low,
    Balanced,
    High,
    VeryHigh
}

public sealed record HistoricalProfileSource
{
    private const int MaximumDisplayNameLength = 128;

    public HistoricalProfileSource(HistoricalSourceKind kind, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (displayName.Length > MaximumDisplayNameLength)
        {
            throw new ArgumentOutOfRangeException(nameof(displayName));
        }

        Kind = kind;
        DisplayName = displayName.Trim();
    }

    public HistoricalSourceKind Kind { get; }
    public string DisplayName { get; }
}

public sealed record OfficialRank
{
    private const int MaximumRankFieldLength = 32;

    public OfficialRank(HistoricalQueue queue, string tier, string division, int? leaguePoints = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(tier);
        ArgumentException.ThrowIfNullOrWhiteSpace(division);
        if (tier.Length > MaximumRankFieldLength)
        {
            throw new ArgumentOutOfRangeException(nameof(tier));
        }

        if (division.Length > MaximumRankFieldLength)
        {
            throw new ArgumentOutOfRangeException(nameof(division));
        }

        if (leaguePoints is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(leaguePoints));
        }

        Queue = queue;
        Tier = tier.Trim();
        Division = division.Trim();
        LeaguePoints = leaguePoints;
    }

    public HistoricalQueue Queue { get; }
    public string Tier { get; }
    public string Division { get; }
    public int? LeaguePoints { get; }
}

public sealed record HistoricalChampionUsage
{
    private const int MaximumChampionNameLength = 128;

    public HistoricalChampionUsage(string championName, int sampleCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(championName);
        if (championName.Length > MaximumChampionNameLength)
        {
            throw new ArgumentOutOfRangeException(nameof(championName));
        }

        if (sampleCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }

        ChampionName = championName.Trim();
        SampleCount = sampleCount;
    }

    public string ChampionName { get; }
    public int SampleCount { get; }
}

public sealed record HistoricalRoleUsage
{
    private const int MaximumRoleLength = 64;

    public HistoricalRoleUsage(string role, int sampleCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        if (role.Length > MaximumRoleLength)
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        if (sampleCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }

        Role = role.Trim();
        SampleCount = sampleCount;
    }

    public string Role { get; }
    public int SampleCount { get; }
}

public sealed record HistoricalStyleDimension
{
    private const int MaximumExplanationLength = 256;

    public HistoricalStyleDimension(HistoricalStyleBand band, string explanation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
        if (explanation.Length > MaximumExplanationLength)
        {
            throw new ArgumentOutOfRangeException(nameof(explanation));
        }

        Band = band;
        Explanation = explanation.Trim();
    }

    public HistoricalStyleBand Band { get; }
    public string Explanation { get; }
}

public sealed record HistoricalPlayStyle
{
    public HistoricalPlayStyle(
        HistoricalStyleDimension aggression,
        HistoricalStyleDimension survival,
        HistoricalStyleDimension teamParticipation,
        HistoricalStyleDimension farming,
        HistoricalStyleDimension championPool)
    {
        Aggression = aggression ?? throw new ArgumentNullException(nameof(aggression));
        Survival = survival ?? throw new ArgumentNullException(nameof(survival));
        TeamParticipation = teamParticipation ?? throw new ArgumentNullException(nameof(teamParticipation));
        Farming = farming ?? throw new ArgumentNullException(nameof(farming));
        ChampionPool = championPool ?? throw new ArgumentNullException(nameof(championPool));
    }

    public HistoricalStyleDimension Aggression { get; }
    public HistoricalStyleDimension Survival { get; }
    public HistoricalStyleDimension TeamParticipation { get; }
    public HistoricalStyleDimension Farming { get; }
    public HistoricalStyleDimension ChampionPool { get; }
}

public sealed record HistoricalProfile
{
    private const int MaximumCommonChampionCount = 32;
    private const int MaximumCommonRoleCount = 8;

    public HistoricalProfile(
        HistoricalQueue queue,
        OfficialRank? officialRank,
        int sampleCount,
        DateTimeOffset fetchedAt,
        HistoricalConfidence confidence,
        IEnumerable<HistoricalChampionUsage> commonChampions,
        IEnumerable<HistoricalRoleUsage> commonRoles,
        HistoricalPlayStyle? playStyle,
        HistoricalProfileSource source)
    {
        if (sampleCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }

        Queue = queue ?? throw new ArgumentNullException(nameof(queue));
        OfficialRank = officialRank;
        SampleCount = sampleCount;
        FetchedAt = fetchedAt;
        Confidence = confidence;
        CommonChampions = MaterializeBounded(
            commonChampions,
            MaximumCommonChampionCount,
            nameof(commonChampions));
        CommonRoles = MaterializeBounded(
            commonRoles,
            MaximumCommonRoleCount,
            nameof(commonRoles));
        // Null here means "no play-style read on this profile" -- a rank-only source (a
        // single ranked-entries lookup, no match history) has nothing to derive a style
        // from. It is not the same as an empty style; there is no band to show at all.
        PlayStyle = playStyle;
        Source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public HistoricalQueue Queue { get; }
    public OfficialRank? OfficialRank { get; }
    public int SampleCount { get; }
    public DateTimeOffset FetchedAt { get; }
    public HistoricalConfidence Confidence { get; }
    public IReadOnlyList<HistoricalChampionUsage> CommonChampions { get; }
    public IReadOnlyList<HistoricalRoleUsage> CommonRoles { get; }
    public HistoricalPlayStyle? PlayStyle { get; }
    public HistoricalProfileSource Source { get; }

    private static IReadOnlyList<T> MaterializeBounded<T>(
        IEnumerable<T>? values,
        int maximumCount,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var materialized = values.Take(maximumCount + 1).ToArray();
        if (materialized.Length > maximumCount)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return materialized;
    }
}

public sealed record HistoricalProfileQuery
{
    public HistoricalProfileQuery(HistoricalQueue queue, bool allowStale = true)
    {
        Queue = queue ?? throw new ArgumentNullException(nameof(queue));
        AllowStale = allowStale;
    }

    public HistoricalQueue Queue { get; }
    public bool AllowStale { get; }
}

public sealed record HistoricalProfileEntry
{
    private HistoricalProfileEntry(
        RevealedPlayerIdentity identity,
        HistoricalProfileAvailability availability,
        HistoricalFailureReason failureReason,
        HistoricalProfile? profile)
    {
        Identity = identity;
        Availability = availability;
        FailureReason = failureReason;
        Profile = profile;
    }

    public RevealedPlayerIdentity Identity { get; }
    public HistoricalProfileAvailability Availability { get; }
    public HistoricalFailureReason FailureReason { get; }
    public HistoricalProfile? Profile { get; }

    public static HistoricalProfileEntry WithProfile(
        RevealedPlayerIdentity identity,
        HistoricalProfileAvailability availability,
        HistoricalProfile profile,
        HistoricalFailureReason failureReason = HistoricalFailureReason.None)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(profile);
        if (availability is not (HistoricalProfileAvailability.Available or
            HistoricalProfileAvailability.Partial or HistoricalProfileAvailability.Stale))
        {
            throw new ArgumentOutOfRangeException(nameof(availability));
        }

        return new HistoricalProfileEntry(identity, availability, failureReason, profile);
    }

    public static HistoricalProfileEntry Failure(
        RevealedPlayerIdentity identity,
        HistoricalProfileAvailability availability,
        HistoricalFailureReason failureReason)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (availability is HistoricalProfileAvailability.Available or
            HistoricalProfileAvailability.Partial or HistoricalProfileAvailability.Stale)
        {
            throw new ArgumentOutOfRangeException(nameof(availability));
        }

        return new HistoricalProfileEntry(identity, availability, failureReason, null);
    }
}

public sealed record HistoricalProfilesResult
{
    public HistoricalProfilesResult(
        HistoricalProfileAvailability availability,
        IEnumerable<HistoricalProfileEntry> entries,
        DateTimeOffset completedAt)
    {
        Availability = availability;
        Entries = entries?.ToArray() ?? throw new ArgumentNullException(nameof(entries));
        CompletedAt = completedAt;
    }

    public HistoricalProfileAvailability Availability { get; }
    public IReadOnlyList<HistoricalProfileEntry> Entries { get; }
    public DateTimeOffset CompletedAt { get; }
}
