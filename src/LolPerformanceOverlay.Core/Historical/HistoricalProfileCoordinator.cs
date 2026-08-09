using System.Collections.Concurrent;

namespace LolPerformanceOverlay.Core;

public interface IHistoricalProfileProvider
{
    Task<HistoricalProfilesResult> GetProfilesAsync(
        IReadOnlyList<RevealedPlayerIdentity> players,
        HistoricalProfileQuery query,
        CancellationToken cancellationToken);
}

/// <summary>
/// A future approved live adapter implements only transport and schema translation.
/// Cache, request coalescing, deadlines, and concurrency limits stay in the coordinator.
/// </summary>
public interface IHistoricalProfileTransport
{
    Task<HistoricalProfileTransportResult> FetchAsync(
        RevealedPlayerIdentity player,
        HistoricalProfileQuery query,
        CancellationToken cancellationToken);
}

public sealed record HistoricalProfileTransportResult
{
    private HistoricalProfileTransportResult(
        HistoricalProfileAvailability availability,
        HistoricalFailureReason failureReason,
        HistoricalProfile? profile)
    {
        Availability = availability;
        FailureReason = failureReason;
        Profile = profile;
    }

    public HistoricalProfileAvailability Availability { get; }
    public HistoricalFailureReason FailureReason { get; }
    public HistoricalProfile? Profile { get; }

    public static HistoricalProfileTransportResult WithProfile(
        HistoricalProfileAvailability availability,
        HistoricalProfile profile,
        HistoricalFailureReason failureReason = HistoricalFailureReason.None)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (availability is not (HistoricalProfileAvailability.Available or
            HistoricalProfileAvailability.Partial or HistoricalProfileAvailability.Stale))
        {
            throw new ArgumentOutOfRangeException(nameof(availability));
        }

        return new HistoricalProfileTransportResult(availability, failureReason, profile);
    }

    public static HistoricalProfileTransportResult Failure(
        HistoricalProfileAvailability availability,
        HistoricalFailureReason failureReason)
    {
        if (availability is HistoricalProfileAvailability.Available or
            HistoricalProfileAvailability.Partial or HistoricalProfileAvailability.Stale)
        {
            throw new ArgumentOutOfRangeException(nameof(availability));
        }

        return new HistoricalProfileTransportResult(availability, failureReason, null);
    }
}

public sealed record HistoricalProfileCoordinatorOptions
{
    public HistoricalProfileCoordinatorOptions(
        TimeSpan requestTimeout,
        TimeSpan freshLifetime,
        TimeSpan staleLifetime,
        int maximumConcurrency = 3,
        int maximumPlayersPerRequest = 10)
    {
        if (requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        if (freshLifetime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(freshLifetime));
        }

        if (staleLifetime < freshLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(staleLifetime));
        }

        if (maximumConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        }

        if (maximumPlayersPerRequest <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPlayersPerRequest));
        }

        RequestTimeout = requestTimeout;
        FreshLifetime = freshLifetime;
        StaleLifetime = staleLifetime;
        MaximumConcurrency = maximumConcurrency;
        MaximumPlayersPerRequest = maximumPlayersPerRequest;
    }

    public TimeSpan RequestTimeout { get; }
    public TimeSpan FreshLifetime { get; }
    public TimeSpan StaleLifetime { get; }
    public int MaximumConcurrency { get; }
    public int MaximumPlayersPerRequest { get; }

    public static HistoricalProfileCoordinatorOptions Default { get; } = new(
        TimeSpan.FromSeconds(3),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(2));
}

public sealed class HistoricalProfileCoordinator : IHistoricalProfileProvider, IDisposable
{
    private readonly IHistoricalProfileTransport _transport;
    private readonly HistoricalProfileCoordinatorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _concurrency;
    private readonly ConcurrentDictionary<CacheKey, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<CacheKey, InflightRequest> _inflight = new();
    private bool _disposed;

    public HistoricalProfileCoordinator(
        IHistoricalProfileTransport transport,
        HistoricalProfileCoordinatorOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? HistoricalProfileCoordinatorOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _concurrency = new SemaphoreSlim(_options.MaximumConcurrency, _options.MaximumConcurrency);
    }

    public async Task<HistoricalProfilesResult> GetProfilesAsync(
        IReadOnlyList<RevealedPlayerIdentity> players,
        HistoricalProfileQuery query,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(query);
        if (players.Count > _options.MaximumPlayersPerRequest)
        {
            throw new ArgumentOutOfRangeException(nameof(players), $"At most {_options.MaximumPlayersPerRequest} revealed players may be queried at once.");
        }

        if (players.Any(player => player is null))
        {
            throw new ArgumentException("Every history request must contain a normally revealed identity.", nameof(players));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var tasks = players.Select(player => GetOneAsync(player, query, cancellationToken)).ToArray();
        var entries = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new HistoricalProfilesResult(
            Combine(entries),
            entries,
            _timeProvider.GetUtcNow());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var request in _inflight.Values)
        {
            request.Cancel();
        }
    }

    private async Task<HistoricalProfileEntry> GetOneAsync(
        RevealedPlayerIdentity player,
        HistoricalProfileQuery query,
        CancellationToken cancellationToken)
    {
        var key = CacheKey.From(player, query.Queue);
        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(key, out var cached) && IsFresh(cached.Profile, now))
        {
            return HistoricalProfileEntry.WithProfile(player, cached.Availability, cached.Profile);
        }

        var candidate = new InflightRequest(
            token => FetchAndCacheAsync(key, player, query, token));
        var inflight = _inflight.GetOrAdd(key, candidate);
        inflight.AddWaiter();
        if (ReferenceEquals(candidate, inflight))
        {
            _ = RemoveCompletedInflightAsync(key, inflight);
        }
        else
        {
            candidate.Dispose();
        }

        HistoricalProfileTransportResult fetched;
        try
        {
            fetched = await inflight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            var remainingWaiters = inflight.RemoveWaiter();
            if (remainingWaiters == 0 && !inflight.Task.IsCompleted)
            {
                RemoveInflight(key, inflight);
                inflight.Cancel();
            }
            else if (inflight.Task.IsCompleted)
            {
                RemoveInflight(key, inflight);
            }
        }

        if (fetched.Profile is not null)
        {
            if (fetched.Availability == HistoricalProfileAvailability.Stale && !query.AllowStale)
            {
                return HistoricalProfileEntry.Failure(
                    player,
                    HistoricalProfileAvailability.Unavailable,
                    fetched.FailureReason == HistoricalFailureReason.None
                        ? HistoricalFailureReason.ProviderUnavailable
                        : fetched.FailureReason);
            }

            return HistoricalProfileEntry.WithProfile(
                player,
                fetched.Availability,
                fetched.Profile,
                fetched.FailureReason);
        }

        now = _timeProvider.GetUtcNow();
        if (query.AllowStale && _cache.TryGetValue(key, out cached) && IsWithinStaleLifetime(cached.Profile, now))
        {
            return HistoricalProfileEntry.WithProfile(
                player,
                HistoricalProfileAvailability.Stale,
                cached.Profile,
                fetched.FailureReason);
        }

        return HistoricalProfileEntry.Failure(player, fetched.Availability, fetched.FailureReason);
    }

    private async Task<HistoricalProfileTransportResult> FetchAndCacheAsync(
        CacheKey key,
        RevealedPlayerIdentity player,
        HistoricalProfileQuery query,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        var lockTaken = false;
        try
        {
            await _concurrency.WaitAsync(linked.Token).ConfigureAwait(false);
            lockTaken = true;
            var result = await _transport.FetchAsync(player, query, linked.Token).ConfigureAwait(false);
            if (!IsValid(result, query, _timeProvider.GetUtcNow()))
            {
                return HistoricalProfileTransportResult.Failure(
                    HistoricalProfileAvailability.Malformed,
                    HistoricalFailureReason.InvalidResponse);
            }

            if (result.Profile is not null &&
                result.Availability is HistoricalProfileAvailability.Available or HistoricalProfileAvailability.Partial)
            {
                _cache[key] = new CacheEntry(result.Availability, result.Profile);
                if (!IsFresh(result.Profile, _timeProvider.GetUtcNow()))
                {
                    return HistoricalProfileTransportResult.WithProfile(
                        HistoricalProfileAvailability.Stale,
                        result.Profile,
                        result.FailureReason);
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return HistoricalProfileTransportResult.Failure(
                HistoricalProfileAvailability.Timeout,
                HistoricalFailureReason.RequestTimedOut);
        }
        catch (HttpRequestException)
        {
            return HistoricalProfileTransportResult.Failure(
                HistoricalProfileAvailability.Offline,
                HistoricalFailureReason.NetworkOffline);
        }
        catch
        {
            return HistoricalProfileTransportResult.Failure(
                HistoricalProfileAvailability.ServerError,
                HistoricalFailureReason.UpstreamFailure);
        }
        finally
        {
            if (lockTaken)
            {
                _concurrency.Release();
            }
        }
    }

    private async Task RemoveCompletedInflightAsync(
        CacheKey key,
        InflightRequest inflight)
    {
        try
        {
            await inflight.Task.ConfigureAwait(false);
        }
        catch
        {
            // FetchAndCacheAsync contains its failures. This is defensive cleanup only.
        }

        RemoveInflight(key, inflight);
        inflight.Dispose();
    }

    private void RemoveInflight(
        CacheKey key,
        InflightRequest inflight) =>
        ((ICollection<KeyValuePair<CacheKey, InflightRequest>>)_inflight)
            .Remove(new KeyValuePair<CacheKey, InflightRequest>(key, inflight));

    private bool IsFresh(HistoricalProfile profile, DateTimeOffset now) =>
        profile.FetchedAt <= now + TimeSpan.FromMinutes(5) && now - profile.FetchedAt <= _options.FreshLifetime;

    private bool IsWithinStaleLifetime(HistoricalProfile profile, DateTimeOffset now) =>
        profile.FetchedAt <= now + TimeSpan.FromMinutes(5) && now - profile.FetchedAt <= _options.StaleLifetime;

    private static bool IsValid(
        HistoricalProfileTransportResult result,
        HistoricalProfileQuery query,
        DateTimeOffset now)
    {
        if (result is null)
        {
            return false;
        }

        if (result.Profile is null)
        {
            return result.Availability is not (HistoricalProfileAvailability.Available or
                HistoricalProfileAvailability.Partial or HistoricalProfileAvailability.Stale);
        }

        var profile = result.Profile;
        if (result.Availability is not (HistoricalProfileAvailability.Available or
            HistoricalProfileAvailability.Partial or HistoricalProfileAvailability.Stale) ||
            profile.Queue.QueueId != query.Queue.QueueId ||
            !string.Equals(profile.Queue.Mode, query.Queue.Mode, StringComparison.OrdinalIgnoreCase) ||
            profile.FetchedAt > now + TimeSpan.FromMinutes(5) ||
            profile.Source.Kind == HistoricalSourceKind.None ||
            profile.CommonChampions.Any(champion => champion.SampleCount > profile.SampleCount) ||
            profile.CommonRoles.Any(role => role.SampleCount > profile.SampleCount) ||
            (profile.SampleCount < 5 && profile.Confidence != HistoricalConfidence.InsufficientSample))
        {
            return false;
        }

        return true;
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

    private readonly record struct CacheKey(
        string StableKey,
        string GameName,
        string TagLine,
        string Region,
        int QueueId,
        string Mode)
    {
        public static CacheKey From(RevealedPlayerIdentity player, HistoricalQueue queue) =>
            new(
                player.StableKey,
                player.GameName,
                player.TagLine,
                player.Region,
                queue.QueueId,
                queue.Mode.ToUpperInvariant());
    }

    private sealed record CacheEntry(
        HistoricalProfileAvailability Availability,
        HistoricalProfile Profile);

    private sealed class InflightRequest : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Lazy<Task<HistoricalProfileTransportResult>> _task;
        private int _waiters;

        public InflightRequest(
            Func<CancellationToken, Task<HistoricalProfileTransportResult>> operation) =>
            _task = new Lazy<Task<HistoricalProfileTransportResult>>(
                () => operation(_cancellation.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);

        public Task<HistoricalProfileTransportResult> Task => _task.Value;

        public void AddWaiter() => Interlocked.Increment(ref _waiters);

        public int RemoveWaiter() => Interlocked.Decrement(ref _waiters);

        public void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => _cancellation.Dispose();
    }
}
