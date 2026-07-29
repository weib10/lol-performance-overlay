using System.Windows;
using System.Windows.Interop;
using LolPerformanceOverlay.Core;
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
    private OverlayWindow? _overlay;
    private TrayIconService? _tray;
    private GlobalHotkey? _hotkey;
    private CancellationTokenSource? _endHide;
    private CancellationTokenSource? _saveDebounce;
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

        _hotkey = new GlobalHotkey(handle);
        _hotkey.Pressed += CycleOverlay;
        if (!_hotkey.Register(_settings.Hotkey))
        {
            _settings.Hotkey = "Ctrl+Shift+F9";
            _hotkey.Register(_settings.Hotkey);
            _ = _settingsStore.SaveAsync(_settings);
        }

        _tray = new TrayIconService(_settings.StartWithWindows);
        _tray.CycleRequested += CycleOverlay;
        _tray.SettingsRequested += OpenSettings;
        _tray.StartupChanged += SetStartup;
        _tray.ExitRequested += Shutdown;

        _staticData = new DataDragonProvider();
        await _staticData.InitializeAsync(_shutdown.Token);
        _scorer = new PerformanceScorer();
        _sessionSource = _isDemo
            ? new ReplaySessionSource(_staticData)
            : new LeagueSessionSource(_staticData);

        _ = RunSessionLoopAsync(_shutdown.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        _endHide?.Cancel();
        _saveDebounce?.Cancel();
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
                await Dispatcher.InvokeAsync(() => ApplyFrame(frame, snapshot));
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

    private void ApplyFrame(LeagueSessionFrame frame, OverlaySnapshot snapshot)
    {
        if (_overlay is null)
        {
            return;
        }

        _overlay.ApplySnapshot(snapshot);
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
                if (enteredNewPhase)
                {
                    BeginEndHide();
                }

                break;

            default:
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
        _settings.Hotkey = dialog.Result.Hotkey;

        StartupManager.SetEnabled(_settings.StartWithWindows);
        _tray?.UpdateStartup(_settings.StartWithWindows);
        _overlay.ApplySettings(_settings);
        if (!_hotkey.Register(_settings.Hotkey))
        {
            _settings.Hotkey = "Ctrl+Shift+F9";
            _hotkey.Register(_settings.Hotkey);
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
}
