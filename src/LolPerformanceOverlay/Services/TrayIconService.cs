using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LolPerformanceOverlay.Services;

/// <summary>
/// Notification-area icon and its right-click menu, built on Shell_NotifyIcon and a native popup
/// menu instead of WinForms' NotifyIcon, so the single-file bundle no longer carries WinForms
/// (about 10 MB less to download; memory is unchanged). See docs/PRODUCT_HANDOFF.md section 20.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    internal const int CallbackMessage = WmApp + 1;
    internal const uint IconId = 1;
    internal const int KeySelect = 0x0401;
    private const string TipText = "LoL 即時表現 Overlay";
    private const long KeySelectRepeatMilliseconds = 300;

    private const int WmNull = 0x0000;
    private const int WmContextMenu = 0x007B;
    private const int WmLButtonDoubleClick = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmApp = 0x8000;
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifTip = 0x04;
    private const uint NifInfo = 0x10;
    private const uint NifShowTip = 0x80;
    private const uint NotifyIconVersion4 = 4;
    private const uint MfString = 0x0000;
    private const uint MfChecked = 0x0008;
    private const uint MfSeparator = 0x0800;
    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmRightAlign = 0x0008;
    private const uint TpmNoNotify = 0x0080;
    private const uint TpmReturnCommand = 0x0100;
    private const int SmMenuDropAlignment = 40;
    private const int IdiApplication = 32512;

    private readonly HwndSource _window;
    private readonly int _taskbarCreatedMessage;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private bool _added;
    private bool _version4;
    private bool _menuOpen;
    private bool _disposed;
    private long _lastKeySelect = long.MinValue / 2;
    private bool _startupEnabled;
    private bool _positionLocked;

    public TrayIconService(bool startupEnabled, bool positionLocked)
    {
        _startupEnabled = startupEnabled;
        _positionLocked = positionLocked;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        // Hidden top-level rather than message-only: Explorer announces a restart by broadcast,
        // and broadcasts skip message-only windows.
        _window = new HwndSource(new HwndSourceParameters("LolPerformanceOverlay.Tray")
        {
            WindowStyle = 0,
            Width = 0,
            Height = 0
        });
        _window.AddHook(WndProc);
        (_icon, _ownsIcon) = LoadSmallIcon();
        AddIcon();
    }

    public event Action? CycleRequested;
    public event Action? SettingsRequested;
    public event Action? ResetPositionRequested;
    public event Action<bool>? StartupChanged;
    public event Action<bool>? PositionLockedChanged;
    public event Action? ExitRequested;

    internal IntPtr WindowHandle => _window.Handle;

    public void UpdateStartup(bool enabled) => _startupEnabled = enabled;

    public void UpdatePositionLocked(bool locked) => _positionLocked = locked;

    public void ShowNotice(string title, string message)
    {
        if (!_added)
        {
            return;
        }

        // Every modify restates NIF_SHOWTIP: under version 4 a modify without it can drop the
        // standard hover tooltip.
        var data = NewData(NifInfo | NifShowTip);
        data.InfoTitle = title;
        data.Info = message;
        Shell_NotifyIcon(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RemoveIcon();
        _window.RemoveHook(WndProc);
        _window.Dispose();
        if (_ownsIcon)
        {
            DestroyIcon(_icon);
        }
    }

    internal void Execute(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Cycle:
                CycleRequested?.Invoke();
                break;
            case TrayCommand.ResetPosition:
                ResetPositionRequested?.Invoke();
                break;
            case TrayCommand.TogglePositionLocked:
                _positionLocked = !_positionLocked;
                PositionLockedChanged?.Invoke(_positionLocked);
                break;
            case TrayCommand.Settings:
                SettingsRequested?.Invoke();
                break;
            case TrayCommand.ToggleStartup:
                _startupEnabled = !_startupEnabled;
                StartupChanged?.Invoke(_startupEnabled);
                break;
            case TrayCommand.Exit:
                ExitRequested?.Invoke();
                break;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == CallbackMessage)
        {
            // NOTIFYICON_VERSION_4 puts the event in the low word of lParam; the legacy version
            // sends the bare mouse message, which is the same value.
            switch (unchecked((ushort)lParam.ToInt64()))
            {
                case WmLButtonDoubleClick:
                    Post(TrayCommand.Cycle);
                    break;
                case KeySelect:
                    // Enter or Space on the focused icon; Enter reports it twice.
                    var now = Environment.TickCount64;
                    if (now - _lastKeySelect > KeySelectRepeatMilliseconds)
                    {
                        Post(TrayCommand.Cycle);
                    }

                    _lastKeySelect = now;
                    break;
                case WmContextMenu:
                    ShowMenu();
                    break;
                case WmRButtonUp when !_version4:
                    // Without version 4 the shell never sends WM_CONTEXTMENU.
                    ShowMenu();
                    break;
            }

            handled = true;
        }
        else if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            // Explorer restarted, or the taskbar changed DPI; either way the icon must be re-added.
            RemoveIcon();
            AddIcon();
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        // The menu's modal loop keeps dispatching tray callbacks, so a second right-click while it
        // is open lands here again.
        if (_menuOpen)
        {
            return;
        }

        // The cursor, not the anchor in wParam: Explorer sends that in physical pixels, which
        // only match this process's coordinates when the tray's monitor is at the system DPI.
        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        int command;
        _menuOpen = true;
        try
        {
            Append(menu, TrayCommand.Cycle, "顯示／切換");
            Append(menu, TrayCommand.ResetPosition, "重設 Overlay 位置");
            Append(menu, TrayCommand.TogglePositionLocked, "鎖定 Overlay 位置", _positionLocked);
            Append(menu, TrayCommand.Settings, "設定");
            Append(menu, TrayCommand.ToggleStartup, "登入 Windows 後常駐", _startupEnabled);
            AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
            Append(menu, TrayCommand.Exit, "結束");
            // Bold marks what a double-click on the icon does.
            SetMenuDefaultItem(menu, (uint)TrayCommand.Cycle, 0);

            // Without the foreground switch the menu stays open after a click elsewhere; without the
            // trailing WM_NULL the next right-click can need two tries (documented Win32 behavior).
            SetForegroundWindow(_window.Handle);
            var alignment = GetSystemMetrics(SmMenuDropAlignment) != 0 ? TpmRightAlign : TpmLeftAlign;
            command = TrackPopupMenuEx(
                menu,
                TpmReturnCommand | TpmNoNotify | TpmRightButton | alignment,
                cursor.X,
                cursor.Y,
                _window.Handle,
                IntPtr.Zero);
            PostMessage(_window.Handle, WmNull, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            _menuOpen = false;
            DestroyMenu(menu);
        }

        if (command != 0)
        {
            Post((TrayCommand)command);
        }
    }

    // Run commands after the window procedure returns: Exit tears this window down, and Settings
    // opens a modal dialog, neither of which belongs inside the tray callback.
    private void Post(TrayCommand command) => _window.Dispatcher.BeginInvoke(() => Execute(command));

    private static void Append(IntPtr menu, TrayCommand command, string text, bool isChecked = false) =>
        AppendMenu(menu, MfString | (isChecked ? MfChecked : 0), (UIntPtr)(uint)command, text);

    private void AddIcon()
    {
        var data = NewData(NifMessage | NifIcon | NifTip | NifShowTip);
        data.CallbackMessage = CallbackMessage;
        data.Icon = _icon;
        data.Tip = TipText;
        // A busy Explorer at sign-in can time NIM_ADD out after it has already added the icon; a
        // modify that succeeds tells that apart from a real failure. A real failure is retried
        // when Explorer broadcasts TaskbarCreated.
        _added = Shell_NotifyIcon(NimAdd, ref data) || Shell_NotifyIcon(NimModify, ref data);
        data.TimeoutOrVersion = NotifyIconVersion4;
        _version4 = _added && Shell_NotifyIcon(NimSetVersion, ref data);
    }

    private void RemoveIcon()
    {
        var data = NewData(0);
        Shell_NotifyIcon(NimDelete, ref data);
        _added = false;
        _version4 = false;
    }

    private NotifyIconData NewData(uint flags) => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        Window = _window.Handle,
        Id = IconId,
        Flags = flags
    };

    private static (IntPtr Handle, bool Owned) LoadSmallIcon()
    {
        // The small image at the system DPI is the size the notification area draws, so the
        // 24-pixel frame in app.ico is used as-is at 150% instead of being scaled from 32.
        if (Environment.ProcessPath is { } path &&
            ExtractIconEx(path, 0, IntPtr.Zero, out var small, 1) > 0 &&
            small != IntPtr.Zero)
        {
            return (small, true);
        }

        return (LoadIcon(IntPtr.Zero, (IntPtr)IdiApplication), false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public int CallbackMessage;
        public IntPtr Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string? Info;

        public uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string? InfoTitle;

        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr largeIcons, out IntPtr smallIcon, uint count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

internal enum TrayCommand
{
    Cycle = 1,
    ResetPosition,
    TogglePositionLocked,
    Settings,
    ToggleStartup,
    Exit
}
