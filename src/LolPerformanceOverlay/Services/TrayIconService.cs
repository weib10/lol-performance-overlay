using DrawingIcon = System.Drawing.Icon;
using Forms = System.Windows.Forms;

namespace LolPerformanceOverlay.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _startupItem;

    public TrayIconService(bool startupEnabled)
    {
        _startupItem = new Forms.ToolStripMenuItem("登入 Windows 後常駐")
        {
            Checked = startupEnabled,
            CheckOnClick = true
        };
        _startupItem.CheckedChanged += StartupItemOnCheckedChanged;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("顯示／切換", null, (_, _) => CycleRequested?.Invoke());
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
    public event Action<bool>? StartupChanged;
    public event Action? ExitRequested;

    public void UpdateStartup(bool enabled)
    {
        _startupItem.CheckedChanged -= StartupItemOnCheckedChanged;
        _startupItem.Checked = enabled;
        _startupItem.CheckedChanged += StartupItemOnCheckedChanged;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private void StartupItemOnCheckedChanged(object? sender, EventArgs e) =>
        StartupChanged?.Invoke(_startupItem.Checked);
}
