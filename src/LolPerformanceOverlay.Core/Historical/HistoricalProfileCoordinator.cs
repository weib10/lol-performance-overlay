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
        int maximumPlayersPerRequest = 10,
        int maximumCacheEntries = 256)
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

        if (maximumCacheEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCacheEntries));
        }

        RequestTimeout = requestTimeout;
        FreshLifetime = freshLifetime;
        StaleLifetime = staleLifetime;
        MaximumConcurrency = maximumConcurrency;
        MaximumPlayersPerRequest = maximumPlayersPerRequest;
        MaximumCacheEntries = maximumCacheEntries;
    }

    public TimeSpan RequestTimeout { get; }
    public TimeSpan FreshLifetime { get; }
    public TimeSpan StaleLifetime { get; }
    public int MaximumConcurrency { get; }
    public int MaximumPlayersPerRequest { get; }
    public int MaximumCacheEntries { get; }

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
    private readonly ITimer _maintenanceTimer;
    private readonly object _stateGate = new();
    private readonly Dictionary<CacheKey, CacheEntry> _cache = new();
    private readonly Dictionary<CacheKey, InflightRequest> _inflight = new();
    private long _cacheAccessSequence;
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
        var maintenanceInterval = CacheMaintenanceInterval(_options.StaleLifetime);
        _maintenanceTimer = _timeProvider.CreateTimer(
            static state => ((HistoricalProfileCoordinator)state!).RunCacheMaintenance(),
            this,
            maintenanceInterval,
            maintenanceInterval);
    }

    internal int CacheCount
    {
        get
        {
            lock (_stateGate)
            {
                return _cache.Count;
            }
        }
    }

    internal int InflightCount
    {
        get
        {
            lock (_stateGate)
            {
                return _inflight.Count;
            }
        }
    }

    internal void RunCacheMaintenance()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            RemoveExpiredEntriesUnderLock(_timeProvider.GetUtcNow());
        }
    }

    public async Task<HistoricalProfilesResult> GetProfilesAsync(
        IReadOnlyList<RevealedPlayerIdentity> players,
        HistoricalProfileQuery query,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
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
        _maintenanceTimer.Dispose();
        InflightRequest[] inflight;
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            inflight = _inflight.Values.Distinct().ToArray();
            _inflight.Clear();
            _cache.Clear();
        }

        foreach (var request in inflight)
        {
            request.Cancel();
        }

        // The coordinator is the sole owner of the transport it was constructed with; a live
        // transport typically holds an HttpClient that needs releasing.
        (_transport as IDisposable)?.Dispose();
    }

    private async Task<HistoricalProfileEntry> GetOneAsync(
        RevealedPlayerIdentity player,
        HistoricalProfileQuery query,
        CancellationToken cancellationToken)
    {
        var key = CacheKey.From(player, query.Queue);
        var now = _timeProvider.GetUtcNow();
        if (TryGetCached(key, now, requireFresh: true, out var cached))
        {
            return HistoricalProfileEntry.WithProfile(player, cached.Availability, cached.Profile);
        }

        InflightRequest inflight;
        var created = false;
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_inflight.TryGetValue(key, out inflight!))
            {
                inflight = new InflightRequest(
                    token => FetchAndCacheAsync(key, player, query, token));
                _inflight.Add(key, inflight);
                created = true;
            }

            inflight.AddWaiter();
        }

        if (created)
        {
            _ = RemoveCompletedInflightAsync(key, inflight);
        }

        HistoricalProfileTransportResult fetched;
        try
        {
            fetched = await inflight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (ReleaseWaiter(key, inflight))
            {
                inflight.Cancel();
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
        if (query.AllowStale && TryGetCached(key, now, requireFresh: false, out cached))
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
            var now = _timeProvider.GetUtcNow();
            if (!IsValid(result, query, now) ||
                result.Profile is not null && !IsWithinStaleLifetime(result.Profile, now))
            {
                return HistoricalProfileTransportResult.Failure(
                    HistoricalProfileAvailability.Malformed,
                    HistoricalFailureReason.InvalidResponse);
            }

            if (result.Profile is not null &&
                result.Availability is HistoricalProfileAvailability.Available or HistoricalProfileAvailability.Partial)
            {
                StoreCached(key, result.Availability, result.Profile);
                if (!IsFresh(result.Profile, now))
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
        InflightRequest inflight)
    {
        lock (_stateGate)
        {
            if (_inflight.TryGetValue(key, out var current) && ReferenceEquals(current, inflight))
            {
                _inflight.Remove(key);
            }
        }
    }

    private bool ReleaseWaiter(CacheKey key, InflightRequest inflight)
    {
        lock (_stateGate)
        {
            var remainingWaiters = inflight.RemoveWaiter();
            if (remainingWaiters < 0)
            {
                throw new InvalidOperationException("Historical request waiter count became negative.");
            }

            var completed = inflight.Task.IsCompleted;
            if ((remainingWaiters == 0 || completed) &&
                _inflight.TryGetValue(key, out var current) &&
                ReferenceEquals(current, inflight))
            {
                _inflight.Remove(key);
            }

            // Removal and the zero-waiter decision share the same lock used by new callers,
            // so nobody can attach to this request after we decide to cancel it.
            return remainingWaiters == 0 && !completed;
        }
    }

    private bool TryGetCached(
        CacheKey key,
        DateTimeOffset now,
        bool requireFresh,
        out CacheEntry entry)
    {
        lock (_stateGate)
        {
            if (!_cache.TryGetValue(key, out entry!))
            {
                return false;
            }

            if (!IsWithinStaleLifetime(entry.Profile, now))
            {
                _cache.Remove(key);
                entry = null!;
                return false;
            }

            if (requireFresh && !IsFresh(entry.Profile, now))
            {
                return false;
            }

            entry.LastAccess = ++_cacheAccessSequence;
            return true;
        }
    }

    private void StoreCached(
        CacheKey key,
        HistoricalProfileAvailability availability,
        HistoricalProfile profile)
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            RemoveExpiredEntriesUnderLock(now);
            _cache[key] = new CacheEntry(availability, profile, ++_cacheAccessSequence);
            while (_cache.Count > _options.MaximumCacheEntries)
            {
                var leastRecentlyUsed = _cache.MinBy(entry => entry.Value.LastAccess).Key;
                _cache.Remove(leastRecentlyUsed);
            }
        }
    }

    private void RemoveExpiredEntriesUnderLock(DateTimeOffset now)
    {
        if (_cache.Count == 0)
        {
            return;
        }

        List<CacheKey>? expiredKeys = null;
        foreach (var entry in _cache)
        {
            if (!IsWithinStaleLifetime(entry.Value.Profile, now))
            {
                (expiredKeys ??= []).Add(entry.Key);
            }
        }

        if (expiredKeys is null)
        {
            return;
        }

        foreach (var key in expiredKeys)
        {
            _cache.Remove(key);
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private static TimeSpan CacheMaintenanceInterval(TimeSpan staleLifetime)
    {
        var halfLifetimeTicks = Math.Max(staleLifetime.Ticks / 2, TimeSpan.FromSeconds(1).Ticks);
        return TimeSpan.FromTicks(Math.Min(halfLifetimeTicks, TimeSpan.FromMinutes(5).Ticks));
    }

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
            profile.OfficialRank is not null &&
            (profile.OfficialRank.Queue.QueueId != profile.Queue.QueueId ||
             !string.Equals(
                 profile.OfficialRank.Queue.Mode,
                 profile.Queue.Mode,
                 StringComparison.OrdinalIgnoreCase)) ||
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
        string Region,
        int QueueId,
        string Mode)
    {
        public static CacheKey From(RevealedPlayerIdentity player, HistoricalQueue queue) =>
            new(
                player.StableKey,
                player.Region,
                queue.QueueId,
                queue.Mode);
    }

    private sealed class CacheEntry
    {
        public CacheEntry(
            HistoricalProfileAvailability availability,
            HistoricalProfile profile,
            long lastAccess)
        {
            Availability = availability;
            Profile = profile;
            LastAccess = lastAccess;
        }

        public HistoricalProfileAvailability Availability { get; }
        public HistoricalProfile Profile { get; }
        public long LastAccess { get; set; }
    }

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
