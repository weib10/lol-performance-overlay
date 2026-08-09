using LolPerformanceOverlay.Core;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class HistoricalCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FreshProfileIsCachedPerIdentityAndQueue()
    {
        var clock = new HistoricalManualTimeProvider(Now);
        var transport = new RecordingHistoricalTransport((player, query, _) =>
            Task.FromResult(HistoricalProfileTransportResult.WithProfile(
                HistoricalProfileAvailability.Available,
                HistoricalTestData.Profile(player, query.Queue, clock.GetUtcNow()))));
        using var coordinator = CreateCoordinator(transport, clock);
        var player = HistoricalTestData.Player(40);
        var query = new HistoricalProfileQuery(HistoricalQueue.RankedSolo);

        var first = await coordinator.GetProfilesAsync([player], query, CancellationToken.None);
        var second = await coordinator.GetProfilesAsync([player], query, CancellationToken.None);

        Assert.Equal(HistoricalProfileAvailability.Available, first.Availability);
        Assert.Equal(HistoricalProfileAvailability.Available, second.Availability);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task DifferentQueuesDoNotShareCachedProfile()
    {
        var clock = new HistoricalManualTimeProvider(Now);
        var transport = new RecordingHistoricalTransport((player, query, _) =>
            Task.FromResult(HistoricalProfileTransportResult.WithProfile(
                HistoricalProfileAvailability.Available,
                HistoricalTestData.Profile(player, query.Queue, clock.GetUtcNow()))));
        using var coordinator = CreateCoordinator(transport, clock);
        var player = HistoricalTestData.Player(41);

        await coordinator.GetProfilesAsync(
            [player],
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);
        await coordinator.GetProfilesAsync(
            [player],
            new HistoricalProfileQuery(HistoricalQueue.Aram),
            CancellationToken.None);

        Assert.Equal(2, transport.CallCount);
    }

    [Fact]
    public async Task ExpiredFreshProfileFallsBackToStaleOnOfflineFailure()
    {
        var clock = new HistoricalManualTimeProvider(Now);
        var responseNumber = 0;
        var transport = new RecordingHistoricalTransport((player, query, _) =>
        {
            if (Interlocked.Increment(ref responseNumber) == 1)
            {
                return Task.FromResult(HistoricalProfileTransportResult.WithProfile(
                    HistoricalProfileAvailability.Available,
                    HistoricalTestData.Profile(player, query.Queue, clock.GetUtcNow())));
            }

            return Task.FromResult(HistoricalProfileTransportResult.Failure(
                HistoricalProfileAvailability.Offline,
                HistoricalFailureReason.NetworkOffline));
        });
        using var coordinator = CreateCoordinator(
            transport,
            clock,
            freshLifetime: TimeSpan.FromMinutes(1),
            staleLifetime: TimeSpan.FromMinutes(10));
        var player = HistoricalTestData.Player(42);
        var query = new HistoricalProfileQuery(HistoricalQueue.RankedSolo);

        await coordinator.GetProfilesAsync([player], query, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(2));
        var result = await coordinator.GetProfilesAsync([player], query, CancellationToken.None);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(2, transport.CallCount);
        Assert.Equal(HistoricalProfileAvailability.Stale, entry.Availability);
        Assert.Equal(HistoricalFailureReason.NetworkOffline, entry.FailureReason);
        Assert.NotNull(entry.Profile);
    }

    [Fact]
    public async Task ConcurrentRequestsForSamePlayerAreDeduplicated()
    {
        var clock = new HistoricalManualTimeProvider(Now);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new RecordingHistoricalTransport(async (player, query, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return HistoricalProfileTransportResult.WithProfile(
                HistoricalProfileAvailability.Available,
                HistoricalTestData.Profile(player, query.Queue, clock.GetUtcNow()));
        });
        using var coordinator = CreateCoordinator(transport, clock);
        var player = HistoricalTestData.Player(43);
        var query = new HistoricalProfileQuery(HistoricalQueue.RankedSolo);

        var first = coordinator.GetProfilesAsync([player], query, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = coordinator.GetProfilesAsync([player], query, CancellationToken.None);
        await Task.Delay(20);
        release.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task TransportConcurrencyIsBoundedAcrossTenRevealedPlayers()
    {
        var clock = new HistoricalManualTimeProvider(Now);
        var transport = new RecordingHistoricalTransport(async (player, query, cancellationToken) =>
        {
            await Task.Delay(30, cancellationToken);
            return HistoricalProfileTransportResult.WithProfile(
                HistoricalProfileAvailability.Available,
                HistoricalTestData.Profile(player, query.Queue, clock.GetUtcNow()));
        });
        using var coordinator = CreateCoordinator(transport, clock, maximumConcurrency: 2);
        var players = Enumerable.Range(50, 10).Select(HistoricalTestData.Player).ToArray();

        var result = await coordinator.GetProfilesAsync(
            players,
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);

        Assert.Equal(HistoricalProfileAvailability.Available, result.Availability);
        Assert.Equal(10, transport.CallCount);
        Assert.InRange(transport.MaximumObservedConcurrency, 1, 2);
    }

    [Fact]
    public async Task LastCallerCancellationStopsUnderlyingTransport()
    {
        var clock = new HistoricalManualTimeProvider(Now);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transportCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new RecordingHistoricalTransport(async (player, query, cancellationToken) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                transportCancelled.TrySetResult();
                throw;
            }

            return HistoricalProfileTransportResult.WithProfile(
                HistoricalProfileAvailability.Available,
                HistoricalTestData.Profile(player, query.Queue, clock.GetUtcNow()));
        });
        using var coordinator = CreateCoordinator(transport, clock);
        using var cancellation = new CancellationTokenSource();
        var pending = coordinator.GetProfilesAsync(
            [HistoricalTestData.Player(60)],
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await transportCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => transport.ActiveCount == 0, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CancellingOneOfTwoDeduplicatedCallersKeepsSharedTransportAlive()
    {
        var clock = new HistoricalManualTimeProvider(Now);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new RecordingHistoricalTransport(async (player, query, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return HistoricalProfileTransportResult.WithProfile(
                HistoricalProfileAvailability.Available,
                HistoricalTestData.Profile(player, query.Queue, clock.GetUtcNow()));
        });
        using var coordinator = CreateCoordinator(transport, clock);
        var player = HistoricalTestData.Player(64);
        var query = new HistoricalProfileQuery(HistoricalQueue.RankedSolo);
        using var cancellation = new CancellationTokenSource();

        var cancelledCaller = coordinator.GetProfilesAsync([player], query, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var survivingCaller = coordinator.GetProfilesAsync([player], query, CancellationToken.None);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledCaller);
        Assert.Equal(1, transport.ActiveCount);
        release.TrySetResult();
        Assert.Equal(HistoricalProfileAvailability.Available, (await survivingCaller).Availability);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task CoordinatorClassifiesTransportDeadlineAsTimeout()
    {
        var clock = new HistoricalManualTimeProvider(Now);
        var transport = new RecordingHistoricalTransport(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable synthetic branch");
        });
        using var coordinator = CreateCoordinator(
            transport,
            clock,
            requestTimeout: TimeSpan.FromMilliseconds(40));

        var result = await coordinator.GetProfilesAsync(
            [HistoricalTestData.Player(61)],
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(HistoricalProfileAvailability.Timeout, entry.Availability);
        Assert.Equal(HistoricalFailureReason.RequestTimedOut, entry.FailureReason);
        Assert.Null(entry.Profile);
    }

    [Fact]
    public async Task WrongQueueSchemaIsClassifiedAsMalformed()
    {
        var clock = new HistoricalManualTimeProvider(Now);
        var transport = new RecordingHistoricalTransport((player, _, _) =>
            Task.FromResult(HistoricalProfileTransportResult.WithProfile(
                HistoricalProfileAvailability.Available,
                HistoricalTestData.Profile(player, HistoricalQueue.Aram, clock.GetUtcNow()))));
        using var coordinator = CreateCoordinator(transport, clock);

        var result = await coordinator.GetProfilesAsync(
            [HistoricalTestData.Player(62)],
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(HistoricalProfileAvailability.Malformed, entry.Availability);
        Assert.Equal(HistoricalFailureReason.InvalidResponse, entry.FailureReason);
        Assert.Null(entry.Profile);
    }

    [Fact]
    public async Task HttpFailureIsClassifiedAsOfflineWithoutLeakingExceptionText()
    {
        var clock = new HistoricalManualTimeProvider(Now);
        var transport = new RecordingHistoricalTransport((_, _, _) =>
            throw new HttpRequestException("synthetic private response body"));
        using var coordinator = CreateCoordinator(transport, clock);

        var result = await coordinator.GetProfilesAsync(
            [HistoricalTestData.Player(63)],
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(HistoricalProfileAvailability.Offline, entry.Availability);
        Assert.Equal(HistoricalFailureReason.NetworkOffline, entry.FailureReason);
        Assert.Null(entry.Profile);
    }

    private static HistoricalProfileCoordinator CreateCoordinator(
        IHistoricalProfileTransport transport,
        TimeProvider clock,
        TimeSpan? requestTimeout = null,
        TimeSpan? freshLifetime = null,
        TimeSpan? staleLifetime = null,
        int maximumConcurrency = 3) =>
        new(
            transport,
            new HistoricalProfileCoordinatorOptions(
                requestTimeout ?? TimeSpan.FromSeconds(2),
                freshLifetime ?? TimeSpan.FromMinutes(5),
                staleLifetime ?? TimeSpan.FromMinutes(30),
                maximumConcurrency),
            clock);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Synthetic condition was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class RecordingHistoricalTransport : IHistoricalProfileTransport
    {
        private readonly Func<RevealedPlayerIdentity, HistoricalProfileQuery, CancellationToken, Task<HistoricalProfileTransportResult>> _handler;
        private int _callCount;
        private int _activeCount;
        private int _maximumObservedConcurrency;

        public RecordingHistoricalTransport(
            Func<RevealedPlayerIdentity, HistoricalProfileQuery, CancellationToken, Task<HistoricalProfileTransportResult>> handler) =>
            _handler = handler;

        public int CallCount => Volatile.Read(ref _callCount);
        public int ActiveCount => Volatile.Read(ref _activeCount);
        public int MaximumObservedConcurrency => Volatile.Read(ref _maximumObservedConcurrency);

        public async Task<HistoricalProfileTransportResult> FetchAsync(
            RevealedPlayerIdentity player,
            HistoricalProfileQuery query,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            var active = Interlocked.Increment(ref _activeCount);
            UpdateMaximum(active);
            try
            {
                return await _handler(player, query, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCount);
            }
        }

        private void UpdateMaximum(int observed)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumObservedConcurrency);
                if (observed <= current ||
                    Interlocked.CompareExchange(ref _maximumObservedConcurrency, observed, current) == current)
                {
                    return;
                }
            }
        }
    }
}
