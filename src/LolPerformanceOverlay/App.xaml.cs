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
    private SettingsStore? _settingsStore;
    private AppSettings? _settings;
    private DataDragonProvider? _staticData;
    private ILeagueSessionSource? _sessionSource;
    private PerformanceScorer? _scorer;
    private IHistoricalProfileProvider? _historicalProvider;
    private readonly OverlayUpdateReducer _updateReducer = new(TimeSpan.FromMilliseconds(250));
    private readonly object _flushGate = new();
    private OverlayWindow? _overlay;
    private TrayIconService? _tray;
    private GlobalHotkey? _hotkey;
    private CancellationTokenSource? _endHide;
    private CancellationTokenSource? _saveDebounce;
    private CancellationTokenSource? _historyLookup;
    private CancellationTokenSource? _flushDelay;
    private LeagueSessionFrame? _pendingFrame;
    private string? _historyRosterKey;
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
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        _settingsStore = new SettingsStore();
        _settings = _settingsStore.Load();
        _isDemo = e.Args.Any(argument =>
            argument.Equals("--demo", StringComparison.OrdinalIgnoreCase) ||
            argument.Equals("--demo-expanded", StringComparison.OrdinalIgnoreCase));
        _demoExpanded = e.Args.Any(argument =>
            argument.Equals("--demo-expanded", StringComparison.OrdinalIgnoreCase));

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
            _ = _settingsStore.SaveAsync(_settings);
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
        try
        {
            await Task.Run(() => _staticData.InitializeAsync(_shutdown.Token), _shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            // Static names and icons may fall back to safe unknown values; startup must still recover.
        }
        _scorer = new PerformanceScorer();
        _historicalProvider = _isDemo
            ? new SyntheticHistoricalProfileProvider()
            : HistoricalProfileProviders.CreateShippingDefault();
        _sessionSource = _isDemo
            ? new ReplaySessionSource(_staticData)
            : new LeagueSessionSource(_staticData);

        _ = Task.Run(() => RunSessionLoopAsync(_shutdown.Token), _shutdown.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _saveDebounce?.Cancel();
        _saveDebounce?.Dispose();
        _saveDebounce = null;
        if (!_isDemo && _settingsStore is not null && _settings is not null)
        {
            try
            {
                using var finalSaveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                _settingsStore.SaveAsync(_settings, finalSaveTimeout.Token).GetAwaiter().GetResult();
            }
            catch
            {
                // Shutdown must proceed even if the user profile has become unwritable.
            }
        }

        _shutdown.Cancel();
        _endHide?.Cancel();
        _historyLookup?.Cancel();
        CancelScheduledFlush();
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
        _singleInstance?.ReleaseMutex();
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
                    await Dispatcher.InvokeAsync(() => ApplyFrame(frame, update));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            await Dispatcher.InvokeAsync(() => _overlay?.Hide());
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
                    _scorer?.Reset();
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

        var dialog = new SettingsWindow(_settings);
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

        _ = _settingsStore?.SaveAsync(_settings);
    }

    private void SetStartup(bool enabled)
    {
        if (_settings is null)
        {
            return;
        }

        _settings.StartWithWindows = enabled;
        StartupManager.SetEnabled(enabled);
        _ = _settingsStore?.SaveAsync(_settings);
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
        _ = _settingsStore?.SaveAsync(_settings);
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
                _historyLookup?.Cancel();
                _historyLookup?.Dispose();
                _historyLookup = null;
                _historyRosterKey = null;
            }

            return;
        }

        var identities = snapshot.Teams
            .SelectMany(team => team.Players)
            .Where(player => !player.IsAnonymous)
            .Select(player => TryCreateRevealedIdentity(player, frame.PlatformRegion))
            .Where(identity => identity is not null)
            .Cast<RevealedPlayerIdentity>()
            .Take(10)
            .ToArray();
        if (identities.Length == 0)
        {
            return;
        }

        var rosterKey = string.Join('|', identities.Select(identity => identity.StableKey).OrderBy(key => key));
        if (string.Equals(_historyRosterKey, rosterKey, StringComparison.Ordinal))
        {
            return;
        }

        _historyRosterKey = rosterKey;
        _historyLookup?.Cancel();
        _historyLookup?.Dispose();
        _historyLookup = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        _ = RefreshHistoricalProfilesAsync(identities, frame, rosterKey, _historyLookup.Token);
    }

    private async Task RefreshHistoricalProfilesAsync(
        IReadOnlyList<RevealedPlayerIdentity> identities,
        LeagueSessionFrame frame,
        string rosterKey,
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
            await Dispatcher.InvokeAsync(() =>
            {
                if (string.Equals(_historyRosterKey, rosterKey, StringComparison.Ordinal))
                {
                    _overlay?.ApplyHistoricalProfiles(result);
                }
            });
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

    private void OnOverlayPositionChanged(double left, double top)
    {
        if (_settings is null || _isDemo)
        {
            return;
        }

        _settings.Left = left;
        _settings.Top = top;
        _saveDebounce?.Cancel();
        _saveDebounce?.Dispose();
        _saveDebounce = new CancellationTokenSource();
        var token = _saveDebounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token);
                if (_settingsStore is not null)
                {
                    await _settingsStore.SaveAsync(_settings, token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
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
                await Dispatcher.InvokeAsync(() => _overlay?.Hide());
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
        CancellationTokenSource? scheduled = null;
        var delay = Timeout.InfiniteTimeSpan;
        OverlayUpdate? update;
        lock (_flushGate)
        {
            update = _updateReducer.Offer(snapshot);
            if (update is not null)
            {
                _flushDelay?.Cancel();
                _flushDelay = null;
                _pendingFrame = null;
            }
            else
            {
                delay = _updateReducer.DelayUntilFlush;
                if (delay != Timeout.InfiniteTimeSpan)
                {
                    _pendingFrame = frame;
                    _flushDelay?.Cancel();
                    scheduled = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
                    _flushDelay = scheduled;
                }
            }
        }

        if (scheduled is not null)
        {
            _ = FlushReducerAfterDelayAsync(delay, scheduled);
        }

        return update;
    }

    private async Task FlushReducerAfterDelayAsync(
        TimeSpan delay,
        CancellationTokenSource scheduled)
    {
        try
        {
            await Task.Delay(delay, scheduled.Token).ConfigureAwait(false);
            OverlayUpdate? update;
            LeagueSessionFrame? frame;
            lock (_flushGate)
            {
                if (!ReferenceEquals(_flushDelay, scheduled))
                {
                    return;
                }

                update = _updateReducer.Flush();
                frame = _pendingFrame;
                _pendingFrame = null;
                _flushDelay = null;
            }

            if (update is not null && frame is not null)
            {
                await Dispatcher.InvokeAsync(() => ApplyFrame(frame, update));
            }
        }
        catch (OperationCanceledException) when (scheduled.IsCancellationRequested)
        {
        }
        finally
        {
            scheduled.Dispose();
        }
    }

    private void CancelScheduledFlush()
    {
        lock (_flushGate)
        {
            _flushDelay?.Cancel();
            _flushDelay = null;
            _pendingFrame = null;
        }
    }
}
