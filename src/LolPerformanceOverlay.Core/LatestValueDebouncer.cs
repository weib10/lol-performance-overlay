namespace LolPerformanceOverlay.Core;

/// <summary>
/// Coalesces a burst of values behind one reusable timer and at most one persistence worker.
/// Signals only replace the pending value; they never create a task, delay, or cancellation source.
/// </summary>
internal sealed class LatestValueDebouncer<T> : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _delay;
    private readonly Func<T, CancellationToken, Task> _persistAsync;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ITimer _timer;
    private TaskCompletionSource? _workerCompletion;
    private T _latest = default!;
    private long _submittedVersion;
    private long _completedVersion;
    private long _persistenceAttemptCount;
    private bool _saveRequested;
    private bool _workerActive;
    private bool _disposed;

    public LatestValueDebouncer(
        TimeSpan delay,
        Func<T, CancellationToken, Task> persistAsync,
        TimeProvider? timeProvider = null)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        _delay = delay;
        _persistAsync = persistAsync ?? throw new ArgumentNullException(nameof(persistAsync));
        _timer = (timeProvider ?? TimeProvider.System).CreateTimer(
            static state => ((LatestValueDebouncer<T>)state!).OnTimerElapsed(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    internal int PendingWorkerCount
    {
        get
        {
            lock (_gate)
            {
                return _workerActive ? 1 : 0;
            }
        }
    }

    internal long PersistenceAttemptCount => Interlocked.Read(ref _persistenceAttemptCount);

    public void Signal(T value)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _latest = value;
            _submittedVersion++;
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        Task worker;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_completedVersion < _submittedVersion)
            {
                _saveRequested = true;
                EnsureWorkerUnderLock();
            }

            worker = _workerCompletion?.Task ?? Task.CompletedTask;
        }

        return cancellationToken.CanBeCanceled
            ? worker.WaitAsync(cancellationToken)
            : worker;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsDisposed())
        {
            return;
        }

        await FlushAsync().ConfigureAwait(false);
        Dispose();
    }

    public void Dispose()
    {
        TaskCompletionSource? completion;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _saveRequested = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            completion = _workerCompletion;
        }

        _lifetime.Cancel();
        _timer.Dispose();
        if (completion is null || completion.Task.IsCompleted)
        {
            _lifetime.Dispose();
        }
        else
        {
            _ = completion.Task.ContinueWith(
                static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                _lifetime,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private bool IsDisposed()
    {
        lock (_gate)
        {
            return _disposed;
        }
    }

    private void OnTimerElapsed()
    {
        lock (_gate)
        {
            if (_disposed || _completedVersion >= _submittedVersion)
            {
                return;
            }

            _saveRequested = true;
            EnsureWorkerUnderLock();
        }
    }

    private void EnsureWorkerUnderLock()
    {
        if (_workerActive)
        {
            return;
        }

        _workerActive = true;
        _workerCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = RunWorkerAsync(_workerCompletion);
    }

    private async Task RunWorkerAsync(TaskCompletionSource completion)
    {
        await Task.Yield();
        while (true)
        {
            T value;
            long version;
            lock (_gate)
            {
                if (_disposed || !_saveRequested || _completedVersion >= _submittedVersion)
                {
                    _workerActive = false;
                    if (ReferenceEquals(_workerCompletion, completion))
                    {
                        _workerCompletion = null;
                    }

                    completion.TrySetResult();
                    return;
                }

                _saveRequested = false;
                value = _latest;
                version = _submittedVersion;
            }

            Interlocked.Increment(ref _persistenceAttemptCount);
            try
            {
                await _persistAsync(value, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch
            {
                // Persistence is best effort. A future signal may retry with a newer value.
            }

            lock (_gate)
            {
                _completedVersion = Math.Max(_completedVersion, version);
            }
        }
    }
}
