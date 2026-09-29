using System.Runtime.InteropServices;
using LolPerformanceOverlay.Core.Interaction;

namespace LolPerformanceOverlay.Services;

/// <summary>
/// Lists the attached monitors in physical pixels with each monitor's effective DPI. Reads Win32
/// directly so the app does not load WinForms just for <c>Screen.AllScreens</c>.
/// </summary>
public static class DisplayMonitors
{
    private const uint MonitorInfoFlagPrimary = 1;
    private const uint MonitorDefaultToPrimary = 1;
    private const int MonitorDpiTypeEffective = 0;

    public static IReadOnlyList<PhysicalDisplayWorkArea> Enumerate(uint fallbackDpiX, uint fallbackDpiY)
    {
        var displays = new List<PhysicalDisplayWorkArea>();
        EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (IntPtr monitor, IntPtr _, ref NativeRect _, IntPtr _) =>
            {
                if (TryDescribe(monitor, fallbackDpiX, fallbackDpiY) is { } display)
                {
                    displays.Add(display);
                }

                return true;
            },
            IntPtr.Zero);

        // A session without an enumerable display (for example while a remote desktop is being
        // reconnected) still has a primary monitor; placement always needs at least one.
        if (displays.Count == 0 &&
            TryDescribe(MonitorFromPoint(default, MonitorDefaultToPrimary), fallbackDpiX, fallbackDpiY) is { } primary)
        {
            displays.Add(primary with { IsPrimary = true });
        }

        return displays;
    }

    private static PhysicalDisplayWorkArea? TryDescribe(IntPtr monitor, uint fallbackDpiX, uint fallbackDpiY)
    {
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return null;
        }

        var dpiX = fallbackDpiX;
        var dpiY = fallbackDpiY;
        if (GetDpiForMonitor(monitor, MonitorDpiTypeEffective, out var monitorDpiX, out var monitorDpiY) == 0)
        {
            dpiX = monitorDpiX;
            dpiY = monitorDpiY;
        }

        return new PhysicalDisplayWorkArea(
            info.DeviceName,
            info.Monitor.ToPixelRect(),
            info.Work.ToPixelRect(),
            dpiX,
            dpiY,
            (info.Flags & MonitorInfoFlagPrimary) != 0);
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, ref NativeRect bounds, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clip,
        MonitorEnumProc callback,
        IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly PixelRect ToPixelRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }
}
