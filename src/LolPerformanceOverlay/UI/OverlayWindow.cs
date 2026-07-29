using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using LolPerformanceOverlay.Core;
using LolPerformanceOverlay.Services;

namespace LolPerformanceOverlay.UI;

public sealed class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int WmNcHitTest = 0x0084;
    private const int MaNoActivate = 3;
    private const int HtTransparent = -1;
    private const int HtCaption = 2;

    private readonly AppSettings _settings;
    private OverlaySnapshot _snapshot = OverlaySnapshot.Empty();
    private HwndSource? _source;

    public OverlayWindow(AppSettings settings)
    {
        _settings = settings;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        Mode = OverlayMode.Dot;
        Opacity = settings.Opacity;
        Left = double.IsNaN(settings.Left) ? SystemParameters.WorkArea.Right - 58 : settings.Left;
        Top = double.IsNaN(settings.Top) ? SystemParameters.WorkArea.Top + 96 : settings.Top;
        SourceInitialized += OnSourceInitialized;
        LocationChanged += (_, _) => PositionChanged?.Invoke(Left, Top);
        Render();
        ClampToVisibleWorkArea();
    }

    public OverlayMode Mode { get; private set; }
    public event Action<double, double>? PositionChanged;
    public event Action? SettingsRequested;

    public void ApplySnapshot(OverlaySnapshot snapshot)
    {
        _snapshot = snapshot;
        Render();
    }

    public void SetMode(OverlayMode mode)
    {
        Mode = mode;
        Render();
        ClampToVisibleWorkArea();
        ShowWithoutActivation();
    }

    public void CycleMode()
    {
        SetMode(Mode switch
        {
            OverlayMode.Dot => OverlayMode.Compact,
            OverlayMode.Compact => OverlayMode.Expanded,
            _ => OverlayMode.Dot
        });
    }

    public void ApplySettings(AppSettings settings)
    {
        Opacity = settings.Opacity;
        if (double.IsNaN(settings.Left) || double.IsNaN(settings.Top))
        {
            Left = SystemParameters.WorkArea.Right - Width - 24;
            Top = SystemParameters.WorkArea.Top + 96;
        }

        Render();
        ClampToVisibleWorkArea();
    }

    public void ShowWithoutActivation()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (_source is not null)
        {
            ShowWindow(_source.Handle, 4);
        }
    }

    private void Render()
    {
        Content = Mode switch
        {
            OverlayMode.Dot => BuildDot(),
            OverlayMode.Compact => BuildCompact(),
            OverlayMode.Expanded => BuildExpanded(),
            _ => BuildDot()
        };
    }

    private UIElement BuildDot()
    {
        Width = 34;
        Height = 34;
        var button = new Button
        {
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Color.FromArgb(220, 235, 241, 250)),
            Background = DotBrush(),
            Cursor = Cursors.Hand,
            Tag = "Interactive",
            ToolTip = _snapshot.Summary,
            Template = RoundButtonTemplate()
        };
        button.Click += (_, _) => CycleMode();
        return new Grid { Children = { button } };
    }

    private UIElement BuildCompact()
    {
        Width = 440;
        Height = _snapshot.Phase == LeaguePhase.ChampSelect ? 92 : 102;
        var root = Card();
        var stack = new StackPanel();
        stack.Children.Add(Header(allowSettings: false));

        if (_snapshot.Phase == LeaguePhase.ChampSelect)
        {
            var icons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(14, 2, 14, 10)
            };
            foreach (var player in _snapshot.Teams.SelectMany(team => team.Players).Take(10))
            {
                icons.Children.Add(ChampionChip(player));
            }

            stack.Children.Add(icons);
        }
        else
        {
            var teams = _snapshot.Teams.Take(2).ToArray();
            var line = new Grid { Margin = new Thickness(14, 2, 14, 10) };
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition());
            if (teams.Length > 0)
            {
                var left = TeamCompact(teams[0], HorizontalAlignment.Left);
                Grid.SetColumn(left, 0);
                line.Children.Add(left);
            }

            if (teams.Length > 1)
            {
                var right = TeamCompact(teams[1], HorizontalAlignment.Right);
                Grid.SetColumn(right, 1);
                line.Children.Add(right);
            }

            stack.Children.Add(line);
        }

        root.Child = stack;
        return root;
    }

    private UIElement BuildExpanded()
    {
        Width = 700;
        Height = 476;
        var root = Card();
        var layout = new DockPanel();
        var header = Header(allowSettings: true);
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        var footer = Text(
            _snapshot.Phase == LeaguePhase.ChampSelect
                ? "選角只顯示 Riot 正常提供的身分；匿名玩家不做還原。"
                : "分數只表示本場目前相對表現 · 不含歷史戰績或勝率預測",
            11,
            "#8190A6");
        footer.Margin = new Thickness(16, 8, 16, 12);
        footer.TextAlignment = TextAlignment.Center;
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);

        var teamsGrid = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        teamsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        teamsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        teamsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var teams = _snapshot.Teams.Take(2).ToArray();
        if (teams.Length > 0)
        {
            var team = TeamPanel(teams[0]);
            Grid.SetColumn(team, 0);
            teamsGrid.Children.Add(team);
        }

        if (teams.Length > 1)
        {
            var team = TeamPanel(teams[1]);
            Grid.SetColumn(team, 2);
            teamsGrid.Children.Add(team);
        }

        layout.Children.Add(teamsGrid);
        root.Child = layout;
        return root;
    }

    private Border Header(bool allowSettings)
    {
        var border = new Border
        {
            Padding = new Thickness(14, 10, 10, 8),
            Tag = "Drag"
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        text.Children.Add(Text(_snapshot.Header, 13, "#F5F7FB", FontWeights.SemiBold));
        text.Children.Add(Text(_snapshot.Summary, 11, "#9DABC0"));
        grid.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (allowSettings)
        {
            var settings = SmallButton("⚙", "設定");
            settings.Click += (_, _) => SettingsRequested?.Invoke();
            buttons.Children.Add(settings);
        }

        var dot = SmallButton("•", "縮成小點");
        dot.Click += (_, _) => SetMode(OverlayMode.Dot);
        buttons.Children.Add(dot);
        if (Mode == OverlayMode.Compact)
        {
            var expand = SmallButton("▣", "展開");
            expand.Click += (_, _) => SetMode(OverlayMode.Expanded);
            buttons.Children.Add(expand);
        }
        else
        {
            var compact = SmallButton("—", "縮成資訊條");
            compact.Click += (_, _) => SetMode(OverlayMode.Compact);
            buttons.Children.Add(compact);
        }

        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        border.Child = grid;
        return border;
    }

    private Border TeamPanel(OverlayTeam team)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(80, 38, 48, 67)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10)
        };
        var stack = new StackPanel();
        var heading = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(Text(team.DisplayName, 13, TeamColor(team.Team), FontWeights.SemiBold));
        var score = Text(
            team.PerformanceScore.HasValue ? $"{team.PerformanceScore:0.0}" : "未評分",
            13,
            "#F5F7FB",
            FontWeights.SemiBold);
        Grid.SetColumn(score, 1);
        heading.Children.Add(score);
        stack.Children.Add(heading);

        foreach (var player in team.Players.Take(5))
        {
            stack.Children.Add(PlayerRow(player));
        }

        border.Child = stack;
        return border;
    }

    private Border PlayerRow(OverlayPlayer player)
    {
        var border = new Border
        {
            Height = 58,
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Color.FromArgb(92, 22, 29, 42)),
            CornerRadius = new CornerRadius(8)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        grid.Children.Add(ChampionVisual(player, 36));

        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(Text(player.ChampionName, 12, "#F4F7FC", FontWeights.SemiBold));
        identity.Children.Add(Text(player.DisplayName, 10.5, "#9EABC0"));
        Grid.SetColumn(identity, 1);
        grid.Children.Add(identity);

        var performance = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        performance.Children.Add(Text(
            player.PerformanceScore.HasValue ? $"{player.PerformanceScore:0.0}" : "—",
            14,
            ScoreColor(player.PerformanceScore),
            FontWeights.Bold));
        performance.Children.Add(Text(
            player.PerformanceLabel is null
                ? player.IsAnonymous ? "匿名" : "尚未開始"
                : $"{player.PerformanceLabel} · {ConfidenceText(player.Confidence)}",
            9.5,
            "#8795AA"));
        Grid.SetColumn(performance, 2);
        grid.Children.Add(performance);

        border.Child = grid;
        return border;
    }

    private FrameworkElement ChampionChip(OverlayPlayer player)
    {
        var visual = ChampionVisual(player, 30);
        visual.Margin = new Thickness(0, 0, 7, 0);
        visual.ToolTip = $"{player.ChampionName} · {player.DisplayName}";
        return visual;
    }

    private FrameworkElement ChampionVisual(OverlayPlayer player, double size)
    {
        if (!string.IsNullOrWhiteSpace(player.ChampionIconPath) &&
            File.Exists(player.ChampionIconPath))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(player.ChampionIconPath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return new Border
                {
                    Width = size,
                    Height = size,
                    CornerRadius = new CornerRadius(size / 2),
                    Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2),
                    Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill }
                };
            }
            catch
            {
                // Fall back to a lightweight initial badge.
            }
        }

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = new SolidColorBrush(Color.FromRgb(51, 66, 91)),
            Child = Text(
                string.IsNullOrWhiteSpace(player.ChampionName) ? "?" : player.ChampionName[..1],
                size * 0.38,
                "#E8EEF8",
                FontWeights.Bold,
                HorizontalAlignment.Center)
        };
    }

    private StackPanel TeamCompact(OverlayTeam team, HorizontalAlignment alignment)
    {
        var stack = new StackPanel { HorizontalAlignment = alignment };
        var score = team.PerformanceScore.HasValue ? $"{team.PerformanceScore:0.0}" : "—";
        stack.Children.Add(Text($"{team.DisplayName}  {score}", 13, TeamColor(team.Team), FontWeights.SemiBold));

        var scored = team.Players.Where(player => player.PerformanceScore.HasValue).ToArray();
        if (scored.Length > 0)
        {
            var top = scored.MaxBy(player => player.PerformanceScore)!;
            var bottom = scored.MinBy(player => player.PerformanceScore)!;
            stack.Children.Add(Text(
                $"↑ {top.ChampionName}  ↓ {bottom.ChampionName}",
                10.5,
                "#8F9DB1"));
        }

        return stack;
    }

    private Border Card() =>
        new()
        {
            Background = new SolidColorBrush(Color.FromArgb(242, 17, 23, 34)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, 75, 91, 118)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13)
        };

    private Button SmallButton(string text, string toolTip)
    {
        var button = new Button
        {
            Content = text,
            Width = 30,
            Height = 27,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(100, 53, 65, 86)),
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(221, 229, 241)),
            ToolTip = toolTip,
            Cursor = Cursors.Hand,
            Tag = "Interactive"
        };
        return button;
    }

    private static TextBlock Text(
        string text,
        double size,
        string color,
        FontWeight? weight = null,
        HorizontalAlignment alignment = HorizontalAlignment.Left) =>
        new()
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = size,
            Foreground = Brush(color),
            FontWeight = weight ?? FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = alignment
        };

    private static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    private static string TeamColor(int team) => team is 100 or 1 ? "#74B7FF" : "#FF8797";

    private static string ScoreColor(double? score) => score switch
    {
        >= 75d => "#65E6A7",
        >= 60d => "#A9E37A",
        >= 40d => "#E7EDF7",
        >= 25d => "#FFBE72",
        null => "#7E8BA0",
        _ => "#FF7D8E"
    };

    private static string ConfidenceText(PerformanceConfidence? confidence) => confidence switch
    {
        PerformanceConfidence.High => "高信心",
        PerformanceConfidence.Medium => "中信心",
        PerformanceConfidence.Low => "低信心",
        _ => string.Empty
    };

    private SolidColorBrush DotBrush()
    {
        if (!_snapshot.TeamGap.HasValue ||
            _snapshot.TeamGap.Value < 3d ||
            !_snapshot.LeadingTeam.HasValue)
        {
            return Brush("#8794A8");
        }

        if (!_snapshot.ActiveTeam.HasValue)
        {
            return Brush(_snapshot.LeadingTeam is 100 or 1 ? "#62AEFF" : "#FF7D8E");
        }

        return Brush(_snapshot.LeadingTeam == _snapshot.ActiveTeam ? "#55D99A" : "#FF6F83");
    }

    private static ControlTemplate RoundButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        factory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(
                System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        factory.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(
                System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        factory.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(
                System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        template.VisualTree = factory;
        return template;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        var handle = helper.Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExToolWindow | WsExNoActivate);
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
    }

    private void ClampToVisibleWorkArea()
    {
        var center = new System.Drawing.Point(
            (int)Math.Round(Left + Math.Max(Width, 34) / 2),
            (int)Math.Round(Top + Math.Max(Height, 34) / 2));
        var workArea = System.Windows.Forms.Screen.FromPoint(center).WorkingArea;
        var margin = 10d;
        var maxLeft = Math.Max(workArea.Left + margin, workArea.Right - Width - margin);
        var maxTop = Math.Max(workArea.Top + margin, workArea.Bottom - Height - margin);
        Left = Math.Clamp(Left, workArea.Left + margin, maxLeft);
        Top = Math.Clamp(Top, workArea.Top + margin, maxTop);
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }

        if (message != WmNcHitTest)
        {
            return IntPtr.Zero;
        }

        var packed = lParam.ToInt64();
        var screenPoint = new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff));
        var localPoint = PointFromScreen(screenPoint);
        var hit = InputHitTest(localPoint) as DependencyObject;
        while (hit is not null)
        {
            if (hit is FrameworkElement element)
            {
                if (Equals(element.Tag, "Interactive"))
                {
                    return IntPtr.Zero;
                }

                if (Equals(element.Tag, "Drag"))
                {
                    handled = true;
                    return new IntPtr(HtCaption);
                }
            }

            hit = VisualTreeHelper.GetParent(hit);
        }

        handled = true;
        return new IntPtr(HtTransparent);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr windowHandle, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr windowHandle, int index, int newLong);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);
}
