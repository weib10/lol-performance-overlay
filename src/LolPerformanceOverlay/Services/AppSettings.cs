using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using LolPerformanceOverlay.Core;
using LolPerformanceOverlay.Core.Interaction;
using LolPerformanceOverlay.Core.Presentation;

namespace LolPerformanceOverlay.Services;

public sealed class AppSettings
{
    // The overlay's top-left in physical pixels (see OverlayPlacement for why not WPF DIPs).
    // Read and written as double, not int: an int field makes System.Text.Json throw on a value
    // like 1e10 or 12.5, and SettingsStore.Load then falls back to defaults for the whole file,
    // taking the hotkey and the Riot API key with it. Load rounds and range-checks them instead.
    public double? PositionX { get; set; }
    public double? PositionY { get; set; }

    // Where versions before PerMonitorV2 kept the position, in system-DPI WPF DIPs. Read once to
    // migrate (OverlayPlacement.FromLegacyDips), then cleared, and never written again.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Left { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Top { get; set; }
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
            PositionX = PositionX,
            PositionY = PositionY,
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
    double? PositionX,
    double? PositionY,
    double? Left,
    double? Top,
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
            settings.PositionX,
            settings.PositionY,
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
        PositionX = PositionX,
        PositionY = PositionY,
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
            (settings.PositionX, settings.PositionY) = CoordinatePair(settings.PositionX, settings.PositionY, round: true);
            (settings.Left, settings.Top) = CoordinatePair(settings.Left, settings.Top, round: false);
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

    /// <summary>
    /// Keeps a saved coordinate pair only when both halves are finite and within
    /// <see cref="OverlayPlacement.MaximumCoordinateMagnitude"/>; anything else, including a pair
    /// with one half missing, means "no saved position". Physical pixels are rounded half away
    /// from zero (Math.Round's default would turn 12.5 into 12).
    /// </summary>
    private static (double? First, double? Second) CoordinatePair(double? first, double? second, bool round)
    {
        static bool Usable(double? value) =>
            value is { } number &&
            double.IsFinite(number) &&
            Math.Abs(number) <= OverlayPlacement.MaximumCoordinateMagnitude;

        if (!Usable(first) || !Usable(second))
        {
            return (null, null);
        }

        return round
            ? (Math.Round(first!.Value, MidpointRounding.AwayFromZero), Math.Round(second!.Value, MidpointRounding.AwayFromZero))
            : (first, second);
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
