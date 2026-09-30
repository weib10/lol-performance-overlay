using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;
using LolPerformanceOverlay.Core;
using LolPerformanceOverlay.Core.Interaction;
using LolPerformanceOverlay.Services;
using LolPerformanceOverlay.UI;
using Xunit;

namespace LolPerformanceOverlay.Windows.Tests;

/// <summary>
/// The window side of PerMonitorV2 placement. Each test runs on an STA thread switched to the
/// PerMonitorV2 context first: the test host has no manifest of its own, so it is system-aware
/// otherwise, and every monitor would report the system DPI.
/// </summary>
public sealed class PerMonitorDpiTests
{
    private const int WmWindowPosChanged = 0x0047;
    private const int WmDpiChanged = 0x02E0;
    private static readonly DipSize Dot = new(38, 38);

    [Fact]
    public void SavedPositionsRoundTripAndTheLegacyFieldsAreNotWritten()
    {
        WithSettingsFile(async path =>
        {
            var store = new SettingsStore(path);
            await store.SaveAsync(AppSettingsSnapshot.Capture(new AppSettings { PositionX = -48, PositionY = 900 }));

            var restored = store.Load();

            Assert.Equal(-48d, (double?)restored.PositionX);
            Assert.Equal(900d, (double?)restored.PositionY);
            Assert.Null(restored.Left);
            Assert.DoesNotContain("\"Left\"", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.DoesNotContain("\"Top\"", File.ReadAllText(path), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AFileFromTheDipVersionsIsReadSoItCanBeMigrated()
    {
        WithSettingsFile(path =>
        {
            File.WriteAllText(path, "{\"Left\": 2000, \"Top\": 500, \"Hotkey\": \"Alt+Shift+L\"}");

            var settings = new SettingsStore(path).Load();

            Assert.Equal(2000, settings.Left);
            Assert.Equal(500, settings.Top);
            Assert.Null(settings.PositionX);
            Assert.Equal("Alt+Shift+L", settings.Hotkey);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("2147483647", null)]
    [InlineData("2147483648", null)]
    [InlineData("1e10", null)]
    [InlineData("12.5", 13d)]
    public void ADamagedCoordinateCostsOnlyThePositionNotTheRestOfTheFile(string positionX, double? expected)
    {
        WithSettingsFile(path =>
        {
            File.WriteAllText(
                path,
                $"{{\"PositionX\": {positionX}, \"PositionY\": 5, \"Left\": 1e308, \"Top\": 1, " +
                "\"Hotkey\": \"Alt+Shift+L\", \"Opacity\": 0.71, \"RiotApiKey\": \"k1\"}");

            var settings = new SettingsStore(path).Load();

            Assert.Equal(expected, (double?)settings.PositionX);
            Assert.Equal(expected is null ? null : 5d, (double?)settings.PositionY);
            Assert.Null(settings.Left);
            Assert.Equal("Alt+Shift+L", settings.Hotkey);
            Assert.Equal(0.71, settings.Opacity);
            Assert.Equal("k1", settings.RiotApiKey);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void TheBuiltExecutableEmbedsAPerMonitorV2Manifest()
    {
        // The apphost the test output directory gets from the project reference; the shipped
        // single-file EXE uses the same manifest and is also gated by PackageBuilder.
        var executable = Path.Combine(AppContext.BaseDirectory, "LolPerformanceOverlay.exe");
        Assert.True(File.Exists(executable), $"No apphost at {executable}.");

        var module = LoadLibraryEx(executable, IntPtr.Zero, LoadLibraryAsDataFile | LoadLibraryAsImageResource);
        Assert.NotEqual(IntPtr.Zero, module);
        try
        {
            var resource = FindResource(module, new IntPtr(1), new IntPtr(24));
            Assert.NotEqual(IntPtr.Zero, resource);
            var size = SizeofResource(module, resource);
            var data = LockResource(LoadResource(module, resource));
            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, bytes.Length);

            Assert.Matches("<dpiAwareness[^>]*>\\s*PerMonitorV2", Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    [Fact]
    public void ADotIsSizedBeforeItHasAWindowHandle()
    {
        RunOnPerMonitorV2Thread(() =>
        {
            var window = new OverlayWindow(new AppSettings());

            Assert.Equal(38, window.Width);
            Assert.Equal(38, window.Height);
        });
    }

    [Fact]
    public void StartupPlacesTheWindowAtTheSavedPositionAndReportsItExactlyOnce()
    {
        RunOnPerMonitorV2Thread(() =>
        {
            var area = PrimaryWorkArea();
            var saved = new PixelPoint(area.X + 200, area.Y + 200);
            var reported = new List<PixelPoint>();
            var window = new OverlayWindow(new AppSettings { PositionX = saved.X, PositionY = saved.Y });
            window.PositionChanged += (x, y) => reported.Add(new PixelPoint(x, y));

            var handle = new WindowInteropHelper(window).EnsureHandle();
            try
            {
                Assert.Equal(saved, TopLeft(handle));
                Assert.Equal(saved, Assert.Single(reported));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ALegacyPositionIsConvertedAtStartupAndReportedOnce()
    {
        RunOnPerMonitorV2Thread(() =>
        {
            var displays = Displays();
            var expected = OverlayPlacement.FromLegacyDips(new DipPoint(300, 300), displays);
            var reported = new List<PixelPoint>();
            var window = new OverlayWindow(new AppSettings { Left = 300, Top = 300 });
            window.PositionChanged += (x, y) => reported.Add(new PixelPoint(x, y));

            var handle = new WindowInteropHelper(window).EnsureHandle();
            try
            {
                Assert.Equal(expected, TopLeft(handle));
                Assert.Equal(expected, Assert.Single(reported));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ResetGoesToThePrimaryCornerAndReportsIt()
    {
        RunOnPerMonitorV2Thread(() =>
        {
            var area = PrimaryWorkArea();
            var reported = new List<PixelPoint>();
            var window = new OverlayWindow(new AppSettings { PositionX = area.X + 200, PositionY = area.Y + 200 });
            window.PositionChanged += (x, y) => reported.Add(new PixelPoint(x, y));
            var handle = new WindowInteropHelper(window).EnsureHandle();
            try
            {
                window.ResetPosition();

                var expected = OverlayPlacement.Reset(Dot, Displays());
                Assert.Equal(expected, TopLeft(handle));
                Assert.Equal(expected, reported[^1]);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SavingSettingsThatCarryNoPositionLeavesTheWindowWhereItIs()
    {
        RunOnPerMonitorV2Thread(() =>
        {
            var area = PrimaryWorkArea();
            var saved = new PixelPoint(area.X + 200, area.Y + 200);
            var window = new OverlayWindow(new AppSettings { PositionX = saved.X, PositionY = saved.Y });
            var handle = new WindowInteropHelper(window).EnsureHandle();
            try
            {
                window.ApplySettings(new AppSettings { Opacity = 0.8 });
                Drain();

                Assert.Equal(saved, TopLeft(handle));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwitchingToExpandedInTheCornerNeverLeavesTheWorkAreaAndSitsOnItsMargin(bool withPlayers)
    {
        RunOnPerMonitorV2Thread(() =>
        {
            var display = PrimaryDisplay();
            var area = display.WorkArea;
            var margin = (int)Math.Round(10 * display.DpiX / 96d, MidpointRounding.AwayFromZero);
            var dot = (int)Math.Ceiling(38 * display.DpiX / 96d);
            var corner = new PixelPoint(area.X + area.Width - margin - dot, area.Y + area.Height - margin - dot);
            var window = new OverlayWindow(new AppSettings { PositionX = corner.X, PositionY = corner.Y });
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var rects = Record(handle);
            try
            {
                window.ShowWithoutActivation();
                if (withPlayers)
                {
                    window.ApplySnapshot(TenPlayers(LeaguePhase.InGame));
                }

                Drain();
                rects.Clear();
                window.SetMode(OverlayMode.Expanded);
                Drain();

                Assert.NotEmpty(rects);
                Assert.All(rects, rect => AssertInside(area, rect));
                var final = Rect(handle);
                Assert.Equal(area.Y + area.Height - margin, final.Y + final.Height);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AnExpandedPanelThatGrowsAtTheBottomIsPulledBackInside()
    {
        RunOnPerMonitorV2Thread(() =>
        {
            var display = PrimaryDisplay();
            var area = display.WorkArea;
            var window = new OverlayWindow(new AppSettings { PositionX = area.X + 100, PositionY = area.Y + area.Height - 200 });
            var handle = new WindowInteropHelper(window).EnsureHandle();
            try
            {
                window.SetMode(OverlayMode.Expanded);
                Drain();
                var before = Rect(handle);

                window.ApplySnapshot(TenPlayers(LeaguePhase.InGame));
                Drain();

                var after = Rect(handle);
                Assert.True(after.Height > before.Height, "The ten-player panel should be taller than the idle one.");
                AssertInside(area, after);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CompactTakesChampSelectsHeightAndGivesItBack()
    {
        RunOnPerMonitorV2Thread(() =>
        {
            var area = PrimaryWorkArea();
            var window = new OverlayWindow(new AppSettings { PositionX = area.X + 100, PositionY = area.Y + 100 });
            new WindowInteropHelper(window).EnsureHandle();
            try
            {
                window.SetMode(OverlayMode.Compact);
                Assert.Equal(112, window.Height);

                window.ApplySnapshot(TenPlayers(LeaguePhase.ChampSelect));
                Assert.Equal(120, window.Height);

                window.ApplySnapshot(TenPlayers(LeaguePhase.InGame));
                Assert.Equal(112, window.Height);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Only meaningful with two monitors at different scales, so it runs only where
    /// LOL_OVERLAY_MIXED_DPI_TESTS=1 is set (the development desktop) and is reported as
    /// skipped elsewhere. With the variable set it must find mixed DPI or fail.
    /// </summary>
    [MixedDpiFact]
    public void OnARealMixedDpiDesktopModeSwitchesAtTheBorderNeverChangeDpi()
    {
        RunOnPerMonitorV2Thread(() =>
        {
            var displays = Displays();
            var primary = displays.Single(display => display.IsPrimary);
            var other = displays.FirstOrDefault(display => display.DpiX != primary.DpiX);
            Assert.True(other is not null, "LOL_OVERLAY_MIXED_DPI_TESTS is set but no monitor has a different DPI from the primary.");

            // Mode switches right against the monitor's inner edge, the way a user parks the
            // Dot next to the neighbouring screen.
            var edge = RightEdgeDot(other!);
            var window = new OverlayWindow(new AppSettings { PositionX = edge.X, PositionY = edge.Y });
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var rects = Record(handle, out var dpiMessages);
            try
            {
                window.ShowWithoutActivation();
                window.ApplySnapshot(TenPlayers(LeaguePhase.InGame));
                Drain();
                Assert.Equal(other!.DpiX, GetDpiForWindow(handle));
                Assert.Equal(other.Id, window.HomeDisplayId);

                rects.Clear();
                dpiMessages.Clear();
                foreach (var mode in new[] { OverlayMode.Compact, OverlayMode.Expanded, OverlayMode.Dot })
                {
                    window.SetMode(mode);
                    Drain();
                }

                Assert.Empty(dpiMessages);
                Assert.All(rects, rect => AssertInside(other.WorkArea, rect));
                Assert.Equal(other.DpiX, GetDpiForWindow(handle));
            }
            finally
            {
                window.Close();
            }

            // Repeated crossings through the paths that move home: startup onto the other
            // monitor and reset back to the primary. Counters are read before Close, because a
            // clamp still queued for a closed window must not count.
            var dpiChanges = 0;
            var forced = 0;
            var untriggered = 0;
            for (var iteration = 0; iteration < 50; iteration++)
            {
                var repeat = new OverlayWindow(new AppSettings { PositionX = edge.X, PositionY = edge.Y });
                var repeatHandle = new WindowInteropHelper(repeat).EnsureHandle();
                try
                {
                    repeat.ShowWithoutActivation();
                    Drain();
                    Assert.Equal(other.DpiX, GetDpiForWindow(repeatHandle));
                    Assert.Equal(other.Id, repeat.HomeDisplayId);
                    repeat.SetMode(OverlayMode.Expanded);
                    repeat.SetMode(OverlayMode.Dot);
                    repeat.ResetPosition();
                    Drain();
                    Assert.Equal(primary.DpiX, GetDpiForWindow(repeatHandle));

                    dpiChanges += repeat.DpiChangeCount;
                    forced += repeat.ForcedDpiPlacementCount;
                    untriggered += repeat.UntriggeredStagingCount;
                }
                finally
                {
                    repeat.Close();
                }
            }

            Assert.True(dpiChanges >= 100, $"Only {dpiChanges} DPI changes in 50 round trips.");
            Assert.Equal(0, forced);
            Assert.Equal(0, untriggered);
        });
    }

    private static PixelPoint RightEdgeDot(PhysicalDisplayWorkArea display)
    {
        var area = display.WorkArea;
        var margin = (int)Math.Round(10 * display.DpiX / 96d, MidpointRounding.AwayFromZero);
        var dot = (int)Math.Ceiling(38 * display.DpiX / 96d);
        return new PixelPoint(area.X + area.Width - margin - dot, area.Y + area.Height / 2);
    }

    private static OverlaySnapshot TenPlayers(LeaguePhase phase)
    {
        OverlayPlayer Player(int number) => new(
            $"synthetic-{number}",
            $"測試玩家 {number}",
            number <= 5 ? 100 : 200,
            "Ashe",
            null,
            false,
            50,
            "持平",
            PerformanceConfidence.High,
            PickOrder: phase == LeaguePhase.ChampSelect ? number : null);

        return new OverlaySnapshot(
            phase,
            DateTimeOffset.Now,
            "本場即時表現",
            "雙方接近",
            null,
            100,
            100,
            1,
            PerformanceConfidence.High,
            [
                new OverlayTeam(100, "藍方", 50, Enumerable.Range(1, 5).Select(Player).ToArray()),
                new OverlayTeam(200, "紅方", 50, Enumerable.Range(6, 5).Select(Player).ToArray())
            ]);
    }

    private static List<PixelRect> Record(IntPtr handle) => Record(handle, out _);

    /// <summary>Every rectangle the window takes, and every WM_DPICHANGED it receives.</summary>
    private static List<PixelRect> Record(IntPtr handle, out List<uint> dpiMessages)
    {
        var rects = new List<PixelRect>();
        var messages = new List<uint>();
        HwndSource.FromHwnd(handle)!.AddHook((IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message == WmWindowPosChanged)
            {
                rects.Add(Rect(window));
            }
            else if (message == WmDpiChanged)
            {
                messages.Add((uint)(wParam.ToInt64() & 0xFFFF));
            }

            return IntPtr.Zero;
        });
        dpiMessages = messages;
        return rects;
    }

    private static void AssertInside(PixelRect area, PixelRect rect) =>
        Assert.True(
            rect.X >= area.X && rect.Y >= area.Y &&
            rect.X + rect.Width <= area.X + area.Width &&
            rect.Y + rect.Height <= area.Y + area.Height,
            $"{rect} leaves the work area {area}.");

    private static IReadOnlyList<PhysicalDisplayWorkArea> Displays() => DisplayMonitors.Enumerate(96, 96);

    private static PhysicalDisplayWorkArea PrimaryDisplay() => Displays().Single(display => display.IsPrimary);

    private static PixelRect PrimaryWorkArea() => PrimaryDisplay().WorkArea;

    private static PixelPoint TopLeft(IntPtr handle)
    {
        var rect = Rect(handle);
        return new PixelPoint(rect.X, rect.Y);
    }

    private static PixelRect Rect(IntPtr handle)
    {
        Assert.True(GetWindowRect(handle, out var native));
        return new PixelRect(native.Left, native.Top, native.Right - native.Left, native.Bottom - native.Top);
    }

    /// <summary>Runs everything queued on this thread's dispatcher down to idle priority.</summary>
    private static void Drain()
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        }
    }

    private static void WithSettingsFile(Func<string, Task> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lol-overlay-dpi-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            body(Path.Combine(directory, "settings.json")).GetAwaiter().GetResult();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void RunOnPerMonitorV2Thread(Action body)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                Assert.NotEqual(IntPtr.Zero, SetThreadDpiAwarenessContext(new IntPtr(-4)));
                body();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private const uint LoadLibraryAsDataFile = 0x00000002;
    private const uint LoadLibraryAsImageResource = 0x00000020;

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll")]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr data);

    [DllImport("kernel32.dll")]
    private static extern int SizeofResource(IntPtr module, IntPtr resource);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

/// <summary>
/// A Fact that runs only where LOL_OVERLAY_MIXED_DPI_TESTS=1. xunit 2 has no runtime skip, and
/// returning early would be reported as a pass; setting Skip here is reported as skipped.
/// </summary>
public sealed class MixedDpiFactAttribute : FactAttribute
{
    public MixedDpiFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LOL_OVERLAY_MIXED_DPI_TESTS") != "1")
        {
            Skip = "Needs two monitors at different scales; set LOL_OVERLAY_MIXED_DPI_TESTS=1 to run.";
        }
    }
}
