using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using LolPerformanceOverlay.Core;
using LolPerformanceOverlay.Core.Interaction;
using LolPerformanceOverlay.Core.Presentation;

namespace LolPerformanceOverlay.Services;

public sealed class AppSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Opacity { get; set; } = OverlayOpacityPolicy.Default;
    public bool StartWithWindows { get; set; }
    public bool PositionLocked { get; set; }
    public string Hotkey { get; set; } = "Ctrl+Shift+O";

    // What the Expanded panel shows beside each player's avatar. Defaults to today's behaviour
    // (champion name); PlayerNameDisplay.Resolve is the single place that decides the actual
    // text per row, including the anonymity guarantee -- this property only carries the user's
    // preference, never the decision itself.
    public PlayerNameDisplayMode NameDisplayMode { get; set; } = PlayerNameDisplayMode.ChampionName;

    // Held only in this file (%LOCALAPPDATA%\LolPerformanceOverlay\settings.json), which is
    // never committed and never bundled into a published build. A key entered here takes
    // effect on the next launch, not live -- the historical provider is constructed once at
    // startup, same as the session source and scorer.
    public string RiotApiKey { get; set; } = string.Empty;

    public AppSettings Clone() =>
        new()
        {
            Left = Left,
            Top = Top,
            Opacity = Opacity,
            StartWithWindows = StartWithWindows,
            PositionLocked = PositionLocked,
            Hotkey = Hotkey,
            RiotApiKey = RiotApiKey,
            NameDisplayMode = NameDisplayMode
        };
}

internal readonly record struct AppSettingsSnapshot(
    double Left,
    double Top,
    double Opacity,
    bool StartWithWindows,
    bool PositionLocked,
    string Hotkey,
    string RiotApiKey,
    PlayerNameDisplayMode NameDisplayMode)
{
    public static AppSettingsSnapshot Capture(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new AppSettingsSnapshot(
            settings.Left,
            settings.Top,
            settings.Opacity,
            settings.StartWithWindows,
            settings.PositionLocked,
            settings.Hotkey,
            settings.RiotApiKey,
            settings.NameDisplayMode);
    }

    public AppSettings ToSettings() => new()
    {
        Left = Left,
        Top = Top,
        Opacity = Opacity,
        StartWithWindows = StartWithWindows,
        PositionLocked = PositionLocked,
        Hotkey = Hotkey,
        RiotApiKey = RiotApiKey,
        NameDisplayMode = NameDisplayMode
    };
}

public sealed class SettingsStore
{
    private const int MaximumSettingsCharacters = 64 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private readonly string _path;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public SettingsStore()
    {
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LolPerformanceOverlay",
            "settings.json");
    }

    internal SettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            if (new FileInfo(_path).Length > MaximumSettingsCharacters)
            {
                return new AppSettings();
            }

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream);
            var buffer = new char[MaximumSettingsCharacters + 1];
            var length = reader.ReadBlock(buffer, 0, buffer.Length);
            if (length > MaximumSettingsCharacters || reader.Peek() >= 0)
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(
                               new string(buffer, 0, length),
                               JsonOptions) ??
                           new AppSettings();
            settings.Left = double.IsFinite(settings.Left) ? settings.Left : double.NaN;
            settings.Top = double.IsFinite(settings.Top) ? settings.Top : double.NaN;
            settings.Opacity = OverlayOpacityPolicy.Clamp(settings.Opacity);
            settings.NameDisplayMode = Enum.IsDefined(settings.NameDisplayMode)
                ? settings.NameDisplayMode
                : PlayerNameDisplayMode.ChampionName;
            settings.Hotkey = string.IsNullOrWhiteSpace(settings.Hotkey)
                ? "Ctrl+Shift+O"
                : settings.Hotkey.Trim();
            settings.RiotApiKey = settings.RiotApiKey?.Trim() ?? string.Empty;
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    internal async Task SaveAsync(
        AppSettingsSnapshot settings,
        CancellationToken cancellationToken = default)
    {
        var lockTaken = false;
        try
        {
            var serialized = JsonSerializer.Serialize(settings.ToSettings(), JsonOptions);
            await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockTaken = true;
            await AtomicFile.WriteAllTextAsync(_path, serialized, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Settings persistence must never interrupt the overlay.
        }
        finally
        {
            if (lockTaken)
            {
                _saveGate.Release();
            }
        }
    }
}

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LolPerformanceOverlay";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executable))
            {
                key.SetValue(ValueName, $"\"{executable}\"");
            }
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
