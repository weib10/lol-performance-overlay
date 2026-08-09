namespace LolPerformanceOverlay.Core;

public enum SyntheticHistoricalScenario
{
    Available,
    LowSample,
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

/// <summary>
/// Deterministic fake history for tests and Replay only. Shipping composition must use
/// HistoricalProfileProviders.CreateShippingDefault instead.
/// </summary>
public sealed class SyntheticHistoricalProfileProvider : IHistoricalProfileProvider
{
    private static readonly HistoricalProfileSource Source = new(
        HistoricalSourceKind.Synthetic,
        "合成測試資料");

    private readonly IReadOnlyDictionary<string, SyntheticHistoricalScenario> _scenarios;
    private readonly TimeProvider _timeProvider;

    public SyntheticHistoricalProfileProvider(
        IReadOnlyDictionary<string, SyntheticHistoricalScenario>? scenarios = null,
        TimeProvider? timeProvider = null)
    {
        _scenarios = scenarios is null
            ? new Dictionary<string, SyntheticHistoricalScenario>(StringComparer.Ordinal)
            : new Dictionary<string, SyntheticHistoricalScenario>(scenarios, StringComparer.Ordinal);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<HistoricalProfilesResult> GetProfilesAsync(
        IReadOnlyList<RevealedPlayerIdentity> players,
        HistoricalProfileQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var entries = players.Select(player => CreateEntry(player, query)).ToArray();
        return Task.FromResult(new HistoricalProfilesResult(
            Combine(entries),
            entries,
            _timeProvider.GetUtcNow()));
    }

    private HistoricalProfileEntry CreateEntry(
        RevealedPlayerIdentity player,
        HistoricalProfileQuery query)
    {
        ArgumentNullException.ThrowIfNull(player);
        var scenario = _scenarios.TryGetValue(player.StableKey, out var configured)
            ? configured
            : SyntheticHistoricalScenario.Available;

        return scenario switch
        {
            SyntheticHistoricalScenario.Available => HistoricalProfileEntry.WithProfile(
                player,
                HistoricalProfileAvailability.Available,
                CreateProfile(player, query.Queue, 24, _timeProvider.GetUtcNow())),
            SyntheticHistoricalScenario.LowSample => HistoricalProfileEntry.WithProfile(
                player,
                HistoricalProfileAvailability.Available,
                CreateProfile(player, query.Queue, 3, _timeProvider.GetUtcNow())),
            SyntheticHistoricalScenario.Partial => HistoricalProfileEntry.WithProfile(
                player,
                HistoricalProfileAvailability.Partial,
                CreateProfile(player, query.Queue, 12, _timeProvider.GetUtcNow(), includeRank: false),
                HistoricalFailureReason.IncompleteSourceData),
            SyntheticHistoricalScenario.Stale when query.AllowStale => HistoricalProfileEntry.WithProfile(
                player,
                HistoricalProfileAvailability.Stale,
                CreateProfile(player, query.Queue, 18, _timeProvider.GetUtcNow() - TimeSpan.FromHours(3)),
                HistoricalFailureReason.ProviderUnavailable),
            SyntheticHistoricalScenario.Stale => Failure(
                player,
                HistoricalProfileAvailability.Unavailable,
                HistoricalFailureReason.ProviderUnavailable),
            SyntheticHistoricalScenario.Offline => Failure(
                player,
                HistoricalProfileAvailability.Offline,
                HistoricalFailureReason.NetworkOffline),
            SyntheticHistoricalScenario.Unavailable => Failure(
                player,
                HistoricalProfileAvailability.Unavailable,
                HistoricalFailureReason.ProviderUnavailable),
            SyntheticHistoricalScenario.PolicyDisabled => Failure(
                player,
                HistoricalProfileAvailability.PolicyDisabled,
                HistoricalFailureReason.PolicyNotApproved),
            SyntheticHistoricalScenario.NotFound => Failure(
                player,
                HistoricalProfileAvailability.NotFound,
                HistoricalFailureReason.RecordNotFound),
            SyntheticHistoricalScenario.RateLimited => Failure(
                player,
                HistoricalProfileAvailability.RateLimited,
                HistoricalFailureReason.RequestThrottled),
            SyntheticHistoricalScenario.ServerError => Failure(
                player,
                HistoricalProfileAvailability.ServerError,
                HistoricalFailureReason.UpstreamFailure),
            SyntheticHistoricalScenario.Timeout => Failure(
                player,
                HistoricalProfileAvailability.Timeout,
                HistoricalFailureReason.RequestTimedOut),
            SyntheticHistoricalScenario.Malformed => Failure(
                player,
                HistoricalProfileAvailability.Malformed,
                HistoricalFailureReason.InvalidResponse),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
    }

    private static HistoricalProfileEntry Failure(
        RevealedPlayerIdentity player,
        HistoricalProfileAvailability availability,
        HistoricalFailureReason reason) =>
        HistoricalProfileEntry.Failure(player, availability, reason);

    private static HistoricalProfile CreateProfile(
        RevealedPlayerIdentity player,
        HistoricalQueue queue,
        int sampleCount,
        DateTimeOffset fetchedAt,
        bool includeRank = true)
    {
        var value = StableHash(player.StableKey);
        var championSamples = Math.Min(sampleCount, Math.Max(1, sampleCount / 2));
        var secondarySamples = Math.Min(sampleCount, Math.Max(1, sampleCount / 4));
        var primaryRoleSamples = Math.Min(sampleCount, Math.Max(1, sampleCount * 2 / 3));

        return new HistoricalProfile(
            queue,
            includeRank && queue.QueueId is 420 or 440
                ? new OfficialRank(queue, "GOLD", RomanDivision(value % 4), 20 + (int)(value % 60))
                : null,
            sampleCount,
            fetchedAt,
            ConfidenceFor(sampleCount),
            [
                new HistoricalChampionUsage($"Synthetic Champion {(char)('A' + value % 8)}", championSamples),
                new HistoricalChampionUsage($"Synthetic Champion {(char)('J' + value % 8)}", secondarySamples)
            ],
            [
                new HistoricalRoleUsage(RoleFor(value), primaryRoleSamples),
                new HistoricalRoleUsage(RoleFor(value + 1), sampleCount - primaryRoleSamples)
            ],
            CreatePlayStyle(value),
            Source);
    }

    private static HistoricalPlayStyle CreatePlayStyle(uint value) =>
        new(
            Dimension(value, 0, "近期樣本中的主動交戰傾向"),
            Dimension(value, 3, "近期樣本中的存活傾向"),
            Dimension(value, 6, "近期樣本中的團隊參與傾向"),
            Dimension(value, 9, "近期樣本中的發育傾向"),
            Dimension(value, 12, "近期樣本中的英雄池廣度"));

    private static HistoricalStyleDimension Dimension(uint value, int shift, string explanation) =>
        new((HistoricalStyleBand)((value >> shift) % 5), explanation);

    private static HistoricalConfidence ConfidenceFor(int samples) => samples switch
    {
        < 5 => HistoricalConfidence.InsufficientSample,
        < 10 => HistoricalConfidence.Low,
        < 20 => HistoricalConfidence.Medium,
        _ => HistoricalConfidence.High
    };

    private static string RomanDivision(uint value) => value switch
    {
        0 => "IV",
        1 => "III",
        2 => "II",
        _ => "I"
    };

    private static string RoleFor(uint value) => (value % 5) switch
    {
        0 => "TOP",
        1 => "JUNGLE",
        2 => "MIDDLE",
        3 => "BOTTOM",
        _ => "UTILITY"
    };

    private static uint StableHash(string value)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }

        return hash;
    }

    private static HistoricalProfileAvailability Combine(IReadOnlyList<HistoricalProfileEntry> entries)
    {
        if (entries.Count == 0)
        {
            return HistoricalProfileAvailability.Unavailable;
        }

        var first = entries[0].Availability;
        return entries.All(entry => entry.Availability == first)
            ? first
            : HistoricalProfileAvailability.Partial;
    }
}

public sealed class UnavailableHistoricalProfileProvider : IHistoricalProfileProvider
{
    private readonly TimeProvider _timeProvider;

    public UnavailableHistoricalProfileProvider(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<HistoricalProfilesResult> GetProfilesAsync(
        IReadOnlyList<RevealedPlayerIdentity> players,
        HistoricalProfileQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var entries = players
            .Select(player => HistoricalProfileEntry.Failure(
                player,
                HistoricalProfileAvailability.Unavailable,
                HistoricalFailureReason.ProviderUnavailable))
            .ToArray();
        return Task.FromResult(new HistoricalProfilesResult(
            HistoricalProfileAvailability.Unavailable,
            entries,
            _timeProvider.GetUtcNow()));
    }
}

public sealed class PolicyDisabledHistoricalProfileProvider : IHistoricalProfileProvider
{
    private readonly TimeProvider _timeProvider;

    public PolicyDisabledHistoricalProfileProvider(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<HistoricalProfilesResult> GetProfilesAsync(
        IReadOnlyList<RevealedPlayerIdentity> players,
        HistoricalProfileQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var entries = players
            .Select(player => HistoricalProfileEntry.Failure(
                player,
                HistoricalProfileAvailability.PolicyDisabled,
                HistoricalFailureReason.PolicyNotApproved))
            .ToArray();
        return Task.FromResult(new HistoricalProfilesResult(
            HistoricalProfileAvailability.PolicyDisabled,
            entries,
            _timeProvider.GetUtcNow()));
    }
}

public static class HistoricalProfileProviders
{
    /// <summary>
    /// The public package remains honest when no approved live backend is configured.
    /// Synthetic data is opt-in and is never selected by shipping composition.
    /// </summary>
    public static IHistoricalProfileProvider CreateShippingDefault(TimeProvider? timeProvider = null) =>
        new PolicyDisabledHistoricalProfileProvider(timeProvider);
}
