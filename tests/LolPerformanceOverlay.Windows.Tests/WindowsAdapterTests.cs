using System.Reflection;
using System.IO;
using LolPerformanceOverlay.Services;
using LolPerformanceOverlay.UI;
using Xunit;

namespace LolPerformanceOverlay.Windows.Tests;

public sealed class WindowsAdapterTests
{
    [Fact]
    public async Task SettingsStoreRoundTripsDefaultCoordinatesAndUserChoices()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lol-overlay-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var original = new AppSettings
            {
                Opacity = 0.71,
                PositionLocked = true,
                Hotkey = "Alt+Shift+L"
            };

            await store.SaveAsync(original);
            var restored = store.Load();

            Assert.True(double.IsNaN(restored.Left));
            Assert.True(double.IsNaN(restored.Top));
            Assert.Equal(0.71, restored.Opacity);
            Assert.True(restored.PositionLocked);
            Assert.Equal("Alt+Shift+L", restored.Hotkey);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void WindowsHotkeyAdapterMapsValidatedGestureToVirtualKey()
    {
        Assert.True(GlobalHotkey.TryParse("Ctrl+Shift+O", out var modifiers, out var virtualKey));
        Assert.NotEqual(0u, modifiers);
        Assert.NotEqual(0, virtualKey);
    }

    [Fact]
    public void OverlayAdapterExposesRecoveryAndLockOperations()
    {
        var methods = typeof(OverlayWindow)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(OverlayWindow.ResetPosition), methods);
        Assert.Contains(nameof(OverlayWindow.SetPositionLocked), methods);
        Assert.Contains(nameof(OverlayWindow.ApplySnapshot), methods);
        Assert.Contains(nameof(OverlayWindow.ApplyHistoricalProfiles), methods);
        Assert.Contains(nameof(OverlayWindow.ClearHistoricalProfiles), methods);
    }

    [Fact]
    public async Task ChampionBitmapIsDecodedOncePerPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lol-overlay-synthetic-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(path, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII="));
            var cache = new ChampionImageCache();

            var first = await cache.GetAsync(path);
            var second = await cache.GetAsync(path);

            Assert.NotNull(first);
            Assert.Same(first, second);
            Assert.Equal(1, cache.DecodeCount);
            Assert.Equal(1, cache.CacheHits);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FailedChampionDecodeIsEvictedAndCanRecover()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lol-overlay-synthetic-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllTextAsync(path, "not an image");
            var cache = new ChampionImageCache();

            Assert.Null(await cache.GetAsync(path));
            Assert.False(File.Exists(path));
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII="));

            Assert.NotNull(await cache.GetAsync(path));
            Assert.Equal(1, cache.DecodeCount);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
