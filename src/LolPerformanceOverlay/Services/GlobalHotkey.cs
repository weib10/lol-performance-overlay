using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using LolPerformanceOverlay.Core;

namespace LolPerformanceOverlay.Services;

public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0x4C4F;
    private const int WmHotkey = 0x0312;
    private readonly HwndSource _source;
    private bool _registered;

    public GlobalHotkey(IntPtr windowHandle)
    {
        _source = HwndSource.FromHwnd(windowHandle) ??
                  throw new InvalidOperationException("Overlay window handle is unavailable.");
        _source.AddHook(WndProc);
    }

    public event Action? Pressed;

    public bool Register(string text)
    {
        Unregister();
        if (!TryParse(text, out var modifiers, out var key))
        {
            return false;
        }

        const uint noRepeat = 0x4000;
        _registered = RegisterHotKey(_source.Handle, HotkeyId, modifiers | noRepeat, (uint)key);
        return _registered;
    }

    public static bool TryParse(string text, out uint modifiers, out int virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!HotkeyGestureParser.TryParse(text, out var gesture))
        {
            return false;
        }

        try
        {
            var key = (Key)new KeyConverter().ConvertFromInvariantString(gesture.KeyToken)!;
            modifiers = (uint)gesture.Modifiers;
            virtualKey = KeyInterop.VirtualKeyFromKey(key);
            return modifiers != 0 && virtualKey != 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
    }

    private void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
