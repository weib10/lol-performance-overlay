namespace LolPerformanceOverlay.Core;

/// <summary>
/// An identity that Riot has already revealed through the normal client experience.
/// The absence of a public constructor keeps anonymous lobby slots out of the history API.
/// </summary>
public sealed record RevealedPlayerIdentity
{
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
            string.IsNullOrWhiteSpace(region))
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
    public HistoricalQueue(int queueId, string mode, string displayName)
    {
        if (queueId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        QueueId = queueId;
        Mode = mode.Trim();
        DisplayName = displayName.Trim();
    }

    public int QueueId { get; }
    public string Mode { get; }
    public string DisplayName { get; }

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
    public HistoricalProfileSource(HistoricalSourceKind kind, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        Kind = kind;
        DisplayName = displayName.Trim();
    }

    public HistoricalSourceKind Kind { get; }
    public string DisplayName { get; }
}

public sealed record OfficialRank
{
    public OfficialRank(HistoricalQueue queue, string tier, string division, int? leaguePoints = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(tier);
        ArgumentException.ThrowIfNullOrWhiteSpace(division);
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
    public HistoricalChampionUsage(string championName, int sampleCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(championName);
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
    public HistoricalRoleUsage(string role, int sampleCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
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
    public HistoricalStyleDimension(HistoricalStyleBand band, string explanation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
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
    public HistoricalProfile(
        HistoricalQueue queue,
        OfficialRank? officialRank,
        int sampleCount,
        DateTimeOffset fetchedAt,
        HistoricalConfidence confidence,
        IEnumerable<HistoricalChampionUsage> commonChampions,
        IEnumerable<HistoricalRoleUsage> commonRoles,
        HistoricalPlayStyle playStyle,
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
        CommonChampions = commonChampions?.ToArray() ?? throw new ArgumentNullException(nameof(commonChampions));
        CommonRoles = commonRoles?.ToArray() ?? throw new ArgumentNullException(nameof(commonRoles));
        PlayStyle = playStyle ?? throw new ArgumentNullException(nameof(playStyle));
        Source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public HistoricalQueue Queue { get; }
    public OfficialRank? OfficialRank { get; }
    public int SampleCount { get; }
    public DateTimeOffset FetchedAt { get; }
    public HistoricalConfidence Confidence { get; }
    public IReadOnlyList<HistoricalChampionUsage> CommonChampions { get; }
    public IReadOnlyList<HistoricalRoleUsage> CommonRoles { get; }
    public HistoricalPlayStyle PlayStyle { get; }
    public HistoricalProfileSource Source { get; }
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
