using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LolPerformanceOverlay.Core.Interaction;
using LolPerformanceOverlay.Core.Presentation;
using LolPerformanceOverlay.Services;

namespace LolPerformanceOverlay.UI;

public sealed class SettingsWindow : Window
{
    private readonly CheckBox _startup;
    private readonly CheckBox _positionLocked;
    private readonly RadioButton _nameDisplayChampion;
    private readonly RadioButton _nameDisplayRiotId;
    private readonly Slider _opacity;
    private readonly TextBlock _opacityValue;
    private readonly TextBox _hotkey;
    private readonly PasswordBox _riotApiKey;
    private readonly TextBlock _validation;
    private readonly AppSettings _working;
    private readonly OpacityPreviewSession _opacityPreview;

    /// <summary>
    /// Fired on every slider tick while dragging (live preview) and once more when the dialog
    /// closes without saving (restoring <see cref="OpacityPreviewSession.PriorOpacity"/>).
    /// The caller applies the value straight to the live overlay window -- see
    /// App.xaml.cs.OpenSettings, which is also what "cancel restores the prior opacity" ends
    /// up meaning in practice.
    /// </summary>
    public event Action<double>? OpacityPreviewChanged;

    public SettingsWindow(AppSettings settings)
    {
        _working = settings.Clone();
        _opacityPreview = new OpacityPreviewSession(settings.Opacity);
        Title = "LoL 即時表現 Overlay 設定";
        Width = 420;
        Height = 540;
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

        root.Children.Add(Heading("頭像旁顯示", 13));
        var nameDisplayRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 2)
        };
        // PlayerNameDisplay (Core) is the single place that decides the actual per-row text --
        // this dialog only records which of the two the user asked for.
        _nameDisplayChampion = new RadioButton
        {
            Content = "英雄名稱",
            GroupName = "NameDisplayMode",
            IsChecked = settings.NameDisplayMode == PlayerNameDisplayMode.ChampionName,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 20, 0)
        };
        _nameDisplayRiotId = new RadioButton
        {
            Content = "Riot ID",
            GroupName = "NameDisplayMode",
            IsChecked = settings.NameDisplayMode == PlayerNameDisplayMode.RiotId,
            Foreground = Brushes.White
        };
        nameDisplayRow.Children.Add(_nameDisplayChampion);
        nameDisplayRow.Children.Add(_nameDisplayRiotId);
        root.Children.Add(nameDisplayRow);
        root.Children.Add(Body("被 Riot 隱藏身分的玩家一律仍顯示英雄名稱，不受這個設定影響。"));

        var opacityRow = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        opacityRow.ColumnDefinitions.Add(new ColumnDefinition());
        opacityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var opacityHeading = Heading("透明度", 13);
        // Read out loud while dragging: 「一邊拉動透明度條的時候需要能即時反饋」-- the slider alone
        // does not tell the user what value they are landing on, so the percentage next to the
        // heading updates on every tick alongside the live overlay preview below.
        _opacityValue = Heading(FormatOpacityPercent(_opacityPreview.CurrentOpacity), 13);
        _opacityValue.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_opacityValue, 1);
        opacityRow.Children.Add(opacityHeading);
        opacityRow.Children.Add(_opacityValue);
        root.Children.Add(opacityRow);
        _opacity = new Slider
        {
            Minimum = OverlayOpacityPolicy.Minimum,
            Maximum = OverlayOpacityPolicy.Maximum,
            Value = settings.Opacity,
            TickFrequency = 0.05,
            IsSnapToTickEnabled = true,
            Margin = new Thickness(0, 4, 0, 14)
        };
        _opacity.ValueChanged += (_, e) =>
        {
            var previewed = _opacityPreview.Preview(e.NewValue);
            _opacityValue.Text = FormatOpacityPercent(previewed);
            OpacityPreviewChanged?.Invoke(previewed);
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

        var apiKeyHeading = Heading("官方牌位（選用）", 13);
        apiKeyHeading.Margin = new Thickness(0, 20, 0, 0);
        root.Children.Add(apiKeyHeading);
        root.Children.Add(Body(
            "貼上你自己申請的 Riot Personal API key 才會顯示官方牌位；留空則維持不查詢。" +
            "只存在這台電腦，不會被打包或上傳，改動要重新啟動才會生效。"));
        _riotApiKey = new PasswordBox
        {
            Password = settings.RiotApiKey,
            Margin = new Thickness(0, 8, 0, 4),
            Padding = new Thickness(8, 5, 8, 5)
        };
        root.Children.Add(_riotApiKey);
        // AGENTS.md rule 9 (產品誠實性) requires the official rank and this program's own
        // reading to be kept plainly apart. This used to be a sentence repeated in all ten row
        // tooltips; it is stated once here and once in 先看這裡.html instead, so the panel stays
        // readable mid-game without the distinction going unstated anywhere.
        root.Children.Add(Body(
            "牌位是 Riot 官方資料，分數是本場相對表現。兩者分開呈現，" +
            "不會合併或換算成單一數值，也不是隱藏 MMR／ELO 或勝率預測。"));

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

        // Covers every way the dialog can close without saving -- the Cancel button (which
        // only sets DialogResult = false, itself closing the window), the title bar's own
        // close button, and Alt+F4 alike -- so the live preview above can never be "backed
        // out of" by one path but not another. When Save() runs instead, DialogResult is
        // already true by the time Closed fires, so this is a no-op there.
        Closed += (_, _) =>
        {
            if (DialogResult != true)
            {
                OpacityPreviewChanged?.Invoke(_opacityPreview.Cancel());
            }
        };
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
        _working.NameDisplayMode = _nameDisplayRiotId.IsChecked == true
            ? PlayerNameDisplayMode.RiotId
            : PlayerNameDisplayMode.ChampionName;
        _working.Opacity = _opacityPreview.CurrentOpacity;
        _working.Hotkey = _hotkey.Text.Trim();
        _working.RiotApiKey = _riotApiKey.Password.Trim();
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

    // Manual percentage formatting instead of "P0" -- some cultures insert a space before the
    // percent sign under that format specifier, which is not how percentages read in the rest
    // of this UI.
    private static string FormatOpacityPercent(double opacity) =>
        $"{(int)Math.Round(opacity * 100)}%";
}
