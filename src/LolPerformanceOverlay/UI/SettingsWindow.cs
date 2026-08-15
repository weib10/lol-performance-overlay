using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LolPerformanceOverlay.Services;

namespace LolPerformanceOverlay.UI;

public sealed class SettingsWindow : Window
{
    private readonly CheckBox _startup;
    private readonly CheckBox _positionLocked;
    private readonly Slider _opacity;
    private readonly TextBox _hotkey;
    private readonly TextBlock _validation;
    private readonly AppSettings _working;

    public SettingsWindow(AppSettings settings)
    {
        _working = settings.Clone();
        Title = "LoL 即時表現 Overlay 設定";
        Width = 420;
        Height = 380;
        // The Overlay is Topmost, so an unowned dialog opens underneath it and its
        // controls cannot be reached. Owning the dialog puts it above its owner, and
        // Topmost keeps it above the game as well.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(20, 25, 36));
        Foreground = Brushes.White;

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(Heading("低干擾顯示設定", 20));
        root.Children.Add(Body("Overlay 只有在選角或遊戲中才會出現。"));

        _startup = new CheckBox
        {
            Content = "登入 Windows 後自動常駐",
            IsChecked = settings.StartWithWindows,
            Margin = new Thickness(0, 20, 0, 14),
            Foreground = Brushes.White
        };
        root.Children.Add(_startup);

        _positionLocked = new CheckBox
        {
            Content = "鎖定 Overlay 位置（整個 Overlay 不接收滑鼠）",
            IsChecked = settings.PositionLocked,
            Margin = new Thickness(0, 0, 0, 14),
            Foreground = Brushes.White
        };
        root.Children.Add(_positionLocked);

        root.Children.Add(Heading("透明度", 13));
        _opacity = new Slider
        {
            Minimum = 0.35,
            Maximum = 1,
            Value = settings.Opacity,
            TickFrequency = 0.05,
            IsSnapToTickEnabled = true,
            Margin = new Thickness(0, 4, 0, 14)
        };
        root.Children.Add(_opacity);

        root.Children.Add(Heading("全域快捷鍵", 13));
        _hotkey = new TextBox
        {
            Text = settings.Hotkey,
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(8, 5, 8, 5)
        };
        root.Children.Add(_hotkey);
        _validation = Body("格式範例：Ctrl+Shift+O");
        root.Children.Add(_validation);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        var reset = Button("重設位置");
        reset.Click += (_, _) =>
        {
            _working.Left = double.NaN;
            _working.Top = double.NaN;
        };
        var cancel = Button("取消");
        cancel.Click += (_, _) => DialogResult = false;
        var save = Button("儲存");
        save.Click += (_, _) => Save();
        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        Content = root;
    }

    public AppSettings Result => _working;

    private void Save()
    {
        if (!GlobalHotkey.TryParse(_hotkey.Text, out _, out _))
        {
            _validation.Text = "快捷鍵格式無效，請使用 Ctrl+Shift+O 這類格式。";
            _validation.Foreground = new SolidColorBrush(Color.FromRgb(255, 125, 142));
            return;
        }

        _working.StartWithWindows = _startup.IsChecked == true;
        _working.PositionLocked = _positionLocked.IsChecked == true;
        _working.Opacity = _opacity.Value;
        _working.Hotkey = _hotkey.Text.Trim();
        DialogResult = true;
    }

    private static TextBlock Heading(string text, double size) =>
        new()
        {
            Text = text,
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        };

    private static TextBlock Body(string text) =>
        new()
        {
            Text = text,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(164, 176, 194)),
            Margin = new Thickness(0, 3, 0, 0)
        };

    private static Button Button(string text) =>
        new()
        {
            Content = text,
            MinWidth = 76,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(10, 5, 10, 5)
        };
}
