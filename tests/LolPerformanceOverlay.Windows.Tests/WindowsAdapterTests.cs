using System.Reflection;
using System.IO;
using LolPerformanceOverlay.Core;
using LolPerformanceOverlay.Services;
using LolPerformanceOverlay.UI;
using LolPerformanceOverlay.Infrastructure;
using Xunit;

namespace LolPerformanceOverlay.Windows.Tests;

public sealed class WindowsAdapterTests
{
    [Fact]
    public async Task DisposingSessionSourceCancelsInflightEnumerationWithoutExternalToken()
    {
        var source = new LeagueSessionSource(new NullStaticGameDataProvider());
        await using var frames = source.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        var inFlightMove = frames.MoveNextAsync().AsTask();
        await Task.Delay(20);

        await source.DisposeAsync();

        Assert.False(await inFlightMove.WaitAsync(TimeSpan.FromSeconds(1)));
    }

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

            await store.SaveAsync(AppSettingsSnapshot.Capture(original));
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
    public void OversizedSettingsAndLockfilesAreRejectedWithoutParsing()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lol-overlay-bounds-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(directory, "settings.json");
        var lockfilePath = Path.Combine(directory, "lockfile");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(settingsPath, new string('x', 70 * 1024));
            File.WriteAllText(lockfilePath, new string('x', 4_097));

            var settings = new SettingsStore(settingsPath).Load();

            Assert.True(double.IsNaN(settings.Left));
            Assert.Null(LeagueClientDiscovery.ParseLockfile(lockfilePath));
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
    public void UnknownChampionNamesCannotBecomeAssetKeysOrPaths()
    {
        using var provider = new DataDragonProvider();

        var descriptor = provider.ResolveChampion("../synthetic-unknown");

        Assert.Equal("Unknown", descriptor.Key);
        Assert.Equal("../synthetic-unknown", descriptor.Name);
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
            Assert.InRange(first.Width, 1, 64);
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

    private sealed class NullStaticGameDataProvider : IStaticGameDataProvider
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ChampionDescriptor ResolveChampion(string championName, int championId = 0) =>
            new(championId, "Unknown", championName, [ChampionArchetype.Fighter]);

        public int GetItemGoldValue(int itemId) => 0;

        public ValueTask<string?> EnsureChampionIconAsync(
            ChampionDescriptor champion,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(null);
    }
}
