using LolPerformanceOverlay.Core;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class LatestValueDebouncerTests
{
    [Fact]
    public async Task ReusableTimerPersistsLatestValueOnlyAfterQuietPeriod()
    {
        var clock = new DebounceManualTimeProvider();
        var persisted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var debouncer = new LatestValueDebouncer<int>(
            TimeSpan.FromMilliseconds(500),
            (value, _) =>
            {
                persisted.TrySetResult(value);
                return Task.CompletedTask;
            },
            clock);

        debouncer.Signal(1);
        clock.Advance(TimeSpan.FromMilliseconds(400));
        debouncer.Signal(2);
        clock.Advance(TimeSpan.FromMilliseconds(499));
        Assert.False(persisted.Task.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(2, await persisted.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        await debouncer.FlushAsync();
        Assert.Equal(1, debouncer.PersistenceAttemptCount);
    }

    [Fact]
    public async Task SignalStormKeepsOnePendingValueAndFlushesLatestOnce()
    {
        var persisted = new List<int>();
        using var debouncer = new LatestValueDebouncer<int>(
            TimeSpan.FromHours(1),
            (value, _) =>
            {
                persisted.Add(value);
                return Task.CompletedTask;
            });

        for (var value = 0; value < 10_000; value++)
        {
            debouncer.Signal(value);
        }

        Assert.Equal(0, debouncer.PendingWorkerCount);
        Assert.Equal(0, debouncer.PersistenceAttemptCount);
        await debouncer.FlushAsync();

        Assert.Equal([9_999], persisted);
        Assert.Equal(0, debouncer.PendingWorkerCount);
        Assert.Equal(1, debouncer.PersistenceAttemptCount);
    }

    [Fact]
    public async Task ImmediateFlushSupersedesOlderPendingDebounceValue()
    {
        var clock = new DebounceManualTimeProvider();
        var persisted = new List<int>();
        using var debouncer = new LatestValueDebouncer<int>(
            TimeSpan.FromMilliseconds(500),
            (value, _) =>
            {
                persisted.Add(value);
                return Task.CompletedTask;
            },
            clock);

        debouncer.Signal(1);
        debouncer.Signal(2);
        await debouncer.FlushAsync();
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal([2], persisted);
        Assert.Equal(1, debouncer.PersistenceAttemptCount);
    }

    [Fact]
    public async Task SignalsDuringBlockedWriteCoalesceIntoOneFollowUpWrite()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new List<int>();
        var calls = 0;
        using var debouncer = new LatestValueDebouncer<int>(
            TimeSpan.FromHours(1),
            async (value, cancellationToken) =>
            {
                persisted.Add(value);
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                }
            });

        debouncer.Signal(1);
        var firstFlush = debouncer.FlushAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        for (var value = 2; value <= 1_000; value++)
        {
            debouncer.Signal(value);
        }

        var finalFlush = debouncer.FlushAsync();
        Assert.Equal(1, debouncer.PendingWorkerCount);
        release.TrySetResult();
        await Task.WhenAll(firstFlush, finalFlush);

        Assert.Equal([1, 1_000], persisted);
        Assert.Equal(2, debouncer.PersistenceAttemptCount);
        Assert.Equal(0, debouncer.PendingWorkerCount);
    }

    [Fact]
    public async Task DisposeCancelsActivePersistenceAndLeavesNoWorker()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var debouncer = new LatestValueDebouncer<int>(
            TimeSpan.FromHours(1),
            async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            });

        debouncer.Signal(1);
        var flush = debouncer.FlushAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        debouncer.Dispose();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await flush.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, debouncer.PendingWorkerCount);
        Assert.Throws<ObjectDisposedException>(() => debouncer.Signal(2));
    }

    private sealed class DebounceManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _nowTicks;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            _nowTicks += elapsed.Ticks;
            foreach (var timer in _timers.ToArray())
            {
                timer.FireIfDue(_nowTicks);
            }
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly DebounceManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private long _dueAtTicks = long.MaxValue;
            private TimeSpan _period;
            private bool _disposed;

            public ManualTimer(
                DebounceManualTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                Change(dueTime, period);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _period = period;
                _dueAtTicks = dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : checked(_owner._nowTicks + dueTime.Ticks);
                return true;
            }

            public void Dispose()
            {
                _disposed = true;
                _dueAtTicks = long.MaxValue;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void FireIfDue(long nowTicks)
            {
                if (_disposed || nowTicks < _dueAtTicks)
                {
                    return;
                }

                _dueAtTicks = _period == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : checked(nowTicks + _period.Ticks);
                _callback(_state);
            }
        }
    }
}
