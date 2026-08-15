using System.Windows;
using System.Windows.Interop;
using System.Diagnostics;
using LolPerformanceOverlay.Core;
using LolPerformanceOverlay.Core.Presentation;
using LolPerformanceOverlay.Infrastructure;
using LolPerformanceOverlay.Services;
using LolPerformanceOverlay.UI;

namespace LolPerformanceOverlay;

public partial class App : System.Windows.Application
{
    private readonly CancellationTokenSource _shutdown = new();
    private Mutex? _singleInstance;
    private bool _ownsSingleInstanceMutex;
    private SettingsStore? _settingsStore;
    private AppSettings? _settings;
    private DataDragonProvider? _staticData;
    private ILeagueSessionSource? _sessionSource;
    private PerformanceScorer? _scorer;
    private IHistoricalProfileProvider? _historicalProvider;
    private readonly OverlayUpdateReducer _updateReducer = new(TimeSpan.FromMilliseconds(250));
    private readonly object _flushGate = new();
    private readonly object _historyGate = new();
    private OverlayWindow? _overlay;
    private TrayIconService? _tray;
    private GlobalHotkey? _hotkey;
    private CancellationTokenSource? _endHide;
    private CancellationTokenSource? _historyLookup;
    private ITimer? _flushTimer;
    private LatestValueDebouncer<AppSettingsSnapshot>? _positionSave;
    private Task? _staticDataInitializationTask;
    private Task? _sessionLoopTask;
    private Task? _historyLookupTask;
    private LeagueSessionFrame? _pendingFrame;
    private string[]? _historyRosterKeys;
    private string? _historyRosterRegion;
    private int _historyRosterQueueId;
    private string? _historyRosterGameMode;
    private long _historyRosterGeneration;
    private LeaguePhase _lastPhase = LeaguePhase.None;
    private bool _demoExpanded;
    private bool _isDemo;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(
            initiallyOwned: true,
            "Local\\LolPerformanceOverlay.SingleInstance",
            out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        _flushTimer = TimeProvider.System.CreateTimer(
            static state => ((App)state!).FlushReducerFromTimer(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        _settingsStore = new SettingsStore();
        _settings = _settingsStore.Load();
        _isDemo = e.Args.Any(argument =>
            argument.Equals("--demo", StringComparison.OrdinalIgnoreCase) ||
            argument.Equals("--demo-expanded", StringComparison.OrdinalIgnoreCase));
        _demoExpanded = e.Args.Any(argument =>
            argument.Equals("--demo-expanded", StringComparison.OrdinalIgnoreCase));
        if (!_isDemo)
        {
            _positionSave = new LatestValueDebouncer<AppSettingsSnapshot>(
                TimeSpan.FromMilliseconds(500),
                (snapshot, cancellationToken) => _settingsStore.SaveAsync(snapshot, cancellationToken));
        }

        var windowSettings = _settings.Clone();
        if (_isDemo)
        {
            windowSettings.Left = double.NaN;
            windowSettings.Top = double.NaN;
        }

        _overlay = new OverlayWindow(windowSettings);
        var handle = new WindowInteropHelper(_overlay).EnsureHandle();
        _overlay.PositionChanged += OnOverlayPositionChanged;
        _overlay.SettingsRequested += OpenSettings;
        _overlay.OpenExternalLinkRequested += OpenExternalLink;

        _hotkey = new GlobalHotkey(handle);
        _hotkey.Pressed += CycleOverlay;
        var hotkeyResult = HotkeyRegistrationPolicy.Register(
            _settings.Hotkey,
            "Ctrl+Shift+F9",
            _hotkey.Register);
        if (hotkeyResult.Status == HotkeyRegistrationStatus.FallbackRegistered)
        {
            _settings.Hotkey = hotkeyResult.RegisteredGesture!;
            QueueSettingsSave(flushImmediately: true);
        }

        _tray = new TrayIconService(_settings.StartWithWindows, _settings.PositionLocked);
        _tray.CycleRequested += CycleOverlay;
        _tray.ResetPositionRequested += ResetOverlayPosition;
        _tray.SettingsRequested += OpenSettings;
        _tray.StartupChanged += SetStartup;
        _tray.PositionLockedChanged += SetPositionLocked;
        _tray.ExitRequested += Shutdown;

        if (hotkeyResult.Status == HotkeyRegistrationStatus.FallbackRegistered)
        {
            _tray.ShowNotice("快捷鍵已改用 Ctrl+Shift+F9", "原本的快捷鍵正被其他程式使用，可在設定中修改。");
        }
        else if (hotkeyResult.Status == HotkeyRegistrationStatus.Unavailable)
        {
            _tray.ShowNotice("快捷鍵目前無法使用", "設定的快捷鍵與備用快捷鍵都被占用；仍可使用系統匣切換顯示。");
        }

        _staticData = new DataDragonProvider();
        _staticDataInitializationTask = Task.Run(
            () => _staticData.InitializeAsync(_shutdown.Token),
            _shutdown.Token);
        try
        {
            await _staticDataInitializationTask;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            // Static names and icons may fall back to safe unknown values; startup must still recover.
        }

        // Shutdown can run while the asynchronous static-data initialization is in flight. Never
        // create a new session graph after OnExit has begun disposing application resources.
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        _scorer = new PerformanceScorer();
        _historicalProvider = _isDemo
            ? new SyntheticHistoricalProfileProvider()
            : HistoricalProfileProviders.CreateShippingDefault();
        _sessionSource = _isDemo
            ? new ReplaySessionSource(_staticData)
            : new LeagueSessionSource(_staticData);

        _sessionLoopTask = Task.Run(() => RunSessionLoopAsync(_shutdown.Token), _shutdown.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        CancelEndHide();
        var historyTask = CancelHistoricalLookup(clearRoster: true);
        CancelScheduledFlush();
        _flushTimer?.Dispose();
        _flushTimer = null;

        if (!_isDemo && _positionSave is not null && _settings is not null)
        {
            try
            {
                using var finalSaveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                _positionSave.Signal(AppSettingsSnapshot.Capture(_settings));
                _positionSave.FlushAsync(finalSaveTimeout.Token).GetAwaiter().GetResult();
            }
            catch
            {
                // Shutdown must proceed even if the user profile has become unwritable.
            }
        }

        _positionSave?.Dispose();
        _positionSave = null;
        WaitForBackgroundTask(historyTask, TimeSpan.FromSeconds(2));
        WaitForBackgroundTask(_sessionLoopTask, TimeSpan.FromSeconds(3));
        WaitForBackgroundTask(_staticDataInitializationTask, TimeSpan.FromSeconds(3));
        _hotkey?.Dispose();
        _tray?.Dispose();
        if (_sessionSource is not null)
        {
            try
            {
                _sessionSource.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Process shutdown should remain best effort.
            }
        }

        _staticData?.Dispose();
        (_historicalProvider as IDisposable)?.Dispose();
        if (_ownsSingleInstanceMutex)
        {
            _singleInstance?.ReleaseMutex();
            _ownsSingleInstanceMutex = false;
        }

        _singleInstance?.Dispose();
        _shutdown.Dispose();
        base.OnExit(e);
    }

    private async Task RunSessionLoopAsync(CancellationToken cancellationToken)
    {
        if (_sessionSource is null || _scorer is null)
        {
            return;
        }

        try
        {
            await foreach (var frame in _sessionSource.WatchAsync(cancellationToken))
            {
                var snapshot = _scorer.Evaluate(frame);
                BeginHistoricalLookup(frame, snapshot, cancellationToken);
                var update = OfferFrame(frame, snapshot, cancellationToken);
                if (update is not null)
                {
                    await Dispatcher.InvokeAsync(
                        () => ApplyFrame(frame, update),
                        System.Windows.Threading.DispatcherPriority.Normal,
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            if (!cancellationToken.IsCancellationRequested && !Dispatcher.HasShutdownStarted)
            {
                try
                {
                    await Dispatcher.InvokeAsync(
                        () => _overlay?.Hide(),
                        System.Windows.Threading.DispatcherPriority.Normal,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
            }
        }
    }

    private void ApplyFrame(LeagueSessionFrame frame, OverlayUpdate update)
    {
        if (_overlay is null)
        {
            return;
        }

        _overlay.ApplySnapshot(update.Snapshot, update.Diff);
        _overlay.SetPlatformRegion(frame.PlatformRegion);
        if (frame.Phase != LeaguePhase.InGame)
        {
            _overlay.ClearHistoricalProfiles();
        }

        var enteredNewPhase = frame.Phase != _lastPhase;
        if (frame.Phase is LeaguePhase.ChampSelect or LeaguePhase.Loading or LeaguePhase.InGame)
        {
            CancelEndHide();
        }

        switch (frame.Phase)
        {
            case LeaguePhase.ChampSelect:
                if (enteredNewPhase)
                {
                    _overlay.SetMode(_demoExpanded ? OverlayMode.Expanded : OverlayMode.Compact);
                }

                _overlay.ShowWithoutActivation();
                break;

            case LeaguePhase.Loading:
            case LeaguePhase.InGame:
                if (enteredNewPhase)
                {
                    _overlay.SetMode(_demoExpanded ? OverlayMode.Expanded : OverlayMode.Dot);
                }

                _overlay.ShowWithoutActivation();
                break;

            case LeaguePhase.EndOfGame:
                _overlay.ClearHistoricalProfiles();
                if (enteredNewPhase)
                {
                    BeginEndHide();
                }

                break;

            default:
                _overlay.ClearHistoricalProfiles();
                _overlay.Hide();
                break;
        }

        _lastPhase = frame.Phase;
    }

    private void CycleOverlay()
    {
        if (_overlay is null)
        {
            return;
        }

        if (!_overlay.IsVisible)
        {
            _overlay.SetMode(OverlayMode.Compact);
            _overlay.ShowWithoutActivation();
            return;
        }

        _overlay.CycleMode();
    }

    private void OpenSettings()
    {
        if (_settings is null || _overlay is null || _hotkey is null)
        {
            return;
        }

        var dialog = new SettingsWindow(_settings) { Owner = _overlay };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _settings.Left = dialog.Result.Left;
        _settings.Top = dialog.Result.Top;
        _settings.Opacity = dialog.Result.Opacity;
        _settings.StartWithWindows = dialog.Result.StartWithWindows;
        _settings.PositionLocked = dialog.Result.PositionLocked;
        _settings.Hotkey = dialog.Result.Hotkey;

        StartupManager.SetEnabled(_settings.StartWithWindows);
        _tray?.UpdateStartup(_settings.StartWithWindows);
        _tray?.UpdatePositionLocked(_settings.PositionLocked);
        _overlay.ApplySettings(_settings);
        var hotkeyResult = HotkeyRegistrationPolicy.Register(
            _settings.Hotkey,
            "Ctrl+Shift+F9",
            _hotkey.Register);
        if (hotkeyResult.Status == HotkeyRegistrationStatus.FallbackRegistered)
        {
            _settings.Hotkey = hotkeyResult.RegisteredGesture!;
            _tray?.ShowNotice("快捷鍵已改用 Ctrl+Shift+F9", "剛才選擇的快捷鍵正被其他程式使用。");
        }
        else if (hotkeyResult.Status == HotkeyRegistrationStatus.Unavailable)
        {
            _tray?.ShowNotice("快捷鍵目前無法使用", "選擇的快捷鍵與備用快捷鍵都被占用；可繼續使用系統匣。");
        }

        QueueSettingsSave(flushImmediately: true);
    }

    private void SetStartup(bool enabled)
    {
        if (_settings is null)
        {
            return;
        }

        _settings.StartWithWindows = enabled;
        StartupManager.SetEnabled(enabled);
        QueueSettingsSave(flushImmediately: true);
    }

    private void SetPositionLocked(bool locked)
    {
        if (_settings is null)
        {
            return;
        }

        _settings.PositionLocked = locked;
        _overlay?.SetPositionLocked(locked);
        _tray?.UpdatePositionLocked(locked);
        QueueSettingsSave(flushImmediately: true);
    }

    private void ResetOverlayPosition()
    {
        _overlay?.ResetPosition();
        _overlay?.ShowWithoutActivation();
    }

    private static void OpenExternalLink(Uri destination)
    {
        if (!NetworkDestinationPolicy.IsAllowed(
                destination,
                NetworkDestinationPurpose.UserInitiatedBrowser))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(destination.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // The browser link is optional; shell or browser failures must not stop the overlay.
        }
    }

    private void BeginHistoricalLookup(
        LeagueSessionFrame frame,
        OverlaySnapshot snapshot,
        CancellationToken shutdownToken)
    {
        if (_historicalProvider is null || frame.Phase != LeaguePhase.InGame)
        {
            if (frame.Phase != LeaguePhase.InGame)
            {
                CancelHistoricalLookup(clearRoster: true);
            }

            return;
        }

        if (HistoryRosterMatches(snapshot, frame))
        {
            return;
        }

        var identities = new List<RevealedPlayerIdentity>(10);
        foreach (var team in snapshot.Teams)
        {
            foreach (var player in team.Players)
            {
                if (!player.IsAnonymous &&
                    TryCreateRevealedIdentity(player, frame.PlatformRegion) is { } identity)
                {
                    identities.Add(identity);
                    if (identities.Count == 10)
                    {
                        break;
                    }
                }
            }

            if (identities.Count == 10)
            {
                break;
            }
        }

        if (identities.Count == 0)
        {
            CancelHistoricalLookup(clearRoster: true);
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        CancellationTokenSource? previousCancellation;
        Task? previousTask;
        lock (_historyGate)
        {
            if (_shutdown.IsCancellationRequested)
            {
                cancellation.Dispose();
                return;
            }

            previousCancellation = _historyLookup;
            previousTask = _historyLookupTask;
            _historyRosterKeys = identities.Select(identity => identity.StableKey).ToArray();
            _historyRosterRegion = frame.PlatformRegion;
            _historyRosterQueueId = frame.QueueId;
            _historyRosterGameMode = frame.GameMode;
            var generation = ++_historyRosterGeneration;
            _historyLookup = cancellation;
            _historyLookupTask = RefreshHistoricalProfilesAsync(
                identities,
                frame,
                generation,
                cancellation.Token);
        }

        CancelAndDisposeAfterCompletion(previousCancellation, previousTask);
    }

    private async Task RefreshHistoricalProfilesAsync(
        IReadOnlyList<RevealedPlayerIdentity> identities,
        LeagueSessionFrame frame,
        long rosterGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            var queue = frame.QueueId switch
            {
                420 => HistoricalQueue.RankedSolo,
                440 => HistoricalQueue.RankedFlex,
                450 => HistoricalQueue.Aram,
                _ => new HistoricalQueue(
                    Math.Max(frame.QueueId, 0),
                    string.IsNullOrWhiteSpace(frame.GameMode) ? "UNKNOWN" : frame.GameMode,
                    "目前模式")
            };
            var result = await _historicalProvider!.GetProfilesAsync(
                identities,
                new HistoricalProfileQuery(queue),
                cancellationToken).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(
                () =>
                {
                    if (IsCurrentHistoryGeneration(rosterGeneration))
                    {
                        _overlay?.ApplyHistoricalProfiles(result);
                    }
                },
                System.Windows.Threading.DispatcherPriority.Normal,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // History is optional enrichment. A provider failure must never stop the live session loop.
        }
    }

    private static RevealedPlayerIdentity? TryCreateRevealedIdentity(
        OverlayPlayer player,
        string? platformRegion)
    {
        var separator = player.DisplayName.LastIndexOf('#');
        if (separator <= 0 || separator == player.DisplayName.Length - 1)
        {
            return null;
        }

        return RevealedPlayerIdentity.TryCreateNormallyRevealed(
            player.StableKey,
            player.DisplayName[..separator],
            player.DisplayName[(separator + 1)..],
            platformRegion,
            out var identity)
            ? identity
            : null;
    }

    private bool HistoryRosterMatches(OverlaySnapshot snapshot, LeagueSessionFrame frame)
    {
        lock (_historyGate)
        {
            if (_historyRosterKeys is null ||
                !string.Equals(_historyRosterRegion, frame.PlatformRegion, StringComparison.OrdinalIgnoreCase) ||
                _historyRosterQueueId != frame.QueueId ||
                !string.Equals(_historyRosterGameMode, frame.GameMode, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var index = 0;
            foreach (var team in snapshot.Teams)
            {
                foreach (var player in team.Players)
                {
                    if (player.IsAnonymous || !HasRevealedIdentityShape(player, frame.PlatformRegion))
                    {
                        continue;
                    }

                    if (index >= _historyRosterKeys.Length ||
                        !string.Equals(_historyRosterKeys[index], player.StableKey, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    index++;
                    if (index == 10)
                    {
                        break;
                    }
                }

                if (index == 10)
                {
                    break;
                }
            }

            return index == _historyRosterKeys.Length;
        }
    }

    private static bool HasRevealedIdentityShape(OverlayPlayer player, string? platformRegion)
    {
        if (string.IsNullOrWhiteSpace(player.StableKey) || string.IsNullOrWhiteSpace(platformRegion))
        {
            return false;
        }

        var separator = player.DisplayName.LastIndexOf('#');
        return separator > 0 && separator < player.DisplayName.Length - 1;
    }

    private Task? CancelHistoricalLookup(bool clearRoster)
    {
        CancellationTokenSource? cancellation;
        Task? task;
        lock (_historyGate)
        {
            cancellation = _historyLookup;
            task = _historyLookupTask;
            _historyLookup = null;
            _historyLookupTask = null;

            if (clearRoster)
            {
                _historyRosterKeys = null;
                _historyRosterRegion = null;
                _historyRosterQueueId = 0;
                _historyRosterGameMode = null;
                _historyRosterGeneration++;
            }
        }

        CancelAndDisposeAfterCompletion(cancellation, task);
        return task;
    }

    private bool IsCurrentHistoryGeneration(long generation)
    {
        lock (_historyGate)
        {
            return _historyRosterGeneration == generation;
        }
    }

    private static void CancelAndDisposeAfterCompletion(
        CancellationTokenSource? cancellation,
        Task? task)
    {
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        if (task is null || task.IsCompleted)
        {
            cancellation.Dispose();
            return;
        }

        _ = task.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            cancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnOverlayPositionChanged(double left, double top)
    {
        if (_settings is null || _isDemo)
        {
            return;
        }

        _settings.Left = left;
        _settings.Top = top;
        QueueSettingsSave(flushImmediately: false);
    }

    private void QueueSettingsSave(bool flushImmediately)
    {
        if (_isDemo || _settings is null || _positionSave is null)
        {
            return;
        }

        _positionSave.Signal(AppSettingsSnapshot.Capture(_settings));
        if (flushImmediately)
        {
            _ = _positionSave.FlushAsync();
        }
    }

    private void BeginEndHide()
    {
        CancelEndHide();
        _endHide = new CancellationTokenSource();
        var token = _endHide.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), token);
                await Dispatcher.InvokeAsync(
                    () => _overlay?.Hide(),
                    System.Windows.Threading.DispatcherPriority.Normal,
                    token);
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void CancelEndHide()
    {
        _endHide?.Cancel();
        _endHide?.Dispose();
        _endHide = null;
    }

    private OverlayUpdate? OfferFrame(
        LeagueSessionFrame frame,
        OverlaySnapshot snapshot,
        CancellationToken shutdownToken)
    {
        var delay = Timeout.InfiniteTimeSpan;
        OverlayUpdate? update;
        lock (_flushGate)
        {
            if (shutdownToken.IsCancellationRequested)
            {
                return null;
            }

            update = _updateReducer.Offer(snapshot);
            if (update is not null)
            {
                _flushTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _pendingFrame = null;
            }
            else
            {
                delay = _updateReducer.DelayUntilFlush;
                if (delay != Timeout.InfiniteTimeSpan)
                {
                    _pendingFrame = frame;
                    _flushTimer?.Change(delay, Timeout.InfiniteTimeSpan);
                }
            }
        }

        return update;
    }

    private void FlushReducerFromTimer()
    {
        OverlayUpdate? update;
        LeagueSessionFrame? frame;
        try
        {
            lock (_flushGate)
            {
                if (_shutdown.IsCancellationRequested)
                {
                    return;
                }

                update = _updateReducer.Flush();
                if (update is null)
                {
                    var remaining = _updateReducer.DelayUntilFlush;
                    if (remaining != Timeout.InfiniteTimeSpan)
                    {
                        _flushTimer?.Change(remaining, Timeout.InfiniteTimeSpan);
                    }

                    return;
                }

                frame = _pendingFrame;
                _pendingFrame = null;
                _flushTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            if (frame is not null)
            {
                Dispatcher.BeginInvoke(() => ApplyFrame(frame, update));
            }
        }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted)
        {
        }
    }

    private void CancelScheduledFlush()
    {
        lock (_flushGate)
        {
            _flushTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _pendingFrame = null;
        }
    }

    private static void WaitForBackgroundTask(Task? task, TimeSpan timeout)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            task.Wait(timeout);
        }
        catch
        {
            // Cancellation and dispatcher shutdown are expected during process teardown.
        }
    }
}
