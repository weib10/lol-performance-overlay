using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using LolPerformanceOverlay.Core;

namespace LolPerformanceOverlay.Services;

public sealed class AppSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Opacity { get; set; } = 0.92;
    public bool StartWithWindows { get; set; }
    public bool PositionLocked { get; set; }
    public string Hotkey { get; set; } = "Ctrl+Shift+O";

    public AppSettings Clone() =>
        new()
        {
            Left = Left,
            Top = Top,
            Opacity = Opacity,
            StartWithWindows = StartWithWindows,
            PositionLocked = PositionLocked,
            Hotkey = Hotkey
        };
}

public sealed class SettingsStore
{
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

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ??
                           new AppSettings();
            settings.Left = double.IsFinite(settings.Left) ? settings.Left : double.NaN;
            settings.Top = double.IsFinite(settings.Top) ? settings.Top : double.NaN;
            settings.Opacity = double.IsFinite(settings.Opacity)
                ? Math.Clamp(settings.Opacity, 0.35, 1)
                : 0.92;
            settings.Hotkey = string.IsNullOrWhiteSpace(settings.Hotkey)
                ? "Ctrl+Shift+O"
                : settings.Hotkey.Trim();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var lockTaken = false;
        try
        {
            var serialized = JsonSerializer.Serialize(settings.Clone(), JsonOptions);
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
