using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using LolPerformanceOverlay.Core;
using LolPerformanceOverlay.Core.Interaction;
using LolPerformanceOverlay.Core.Presentation;
using LolPerformanceOverlay.Services;
using Forms = System.Windows.Forms;

namespace LolPerformanceOverlay.UI;

/// <summary>
/// Windows presentation adapter. A mode change builds one visual tree; session updates mutate
/// the existing controls and are ignored entirely when the visible snapshot is unchanged.
/// </summary>
public sealed class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int WmNcHitTest = 0x0084;
    private const int MaNoActivate = 3;
    private const int HtTransparent = -1;
    private const uint MonitorDefaultToNearest = 2;
    private const int MonitorDpiTypeEffective = 0;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly ConcurrentDictionary<string, SolidColorBrush> BrushesByHex =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly AppSettings _settings;
    private readonly ChampionImageCache _imageCache = new();
    private readonly PointerInteractionStateMachine _pointer = new(5);
    private readonly List<AvatarView> _compactAvatars = [];
    private readonly List<PlayerRowView> _playerRows = [];
    private readonly List<TeamView> _teamViews = [];
    private CancellationTokenSource _visualGenerationCancellation = new();
    private OverlaySnapshot _snapshot = OverlaySnapshot.Empty();
    private HistoricalProfilesResult? _historicalProfiles;
    private HwndSource? _source;
    private TextBlock? _headerText;
    private TextBlock? _summaryText;
    private Border? _dot;
    private TextBlock? _compactLeft;
    private TextBlock? _compactLeftDetail;
    private TextBlock? _compactRight;
    private TextBlock? _compactRightDetail;
    private TextBlock? _historyStatus;
    private TextBlock? _historyMeta;
    private TextBlock? _historyDetails;
    private Button? _opGgButton;
    private Uri? _opGgDestination;
    private bool _visualWasChampSelect;
    private bool _releasingCapture;
    private DipPoint _dragPointerOrigin;
    private DipPoint _dragWindowOrigin;
    private bool _clamping;
    private IntPtr _windowHandle;
    private string? _platformRegion;

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
        Left = double.IsFinite(settings.Left) ? settings.Left : SystemParameters.WorkArea.Right - 58;
        Top = double.IsFinite(settings.Top) ? settings.Top : SystemParameters.WorkArea.Top + 96;

        _pointer.HandlePositionLock(settings.PositionLocked);
        SourceInitialized += OnSourceInitialized;
        LocationChanged += OnLocationChanged;
        PreviewMouseLeftButtonDown += OnPointerDown;
        PreviewMouseMove += OnPointerMove;
        PreviewMouseLeftButtonUp += OnPointerUp;
        LostMouseCapture += OnLostMouseCapture;
        DpiChanged += (_, _) => ClampToVisibleWorkArea();
        Closed += OnClosed;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        BuildModeVisual();
        UpdateVisibleControls();
        ClampToVisibleWorkArea();
    }

    public OverlayMode Mode { get; private set; }
    public bool IsPositionLocked => _pointer.IsPositionLocked;
    public long PresentationUpdateCount { get; private set; }
    public long VisualTreeBuildCount { get; private set; }
    public long ChampionImageDecodeCount => _imageCache.DecodeCount;
    public long ChampionImageCacheHits => _imageCache.CacheHits;

    public event Action<double, double>? PositionChanged;
    public event Action? SettingsRequested;
    public event Action<Uri>? OpenExternalLinkRequested;

    public void ApplySnapshot(OverlaySnapshot snapshot)
    {
        ApplySnapshot(snapshot, VisibleSnapshot.Diff(_snapshot, snapshot));
    }

    public void ApplySnapshot(OverlaySnapshot snapshot, OverlaySnapshotDiff diff)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(diff);
        if (!diff.HasChanges)
        {
            return;
        }

        var structureChanged = Mode is OverlayMode.Compact or OverlayMode.Expanded &&
                               (_snapshot.Phase == LeaguePhase.ChampSelect) !=
                               (snapshot.Phase == LeaguePhase.ChampSelect);
        _snapshot = snapshot;
        if (structureChanged)
        {
            BuildModeVisual();
            UpdateVisibleControls();
        }
        else
        {
            UpdateVisibleControls(diff);
        }
        PresentationUpdateCount++;
    }

    public void SetPlatformRegion(string? platformRegion)
    {
        var normalized = PlatformRegionMapper.TryMap(platformRegion) ??
                         (string.IsNullOrWhiteSpace(platformRegion) ? null : platformRegion.Trim().ToLowerInvariant());
        if (string.Equals(_platformRegion, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _platformRegion = normalized;
        UpdateHistoryControls();
    }

    public void ApplyHistoricalProfiles(HistoricalProfilesResult profiles)
    {
        _historicalProfiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        UpdateHistoryControls();
    }

    public void ClearHistoricalProfiles()
    {
        if (_historicalProfiles is null)
        {
            return;
        }

        _historicalProfiles = null;
        UpdateHistoryControls();
    }

    public void SetMode(OverlayMode mode)
    {
        if (Mode != mode)
        {
            Mode = mode;
            BuildModeVisual();
            UpdateVisibleControls();
            ClampToVisibleWorkArea();
        }

        ShowWithoutActivation();
    }

    public void CycleMode() => SetMode(Mode switch
    {
        OverlayMode.Dot => OverlayMode.Compact,
        OverlayMode.Compact => OverlayMode.Expanded,
        _ => OverlayMode.Dot
    });

    public void SetPositionLocked(bool locked)
    {
        ProcessPointerActions(_pointer.HandlePositionLock(locked));
        _settings.PositionLocked = locked;
        Cursor = locked ? Cursors.Arrow : Cursors.SizeAll;
        ApplyWindowInteractionStyle();
    }

    public void ResetPosition()
    {
        var workAreas = GetWorkAreas();
        var result = OverlayPlacement.Clamp(
            new DipPoint(double.NaN, double.NaN),
            new DipSize(Math.Max(ActualWidth, Width), Math.Max(ActualHeight, Height)),
            workAreas);
        Left = result.Position.X;
        Top = result.Position.Y;
        PositionChanged?.Invoke(Left, Top);
    }

    public void ApplySettings(AppSettings settings)
    {
        Opacity = Math.Clamp(settings.Opacity, 0.35, 1);
        SetPositionLocked(settings.PositionLocked);
        if (!double.IsFinite(settings.Left) || !double.IsFinite(settings.Top))
        {
            ResetPosition();
        }

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

    private void BuildModeVisual()
    {
        _visualGenerationCancellation.Cancel();
        _visualGenerationCancellation.Dispose();
        _visualGenerationCancellation = new CancellationTokenSource();
        _compactAvatars.Clear();
        _playerRows.Clear();
        _teamViews.Clear();
        _headerText = null;
        _summaryText = null;
        _dot = null;
        _compactLeft = null;
        _compactLeftDetail = null;
        _compactRight = null;
        _compactRightDetail = null;
        _historyStatus = null;
        _historyMeta = null;
        _historyDetails = null;
        _opGgButton = null;
        _visualWasChampSelect = _snapshot.Phase == LeaguePhase.ChampSelect;

        Content = Mode switch
        {
            OverlayMode.Dot => BuildDot(),
            OverlayMode.Compact => BuildCompact(),
            OverlayMode.Expanded => BuildExpanded(),
            _ => BuildDot()
        };
        Cursor = IsPositionLocked ? Cursors.Arrow : Cursors.SizeAll;
        VisualTreeBuildCount++;
    }

    private UIElement BuildDot()
    {
        Width = 38;
        Height = 38;
        var root = new Grid
        {
            Background = Brushes.Transparent,
            ToolTip = "點一下展開；按住拖曳"
        };
        _dot = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(2),
            BorderBrush = Brush("#EBF1FA")
        };
        root.Children.Add(_dot);
        return root;
    }

    private UIElement BuildCompact()
    {
        Width = 460;
        Height = _visualWasChampSelect ? 120 : 112;
        var root = Card();
        var stack = new StackPanel();
        stack.Children.Add(BuildHeader(allowSettings: false));

        if (_visualWasChampSelect)
        {
            // One flat row of ten made the two sides indistinguishable. Split them into
            // labelled, colour-coded halves so blue and red read apart at a glance --
            // the label carries the side, so colour is not the only signal.
            var sides = new Grid { Margin = new Thickness(14, 0, 14, 10) };
            sides.ColumnDefinitions.Add(new ColumnDefinition());
            sides.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            sides.ColumnDefinitions.Add(new ColumnDefinition());
            for (var side = 0; side < 2; side++)
            {
                var column = new StackPanel();
                column.Children.Add(Text(
                    side == 0 ? "藍方" : "紅方",
                    11,
                    TeamColor(side == 0 ? 100 : 200),
                    FontWeights.SemiBold));
                var icons = new UniformGrid { Columns = 5, Margin = new Thickness(0, 3, 0, 0) };
                for (var index = 0; index < 5; index++)
                {
                    var avatar = CreateAvatar(32);
                    avatar.Root.Margin = new Thickness(2, 0, 2, 0);
                    _compactAvatars.Add(avatar);
                    icons.Children.Add(avatar.Root);
                }

                column.Children.Add(icons);
                Grid.SetColumn(column, side == 0 ? 0 : 2);
                sides.Children.Add(column);
            }

            stack.Children.Add(sides);
        }
        else
        {
            var line = new Grid { Margin = new Thickness(16, 3, 16, 12) };
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition());
            var left = CompactTeam(out _compactLeft, out _compactLeftDetail, HorizontalAlignment.Left);
            var right = CompactTeam(out _compactRight, out _compactRightDetail, HorizontalAlignment.Right);
            Grid.SetColumn(right, 1);
            line.Children.Add(left);
            line.Children.Add(right);
            stack.Children.Add(line);
        }

        root.Child = stack;
        return root;
    }

    private UIElement BuildExpanded()
    {
        Width = 648;
        Height = 552;
        var root = Card();
        var layout = new DockPanel();
        var header = BuildHeader(allowSettings: true);
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        var footer = Text(
            _snapshot.Phase == LeaguePhase.ChampSelect
                ? "選角只顯示遊戲正常揭露的身分；匿名玩家不做還原。"
                : "本場即時表現與歷史近期狀態分開呈現；不代表牌位、隱藏分數或勝率。",
            11,
            "#91A0B5");
        footer.Margin = new Thickness(16, 7, 16, 12);
        footer.TextAlignment = TextAlignment.Center;
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);

        var history = BuildHistoryPanel();
        DockPanel.SetDock(history, Dock.Bottom);
        layout.Children.Add(history);

        var teamsGrid = new Grid { Margin = new Thickness(12, 0, 12, 6) };
        teamsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        teamsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        teamsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var index = 0; index < 2; index++)
        {
            var team = CreateTeamView();
            _teamViews.Add(team);
            Grid.SetColumn(team.Root, index == 0 ? 0 : 2);
            teamsGrid.Children.Add(team.Root);
        }

        layout.Children.Add(teamsGrid);
        root.Child = layout;
        return root;
    }

    private Border BuildHeader(bool allowSettings)
    {
        var border = new Border { Padding = new Thickness(14, 10, 10, 8) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        _headerText = Text(string.Empty, 14.5, "#F5F7FB", FontWeights.SemiBold);
        _summaryText = Text(string.Empty, 12, "#A6B3C7");
        text.Children.Add(_headerText);
        text.Children.Add(_summaryText);
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
        var switcher = Mode == OverlayMode.Compact
            ? SmallButton("▣", "展開")
            : SmallButton("—", "縮成資訊條");
        switcher.Click += (_, _) => SetMode(Mode == OverlayMode.Compact
            ? OverlayMode.Expanded
            : OverlayMode.Compact);
        buttons.Children.Add(switcher);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        border.Child = grid;
        return border;
    }

    private Border BuildHistoryPanel()
    {
        var panel = new Border
        {
            Margin = new Thickness(12, 2, 12, 5),
            Padding = new Thickness(12, 9, 12, 9),
            Background = new SolidColorBrush(Color.FromArgb(92, 35, 45, 62)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(110, 88, 112, 145)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(Text("歷史近期狀態／風格（獨立資訊）", 11.5, "#DDE8F7", FontWeights.SemiBold));
        _historyStatus = Text(string.Empty, 10.5, "#B7C3D5");
        _historyMeta = Text(string.Empty, 9.5, "#8798B0");
        _historyDetails = Text(string.Empty, 9.5, "#8798B0");
        text.Children.Add(_historyStatus);
        text.Children.Add(_historyMeta);
        text.Children.Add(_historyDetails);
        grid.Children.Add(text);
        _opGgButton = SmallButton("↗", "由你主動在瀏覽器開啟 OP.GG；Overlay 不讀回網頁資料");
        _opGgButton.Width = 34;
        _opGgButton.Height = 32;
        _opGgButton.Click += (_, _) =>
        {
            if (_opGgDestination is not null)
            {
                OpenExternalLinkRequested?.Invoke(_opGgDestination);
            }
        };
        Grid.SetColumn(_opGgButton, 1);
        grid.Children.Add(_opGgButton);
        panel.Child = grid;
        return panel;
    }

    private TeamView CreateTeamView()
    {
        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(80, 38, 48, 67)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10)
        };
        var stack = new StackPanel();
        var heading = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = Text(string.Empty, 14.5, "#74B7FF", FontWeights.SemiBold);
        var score = Text(string.Empty, 14.5, "#F5F7FB", FontWeights.SemiBold);
        Grid.SetColumn(score, 1);
        heading.Children.Add(name);
        heading.Children.Add(score);
        stack.Children.Add(heading);
        var rows = new List<PlayerRowView>(5);
        for (var index = 0; index < 5; index++)
        {
            var row = CreatePlayerRow();
            rows.Add(row);
            _playerRows.Add(row);
            stack.Children.Add(row.Root);
        }

        root.Child = stack;
        return new TeamView(root, name, score, rows);
    }

    private PlayerRowView CreatePlayerRow()
    {
        var root = new Border
        {
            Height = 56,
            Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(6, 4, 6, 4),
            Background = new SolidColorBrush(Color.FromArgb(92, 22, 29, 42)),
            CornerRadius = new CornerRadius(8)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        var avatar = CreateAvatar(34);
        grid.Children.Add(avatar.Root);
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var champion = Text(string.Empty, 13.5, "#F4F7FC", FontWeights.SemiBold);
        var playerName = Text(string.Empty, 11, "#A6B3C7");
        identity.Children.Add(champion);
        identity.Children.Add(playerName);
        Grid.SetColumn(identity, 1);
        grid.Children.Add(identity);
        var performance = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var score = Text(string.Empty, 15, "#E7EDF7", FontWeights.Bold, HorizontalAlignment.Right);
        var detail = Text(string.Empty, 10.5, "#8F9DB2", alignment: HorizontalAlignment.Right);
        // Champ select shows this cell's pick number; a live game shows carried item value.
        var meta = Text(string.Empty, 10.5, "#7F8FA6", FontWeights.SemiBold, HorizontalAlignment.Right);
        performance.Children.Add(score);
        performance.Children.Add(detail);
        performance.Children.Add(meta);
        Grid.SetColumn(performance, 2);
        grid.Children.Add(performance);
        root.Child = grid;
        return new PlayerRowView(root, avatar, champion, playerName, score, detail, meta);
    }

    private static StackPanel CompactTeam(
        out TextBlock heading,
        out TextBlock detail,
        HorizontalAlignment alignment)
    {
        var stack = new StackPanel { HorizontalAlignment = alignment };
        heading = Text(string.Empty, 14.5, "#DCE8F8", FontWeights.SemiBold);
        detail = Text(string.Empty, 12, "#96A4B8");
        stack.Children.Add(heading);
        stack.Children.Add(detail);
        return stack;
    }

    private AvatarView CreateAvatar(double size)
    {
        var root = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2),
            Background = new SolidColorBrush(Color.FromRgb(51, 66, 91))
        };
        var grid = new Grid();
        var image = new Image { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
        var initial = Text("?", size * 0.36, "#E8EEF8", FontWeights.Bold, HorizontalAlignment.Center);
        grid.Children.Add(initial);
        grid.Children.Add(image);
        root.Child = grid;
        return new AvatarView(root, image, initial);
    }

    private void UpdateVisibleControls()
    {
        if (_headerText is not null)
        {
            _headerText.Text = _snapshot.Header;
        }

        if (_summaryText is not null)
        {
            _summaryText.Text = _snapshot.Summary;
        }

        if (_dot is not null)
        {
            _dot.Background = DotBrush();
            _dot.ToolTip = $"{_snapshot.Summary}；點一下展開，按住拖曳";
        }

        if (Mode == OverlayMode.Compact)
        {
            UpdateCompact();
        }
        else if (Mode == OverlayMode.Expanded)
        {
            UpdateExpanded();
        }
    }

    private void UpdateVisibleControls(OverlaySnapshotDiff diff)
    {
        if (_headerText is not null &&
            (diff.Fields & OverlaySnapshotFields.Header) != 0)
        {
            _headerText.Text = _snapshot.Header;
        }

        if (_summaryText is not null &&
            (diff.Fields & OverlaySnapshotFields.Summary) != 0)
        {
            _summaryText.Text = _snapshot.Summary;
        }

        if (_dot is not null &&
            (diff.Fields & (OverlaySnapshotFields.Summary |
                            OverlaySnapshotFields.ActiveTeam |
                            OverlaySnapshotFields.LeadingTeam |
                            OverlaySnapshotFields.TeamGap)) != 0)
        {
            _dot.Background = DotBrush();
            _dot.ToolTip = $"{_snapshot.Summary}；點一下展開，按住拖曳";
        }

        if (Mode == OverlayMode.Compact &&
            (diff.Fields & (OverlaySnapshotFields.Teams | OverlaySnapshotFields.Phase)) != 0)
        {
            UpdateCompact();
        }
        else if (Mode == OverlayMode.Expanded)
        {
            UpdateExpanded(diff);
        }

    }

    private void UpdateCompact()
    {
        if (_visualWasChampSelect)
        {
            // Slots 0-4 belong to the blue column and 5-9 to the red one, so fill each
            // side from its own team instead of flattening both into one sequence.
            for (var side = 0; side < 2; side++)
            {
                var team = _snapshot.Teams.FirstOrDefault(candidate =>
                    candidate.Team == (side == 0 ? 100 : 200));
                for (var seat = 0; seat < 5; seat++)
                {
                    var slot = side * 5 + seat;
                    if (slot >= _compactAvatars.Count)
                    {
                        break;
                    }

                    UpdateAvatar(_compactAvatars[slot], team?.Players.ElementAtOrDefault(seat));
                }
            }

            return;
        }

        var teams = _snapshot.Teams.Take(2).ToArray();
        UpdateCompactTeam(teams.ElementAtOrDefault(0), _compactLeft, _compactLeftDetail);
        UpdateCompactTeam(teams.ElementAtOrDefault(1), _compactRight, _compactRightDetail);
    }

    private void UpdateExpanded()
    {
        var teams = _snapshot.Teams.Take(2).ToArray();
        for (var index = 0; index < _teamViews.Count; index++)
        {
            var view = _teamViews[index];
            var team = teams.ElementAtOrDefault(index);
            view.Root.Visibility = team is null ? Visibility.Hidden : Visibility.Visible;
            if (team is null)
            {
                continue;
            }

            view.Name.Text = team.DisplayName;
            view.Name.Foreground = Brush(TeamColor(team.Team));
            view.Score.Text = team.PerformanceScore.HasValue ? $"{team.PerformanceScore:0.0}" : "未評分";
            for (var playerIndex = 0; playerIndex < view.Rows.Count; playerIndex++)
            {
                UpdatePlayerRow(view.Rows[playerIndex], team.Players.ElementAtOrDefault(playerIndex));
            }
        }

        UpdateHistoryControls();
    }

    private void UpdateExpanded(OverlaySnapshotDiff diff)
    {
        if ((diff.Fields & OverlaySnapshotFields.Teams) != 0)
        {
            var teams = _snapshot.Teams.Take(2).ToArray();
            var requiresFullTeamRefresh = diff.Teams.Count == 0 ||
                                          (diff.Fields & OverlaySnapshotFields.TeamOrder) != 0 ||
                                          diff.Teams.Any(team => team.Change != SnapshotItemChange.Updated);
            for (var index = 0; index < _teamViews.Count; index++)
            {
                var team = teams.ElementAtOrDefault(index);
                var teamDiff = team is null
                    ? null
                    : diff.Teams.FirstOrDefault(candidate => candidate.Team == team.Team);
                if (requiresFullTeamRefresh || team is null)
                {
                    UpdateTeamView(_teamViews[index], team);
                }
                else if (teamDiff is not null)
                {
                    UpdateTeamView(_teamViews[index], team, teamDiff);
                }
            }
        }

        if ((diff.Fields & OverlaySnapshotFields.ActiveRiotId) != 0)
        {
            UpdateHistoryControls();
        }
    }

    private void UpdateTeamView(
        TeamView view,
        OverlayTeam? team,
        OverlayTeamDiff? diff = null)
    {
        view.Root.Visibility = team is null ? Visibility.Hidden : Visibility.Visible;
        if (team is null)
        {
            return;
        }

        if (diff is null || (diff.Fields & OverlayTeamFields.DisplayName) != 0)
        {
            view.Name.Text = team.DisplayName;
            view.Name.Foreground = Brush(TeamColor(team.Team));
        }

        if (diff is null || (diff.Fields & OverlayTeamFields.PerformanceScore) != 0)
        {
            view.Score.Text = team.PerformanceScore.HasValue ? $"{team.PerformanceScore:0.0}" : "未評分";
        }

        if (diff is null)
        {
            for (var index = 0; index < view.Rows.Count; index++)
            {
                UpdatePlayerRow(view.Rows[index], team.Players.ElementAtOrDefault(index));
            }
            return;
        }

        if ((diff.Fields & OverlayTeamFields.Players) == 0)
        {
            return;
        }

        var structuralChange = (diff.Fields & OverlayTeamFields.PlayerOrder) != 0 ||
                               diff.Players.Any(player => player.Change != SnapshotItemChange.Updated) ||
                               team.Players.Count != view.Rows.Count(row => row.Root.Visibility == Visibility.Visible);
        if (structuralChange)
        {
            for (var index = 0; index < view.Rows.Count; index++)
            {
                UpdatePlayerRow(view.Rows[index], team.Players.ElementAtOrDefault(index));
            }
            return;
        }

        foreach (var playerDiff in diff.Players)
        {
            var index = -1;
            for (var candidateIndex = 0; candidateIndex < team.Players.Count; candidateIndex++)
            {
                if (string.Equals(
                        team.Players[candidateIndex].StableKey,
                        playerDiff.StableKey,
                        StringComparison.Ordinal))
                {
                    index = candidateIndex;
                    break;
                }
            }

            if (index >= 0 && index < view.Rows.Count)
            {
                UpdatePlayerRow(view.Rows[index], team.Players[index], playerDiff.Fields);
            }
        }
    }

    private void UpdatePlayerRow(PlayerRowView view, OverlayPlayer? player)
    {
        view.Root.Visibility = player is null ? Visibility.Hidden : Visibility.Visible;
        if (player is null)
        {
            return;
        }

        UpdateAvatar(view.Avatar, player);
        view.Champion.Text = player.ChampionName;
        view.Player.Text = player.DisplayName;
        view.Score.Text = player.PerformanceScore.HasValue ? $"{player.PerformanceScore:0.0}" : "—";
        view.Score.Foreground = Brush(ScoreColor(player.PerformanceScore));
        view.Detail.Text = player.PerformanceLabel is null
            ? player.IsAnonymous ? "匿名" : "尚未開始"
            : $"{player.PerformanceLabel} · {ConfidenceText(player.Confidence)}";
        UpdatePlayerMeta(view, player);
    }

    /// <summary>
    /// Pick number during champ select, carried equipment value during a game. Labelled
    /// 裝備值 rather than 經濟 because the client only reports unspent gold for the
    /// local player, so this is shop value on the board and nothing more.
    /// </summary>
    private static void UpdatePlayerMeta(PlayerRowView view, OverlayPlayer player)
    {
        string text;
        if (player.PickOrder is { } pickOrder)
        {
            text = $"第 {pickOrder} 選";
        }
        else if (player.ItemGold is { } itemGold && itemGold > 0)
        {
            text = $"裝備 {itemGold / 1000d:0.0}k";
        }
        else
        {
            text = string.Empty;
        }

        view.Meta.Text = text;
        view.Meta.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdatePlayerRow(
        PlayerRowView view,
        OverlayPlayer player,
        OverlayPlayerFields fields)
    {
        if ((fields & (OverlayPlayerFields.ChampionName |
                       OverlayPlayerFields.ChampionIconPath |
                       OverlayPlayerFields.DisplayName)) != 0)
        {
            UpdateAvatar(view.Avatar, player);
        }

        if ((fields & OverlayPlayerFields.ChampionName) != 0)
        {
            view.Champion.Text = player.ChampionName;
        }

        if ((fields & OverlayPlayerFields.DisplayName) != 0)
        {
            view.Player.Text = player.DisplayName;
        }

        if ((fields & OverlayPlayerFields.PerformanceScore) != 0)
        {
            view.Score.Text = player.PerformanceScore.HasValue ? $"{player.PerformanceScore:0.0}" : "—";
            view.Score.Foreground = Brush(ScoreColor(player.PerformanceScore));
        }

        if ((fields & (OverlayPlayerFields.PerformanceLabel |
                       OverlayPlayerFields.Confidence |
                       OverlayPlayerFields.IsAnonymous)) != 0)
        {
            view.Detail.Text = player.PerformanceLabel is null
                ? player.IsAnonymous ? "匿名" : "尚未開始"
                : $"{player.PerformanceLabel} · {ConfidenceText(player.Confidence)}";
        }

        if ((fields & (OverlayPlayerFields.PickOrder | OverlayPlayerFields.ItemGold)) != 0)
        {
            UpdatePlayerMeta(view, player);
        }
    }

    private void UpdateAvatar(AvatarView view, OverlayPlayer? player)
    {
        view.Root.Visibility = player is null ? Visibility.Hidden : Visibility.Visible;
        if (player is null)
        {
            return;
        }

        if (!string.Equals(view.IconPath, player.ChampionIconPath, StringComparison.OrdinalIgnoreCase))
        {
            view.IconPath = player.ChampionIconPath;
            view.Image.Source = null;
            _ = LoadAvatarAsync(
                view,
                player.ChampionIconPath,
                _visualGenerationCancellation.Token);
        }

        var hasImage = view.Image.Source is not null;
        view.Image.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        view.Initial.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
        view.Initial.Text = string.IsNullOrWhiteSpace(player.ChampionName) ? "?" : player.ChampionName[..1];
        view.Root.ToolTip = $"{player.ChampionName} · {player.DisplayName}";
    }

    private async Task LoadAvatarAsync(
        AvatarView view,
        string? path,
        CancellationToken cancellationToken)
    {
        try
        {
            var image = await _imageCache.GetAsync(path)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.InvokeAsync(() =>
            {
                if (!string.Equals(view.IconPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                view.Image.Source = image;
                view.Image.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
                view.Initial.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
                // Retain the attempted path after a decode failure. Score-only snapshots keep the
                // same icon path, so clearing it here would schedule another decode task on every
                // poll. A changed path (or a rebuilt visual generation) still retries naturally.
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted)
        {
        }
    }

    private static void UpdateCompactTeam(OverlayTeam? team, TextBlock? heading, TextBlock? detail)
    {
        if (heading is null || detail is null)
        {
            return;
        }

        if (team is null)
        {
            heading.Text = "等待隊伍資料";
            detail.Text = string.Empty;
            return;
        }

        heading.Text = $"{team.DisplayName}  {(team.PerformanceScore.HasValue ? $"{team.PerformanceScore:0.0}" : "—")}";
        heading.Foreground = Brush(TeamColor(team.Team));
        var scored = team.Players.Where(player => player.PerformanceScore.HasValue).ToArray();
        detail.Text = scored.Length == 0
            ? "尚未評分"
            : $"本場較高 {scored.MaxBy(player => player.PerformanceScore)!.ChampionName} · " +
              $"較低 {scored.MinBy(player => player.PerformanceScore)!.ChampionName}";
    }

    private void UpdateHistoryControls()
    {
        if (_historyStatus is null || _historyMeta is null || _historyDetails is null || _opGgButton is null)
        {
            return;
        }

        _opGgDestination = TryBuildActivePlayerLink();
        _opGgButton.Visibility = _opGgDestination is null ? Visibility.Collapsed : Visibility.Visible;
        var entry = FindActiveHistoryEntry();
        if (entry?.Profile is { } profile)
        {
            var rank = profile.OfficialRank is null
                ? "官方牌位未提供"
                : $"官方牌位 {profile.OfficialRank.Tier} {profile.OfficialRank.Division}";
            _historyStatus.Text = $"{rank} · {AvailabilityText(entry.Availability)}";
            _historyMeta.Text =
                $"來源：{profile.Source.DisplayName} · {profile.Queue.DisplayName} · {profile.SampleCount} 場 · " +
                $"{profile.FetchedAt.ToLocalTime():MM/dd HH:mm} · {HistoricalConfidenceText(profile.Confidence)}";
            var champions = string.Join("、", profile.CommonChampions.Take(2).Select(item => item.ChampionName));
            _historyDetails.Text =
                $"常用：{(string.IsNullOrWhiteSpace(champions) ? "資料不足" : champions)} · " +
                $"激進 {StyleText(profile.PlayStyle.Aggression.Band)}／生存 {StyleText(profile.PlayStyle.Survival.Band)}／" +
                $"團隊 {StyleText(profile.PlayStyle.TeamParticipation.Band)}／發育 {StyleText(profile.PlayStyle.Farming.Band)}／" +
                $"英雄池 {StyleText(profile.PlayStyle.ChampionPool.Band)}";
            _historyMeta.Visibility = Visibility.Visible;
            _historyDetails.Visibility = Visibility.Visible;
            return;
        }

        // With no profile, the source/sample and disclaimer lines carry nothing a reader
        // acts on, and they cost two lines of Expanded height on every frame. The state
        // itself still says unavailable, which is what the release criteria require.
        var availability = entry?.Availability ?? _historicalProfiles?.Availability ??
            HistoricalProfileAvailability.PolicyDisabled;
        _historyStatus.Text = $"{AvailabilityText(availability)}；不影響上方本場表現，也不會用假資料冒充真人。";
        _historyMeta.Visibility = Visibility.Collapsed;
        _historyDetails.Visibility = Visibility.Collapsed;
    }

    private HistoricalProfileEntry? FindActiveHistoryEntry()
    {
        if (_historicalProfiles is null || string.IsNullOrWhiteSpace(_snapshot.ActiveRiotId))
        {
            return null;
        }

        return _historicalProfiles.Entries.FirstOrDefault(entry =>
            string.Equals(
                $"{entry.Identity.GameName}#{entry.Identity.TagLine}",
                _snapshot.ActiveRiotId,
                StringComparison.OrdinalIgnoreCase));
    }

    private Uri? TryBuildActivePlayerLink()
    {
        var identity = FindActiveHistoryEntry()?.Identity;
        if (identity is null && !TryCreateActiveIdentity(out identity))
        {
            return null;
        }

        if (!OpGgProfileLinkBuilder.TryBuild(identity, out var action))
        {
            return null;
        }

        return action.Destination;
    }

    private bool TryCreateActiveIdentity(out RevealedPlayerIdentity identity)
    {
        identity = null!;
        var riotId = _snapshot.ActiveRiotId;
        if (string.IsNullOrWhiteSpace(riotId) || string.IsNullOrWhiteSpace(_platformRegion))
        {
            return false;
        }

        var separator = riotId.LastIndexOf('#');
        if (separator <= 0 || separator == riotId.Length - 1)
        {
            return false;
        }

        var player = _snapshot.Teams.SelectMany(team => team.Players).FirstOrDefault(candidate =>
            !candidate.IsAnonymous &&
            string.Equals(candidate.DisplayName, riotId, StringComparison.OrdinalIgnoreCase));
        return player is not null && RevealedPlayerIdentity.TryCreateNormallyRevealed(
            player.StableKey,
            riotId[..separator],
            riotId[(separator + 1)..],
            _platformRegion,
            out identity);
    }

    private void OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (IsPositionLocked ||
            e.ChangedButton != MouseButton.Left ||
            IsInteractiveControl(e.OriginalSource as DependencyObject))
        {
            return;
        }

        ProcessPointerActions(_pointer.HandleDown(PointerPosition()));
        e.Handled = true;
    }

    private void OnPointerMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pointer.State == PointerInteractionState.Idle)
        {
            return;
        }

        ProcessPointerActions(_pointer.HandleMove(PointerPosition()));
        e.Handled = true;
    }

    private void OnPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _pointer.State == PointerInteractionState.Idle)
        {
            return;
        }

        ProcessPointerActions(_pointer.HandleUp(PointerPosition()));
        e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_releasingCapture)
        {
            ProcessPointerActions(_pointer.HandleLostCapture());
        }
    }

    private void ProcessPointerActions(PointerActionBatch actions)
    {
        foreach (var action in actions)
        {
            switch (action)
            {
                case { Kind: PointerActionKind.CapturePointer }:
                    Mouse.Capture(this, CaptureMode.SubTree);
                    break;
                case { Kind: PointerActionKind.ReleasePointer }:
                    _releasingCapture = true;
                    try
                    {
                        if (IsMouseCaptured)
                        {
                            ReleaseMouseCapture();
                        }
                    }
                    finally
                    {
                        _releasingCapture = false;
                    }
                    break;
                case { Kind: PointerActionKind.Click } when Mode == OverlayMode.Dot:
                    CycleMode();
                    break;
                case { Kind: PointerActionKind.BeginDrag } begin:
                    _dragPointerOrigin = begin.Origin;
                    _dragWindowOrigin = new DipPoint(Left, Top);
                    MoveWindowToPointer(begin.Position);
                    break;
                case { Kind: PointerActionKind.DragTo } drag:
                    MoveWindowToPointer(drag.Position);
                    break;
                case { Kind: PointerActionKind.EndDrag } end:
                    MoveWindowToPointer(end.Position);
                    ClampToVisibleWorkArea();
                    break;
            }
        }
    }

    private void MoveWindowToPointer(DipPoint current)
    {
        Left = _dragWindowOrigin.X + current.X - _dragPointerOrigin.X;
        Top = _dragWindowOrigin.Y + current.Y - _dragPointerOrigin.Y;
    }

    private DipPoint PointerPosition()
    {
        var local = Mouse.GetPosition(this);
        return new DipPoint(Left + local.X, Top + local.Y);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(_windowHandle, GwlExStyle);
        SetWindowLong(_windowHandle, GwlExStyle, style | WsExToolWindow | WsExNoActivate);
        ApplyWindowInteractionStyle();
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WndProc);
    }

    private void ApplyWindowInteractionStyle()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        var current = GetWindowLong(_windowHandle, GwlExStyle);
        var updated = OverlayNativeStylePolicy.WithPositionLock(current, IsPositionLocked);
        if (updated != current)
        {
            SetWindowLong(_windowHandle, GwlExStyle, updated);
            SetWindowPos(
                _windowHandle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }

        IsHitTestVisible = !IsPositionLocked;
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (!_clamping && double.IsFinite(Left) && double.IsFinite(Top))
        {
            PositionChanged?.Invoke(Left, Top);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(ClampToVisibleWorkArea);

    private void OnClosed(object? sender, EventArgs e)
    {
        _visualGenerationCancellation.Cancel();
        _visualGenerationCancellation.Dispose();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _source?.RemoveHook(WndProc);
    }

    private void ClampToVisibleWorkArea()
    {
        if (_clamping || !double.IsFinite(Width) || !double.IsFinite(Height))
        {
            return;
        }

        var original = new DipPoint(Left, Top);
        var adjusted = false;
        _clamping = true;
        try
        {
            var result = OverlayPlacement.Clamp(
                new DipPoint(Left, Top),
                new DipSize(Math.Max(ActualWidth, Width), Math.Max(ActualHeight, Height)),
                GetWorkAreas());
            Left = result.Position.X;
            Top = result.Position.Y;
            adjusted = result.Position != original;
        }
        finally
        {
            _clamping = false;
        }

        if (adjusted && double.IsFinite(Left) && double.IsFinite(Top))
        {
            PositionChanged?.Invoke(Left, Top);
        }
    }

    private IReadOnlyList<DisplayWorkArea> GetWorkAreas()
    {
        var fallbackDpi = VisualTreeHelper.GetDpi(this);
        var fallbackDpiX = (uint)Math.Max(96, Math.Round(fallbackDpi.PixelsPerInchX));
        var fallbackDpiY = (uint)Math.Max(96, Math.Round(fallbackDpi.PixelsPerInchY));
        var physicalDisplays = Forms.Screen.AllScreens.Select(screen =>
        {
            var area = screen.WorkingArea;
            var bounds = screen.Bounds;
            var monitor = MonitorFromPoint(
                new NativePoint(
                    bounds.Left + bounds.Width / 2,
                    bounds.Top + bounds.Height / 2),
                MonitorDefaultToNearest);
            var dpiX = fallbackDpiX;
            var dpiY = fallbackDpiY;
            if (monitor != IntPtr.Zero &&
                GetDpiForMonitor(monitor, MonitorDpiTypeEffective, out var monitorDpiX, out var monitorDpiY) == 0)
            {
                dpiX = monitorDpiX;
                dpiY = monitorDpiY;
            }

            return new PhysicalDisplayWorkArea(
                screen.DeviceName,
                new PixelRect(
                    bounds.Left,
                    bounds.Top,
                    bounds.Width,
                    bounds.Height),
                new PixelRect(
                    area.Left,
                    area.Top,
                    area.Width,
                    area.Height),
                dpiX,
                dpiY,
                screen.Primary);
        }).ToArray();
        return DisplayTopologyConverter.ToDips(physicalDisplays);
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

        if (IsPositionLocked)
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        var packed = lParam.ToInt64();
        var screenPoint = new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff));
        var localPoint = PointFromScreen(screenPoint);
        var hit = InputHitTest(localPoint) as DependencyObject;
        if (IsInteractiveControl(hit))
        {
            return IntPtr.Zero;
        }

        var surface = HasTag(hit, "DragGrip")
            ? OverlaySurfaceKind.DedicatedGrip
            : OverlaySurfaceKind.Background;
        if (!OverlayInteractionPolicyRules.CanStartPointerGesture(
                OverlayInteractionPolicy.FullSurfaceGesture,
                surface,
                IsPositionLocked))
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        return IntPtr.Zero;
    }

    private static bool IsInteractiveControl(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase or TextBoxBase or Slider or ComboBox || HasOwnTag(element, "Interactive"))
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private static bool HasTag(DependencyObject? element, string tag)
    {
        while (element is not null)
        {
            if (HasOwnTag(element, tag))
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private static bool HasOwnTag(DependencyObject element, string tag) =>
        element is FrameworkElement framework && Equals(framework.Tag, tag);

    private Border Card() => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(242, 17, 23, 34)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(150, 75, 91, 118)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(13)
    };

    private static Button SmallButton(string text, string toolTip) => new()
    {
        Content = text,
        Width = 34,
        Height = 32,
        Padding = new Thickness(0),
        Margin = new Thickness(5, 0, 0, 0),
        Background = new SolidColorBrush(Color.FromArgb(100, 53, 65, 86)),
        BorderThickness = new Thickness(0),
        Foreground = new SolidColorBrush(Color.FromRgb(221, 229, 241)),
        ToolTip = toolTip,
        Cursor = Cursors.Hand,
        Tag = "Interactive"
    };

    private static TextBlock Text(
        string text,
        double size,
        string color,
        FontWeight? weight = null,
        HorizontalAlignment alignment = HorizontalAlignment.Left) => new()
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

    private static SolidColorBrush Brush(string hex) => BrushesByHex.GetOrAdd(hex, static value =>
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    });

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
        _ => "資料不足"
    };

    private static string HistoricalConfidenceText(HistoricalConfidence confidence) => confidence switch
    {
        HistoricalConfidence.High => "高信心",
        HistoricalConfidence.Medium => "中信心",
        HistoricalConfidence.Low => "低信心",
        _ => "資料不足"
    };

    private static string StyleText(HistoricalStyleBand band) => band switch
    {
        HistoricalStyleBand.VeryLow => "很低",
        HistoricalStyleBand.Low => "偏低",
        HistoricalStyleBand.High => "偏高",
        HistoricalStyleBand.VeryHigh => "很高",
        _ => "平衡"
    };

    private static string AvailabilityText(HistoricalProfileAvailability availability) => availability switch
    {
        HistoricalProfileAvailability.Available => "近期資料可用",
        HistoricalProfileAvailability.Partial => "僅取得部分近期資料",
        HistoricalProfileAvailability.Stale => "顯示較舊的快取資料",
        HistoricalProfileAvailability.Offline => "目前離線，歷史資料不可用",
        HistoricalProfileAvailability.PolicyDisabled => "尚未啟用經核准的歷史資料來源",
        HistoricalProfileAvailability.NotFound => "找不到公開歷史資料",
        HistoricalProfileAvailability.RateLimited => "來源忙碌，稍後再試",
        HistoricalProfileAvailability.ServerError => "歷史來源暫時失效",
        HistoricalProfileAvailability.Timeout => "歷史查詢逾時",
        HistoricalProfileAvailability.Malformed => "歷史資料格式無法辨識",
        _ => "歷史資料暫時無法取得"
    };

    private SolidColorBrush DotBrush()
    {
        if (!_snapshot.TeamGap.HasValue || _snapshot.TeamGap.Value < 3d || !_snapshot.LeadingTeam.HasValue)
        {
            return Brush("#8794A8");
        }

        if (!_snapshot.ActiveTeam.HasValue)
        {
            return Brush(_snapshot.LeadingTeam is 100 or 1 ? "#62AEFF" : "#FF7D8E");
        }

        return Brush(_snapshot.LeadingTeam == _snapshot.ActiveTeam ? "#55D99A" : "#FF6F83");
    }

    private sealed record AvatarView(Border Root, Image Image, TextBlock Initial)
    {
        public string? IconPath { get; set; }
    }

    private sealed record PlayerRowView(
        Border Root,
        AvatarView Avatar,
        TextBlock Champion,
        TextBlock Player,
        TextBlock Score,
        TextBlock Detail,
        TextBlock Meta);

    private sealed record TeamView(
        Border Root,
        TextBlock Name,
        TextBlock Score,
        IReadOnlyList<PlayerRowView> Rows);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr windowHandle, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr windowHandle, int index, int newLong);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

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
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }
}
