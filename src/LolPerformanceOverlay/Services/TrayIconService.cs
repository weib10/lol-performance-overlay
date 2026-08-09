using DrawingIcon = System.Drawing.Icon;
using Forms = System.Windows.Forms;

namespace LolPerformanceOverlay.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _startupItem;
    private readonly Forms.ToolStripMenuItem _positionLockedItem;

    public TrayIconService(bool startupEnabled, bool positionLocked)
    {
        _startupItem = new Forms.ToolStripMenuItem("登入 Windows 後常駐")
        {
            Checked = startupEnabled,
            CheckOnClick = true
        };
        _startupItem.CheckedChanged += StartupItemOnCheckedChanged;
        _positionLockedItem = new Forms.ToolStripMenuItem("鎖定 Overlay 位置")
        {
            Checked = positionLocked,
            CheckOnClick = true
        };
        _positionLockedItem.CheckedChanged += PositionLockedItemOnCheckedChanged;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("顯示／切換", null, (_, _) => CycleRequested?.Invoke());
        menu.Items.Add("重設 Overlay 位置", null, (_, _) => ResetPositionRequested?.Invoke());
        menu.Items.Add(_positionLockedItem);
        menu.Items.Add("設定", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("結束", null, (_, _) => ExitRequested?.Invoke());

        _icon = new Forms.NotifyIcon
        {
            Icon = DrawingIcon.ExtractAssociatedIcon(Environment.ProcessPath!) ??
                   System.Drawing.SystemIcons.Information,
            Text = "LoL 即時表現 Overlay",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => CycleRequested?.Invoke();
    }

    public event Action? CycleRequested;
    public event Action? SettingsRequested;
    public event Action? ResetPositionRequested;
    public event Action<bool>? StartupChanged;
    public event Action<bool>? PositionLockedChanged;
    public event Action? ExitRequested;

    public void UpdateStartup(bool enabled)
    {
        _startupItem.CheckedChanged -= StartupItemOnCheckedChanged;
        _startupItem.Checked = enabled;
        _startupItem.CheckedChanged += StartupItemOnCheckedChanged;
    }

    public void UpdatePositionLocked(bool locked)
    {
        _positionLockedItem.CheckedChanged -= PositionLockedItemOnCheckedChanged;
        _positionLockedItem.Checked = locked;
        _positionLockedItem.CheckedChanged += PositionLockedItemOnCheckedChanged;
    }

    public void ShowNotice(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private void StartupItemOnCheckedChanged(object? sender, EventArgs e) =>
        StartupChanged?.Invoke(_startupItem.Checked);

    private void PositionLockedItemOnCheckedChanged(object? sender, EventArgs e) =>
        PositionLockedChanged?.Invoke(_positionLockedItem.Checked);
}
