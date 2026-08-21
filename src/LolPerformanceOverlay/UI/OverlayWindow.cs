using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
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
    // The rank cell's cross-queue mark (see OfficialRankDisplay.IsFromDifferentQueue): a
    // dotted underline in the rank text's own colour, not a new one -- AGENTS.md rule 6
    // forbids marking state by colour alone, and a dotted underline is a shape distinction
    // that costs zero extra width, so the rank column's existing 25px budget (already proven
    // to fit the longest real code, "GM*") never has to grow, and the champion name column
    // next to it is untouched. It also happens to be the same convention browsers and wikis
    // already use for "there is more detail on hover" (e.g. <abbr title="">), which is
    // exactly what this is -- the row tooltip spells out which queue the rank actually came
    // from (see UpdateRowTooltip/OfficialRankDisplay.TooltipText).
    private static readonly TextDecorationCollection CrossQueueRankDecorations = BuildCrossQueueRankDecorations();

    private readonly AppSettings _settings;
    private readonly ChampionImageCache _imageCache = new();
    private readonly PointerInteractionStateMachine _pointer = new(5);
    private readonly List<AvatarView> _compactAvatars = [];
    private readonly List<PlayerRowView> _playerRows = [];
    private readonly List<TeamView> _teamViews = [];
    // One column-header row per team card (see CreateColumnHeaderRow) -- populated whenever
    // Expanded is built, cleared alongside everything else in BuildModeVisual. Kept as instance
    // fields, not local to BuildExpanded, because a settings-only change (NameDisplayMode) must
    // repaint the name header without a full mode rebuild -- see RefreshPlayerNameDisplay.
    private readonly List<TextBlock> _nameColumnHeaders = [];
    private readonly List<TextBlock> _metaColumnHeaders = [];
    private CancellationTokenSource _visualGenerationCancellation = new();
    private OverlaySnapshot _snapshot = OverlaySnapshot.Empty();
    private HistoricalProfilesResult? _historicalProfiles;
    private HwndSource? _source;
    private Run? _headerRun;
    private Run? _summaryRun;
    private Border? _dot;
    private TextBlock? _compactLeft;
    private TextBlock? _compactLeftDetail;
    private TextBlock? _compactRight;
    private TextBlock? _compactRightDetail;
    private Button? _opGgButton;
    private Uri? _opGgDestination;
    private bool _visualWasChampSelect;
    private bool _releasingCapture;
    private DipPoint _dragPointerOrigin;
    private DipPoint _dragWindowOrigin;
    private bool _clamping;
    private IntPtr _windowHandle;
    private Slider? _menuOpacitySlider;
    private TextBlock? _menuOpacityValue;
    private bool _suppressMenuOpacityChangedEvent;

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
        Opacity = OverlayOpacityPolicy.Clamp(settings.Opacity);
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

        // Attached to the Window itself, not to Content, so it survives BuildModeVisual
        // rebuilding Content on every mode switch -- one menu instance reachable by
        // right-clicking anywhere on the overlay in Dot, Compact, or Expanded alike, at zero
        // layout cost since a ContextMenu is a Popup, not part of any mode's fixed layout.
        // SettingsWindow needs an explicit Owner (see its constructor comment) because it is
        // a separate Window of its own and once had a real bug where a missing Owner left it
        // permanently behind this Topmost overlay. A ContextMenu does not have that failure
        // mode: WPF opens it as a popup owned by whichever window it is attached to (this one)
        // without any extra wiring, and an owned window always stacks above its owner, so it
        // renders above the overlay -- and therefore above the game -- the same way the
        // Settings dialog does, with no separate Topmost/Owner to remember here.
        ContextMenu = BuildContextMenu();
        ContextMenuOpening += OnContextMenuOpening;

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
    // Raised only for a user-driven drag of the right-click menu's opacity slider (see
    // OnMenuOpacityChanged) -- not for the resync OnContextMenuOpening does on every open, and
    // not for ApplySettings/the constructor. Mirrors PositionChanged: the handler in App
    // writes it into AppSettings and saves through the same debounced settings-save path
    // dragging the overlay's position already uses, rather than a second persistence path.
    public event Action<double>? OpacityChanged;

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
            CurrentDipSize(),
            workAreas);
        Left = result.Position.X;
        Top = result.Position.Y;
        PositionChanged?.Invoke(Left, Top);
    }

    public void ApplySettings(AppSettings settings)
    {
        Opacity = OverlayOpacityPolicy.Clamp(settings.Opacity);
        SetPositionLocked(settings.PositionLocked);
        if (!double.IsFinite(settings.Left) || !double.IsFinite(settings.Top))
        {
            ResetPosition();
        }

        ClampToVisibleWorkArea();

        // A NameDisplayMode change does not touch OverlaySnapshot at all, so nothing about it
        // ever shows up in an OverlaySnapshotDiff -- unlike PositionLocked/Opacity above, the
        // rows have to be told about it explicitly here, or the new choice would only take
        // effect after the next unrelated snapshot update (or a mode switch) rebuilds them.
        if (_settings.NameDisplayMode != settings.NameDisplayMode)
        {
            _settings.NameDisplayMode = settings.NameDisplayMode;
            RefreshPlayerNameDisplay();
        }
    }

    /// <summary>
    /// Repaints the name column header and every currently visible player row's name cell from
    /// <see cref="_settings"/>.NameDisplayMode and the existing <see cref="_snapshot"/> -- no
    /// new data is fetched, this only changes which already-known field of each
    /// <see cref="OverlayPlayer"/> is shown (see <see cref="PlayerNameDisplay.Resolve"/>).
    /// Safe to call outside Expanded mode: the header list only has entries when Expanded has
    /// actually been built at least once, and the row loop below is skipped entirely otherwise.
    /// </summary>
    private void RefreshPlayerNameDisplay()
    {
        var headerText = PlayerNameDisplay.ColumnHeader(_settings.NameDisplayMode);
        foreach (var header in _nameColumnHeaders)
        {
            header.Text = headerText;
        }

        if (Mode != OverlayMode.Expanded)
        {
            return;
        }

        var teams = _snapshot.Teams.Take(2).ToArray();
        for (var teamIndex = 0; teamIndex < _teamViews.Count; teamIndex++)
        {
            var team = teams.ElementAtOrDefault(teamIndex);
            if (team is null)
            {
                continue;
            }

            var rows = _teamViews[teamIndex].Rows;
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var player = team.Players.ElementAtOrDefault(rowIndex);
                if (player is not null)
                {
                    UpdatePlayerName(rows[rowIndex], player);
                }
            }
        }
    }

    /// <summary>
    /// Right-click reachability for the opacity slider (「一邊拉動透明度條的時候需要能即時反饋」--
    /// the same live control the Settings dialog has, but reachable mid-game in every mode
    /// without opening Settings, since Dot and Compact never show a settings button at all).
    /// Built once and attached to the Window itself in the constructor, not to Content, so
    /// mode switches (which rebuild Content -- see BuildModeVisual) never disturb it.
    /// </summary>
    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromArgb(248, 17, 23, 34)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, 75, 91, 118)),
            BorderThickness = new Thickness(1),
            Foreground = Brushes.White
        };

        var headerRow = new Grid { Margin = new Thickness(10, 8, 10, 2) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition());
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = Text("透明度", 12.5, "#DCE8F8", FontWeights.SemiBold);
        _menuOpacityValue = Text(string.Empty, 12.5, "#B9C7DB", FontWeights.SemiBold, HorizontalAlignment.Right);
        Grid.SetColumn(_menuOpacityValue, 1);
        headerRow.Children.Add(heading);
        headerRow.Children.Add(_menuOpacityValue);

        _menuOpacitySlider = new Slider
        {
            Minimum = OverlayOpacityPolicy.Minimum,
            Maximum = OverlayOpacityPolicy.Maximum,
            Value = OverlayOpacityPolicy.Clamp(_settings.Opacity),
            TickFrequency = 0.05,
            IsSnapToTickEnabled = true,
            Width = 200,
            Margin = new Thickness(10, 0, 10, 8)
        };
        _menuOpacitySlider.ValueChanged += OnMenuOpacityChanged;
        UpdateMenuOpacityValueLabel(_menuOpacitySlider.Value);

        var content = new StackPanel();
        content.Children.Add(headerRow);
        content.Children.Add(_menuOpacitySlider);

        var opacityItem = new MenuItem
        {
            // Keeps the menu open through the whole drag gesture instead of treating the
            // first tick as an item click and closing -- the same mechanism WPF offers for
            // checkable items that should stay visible after being toggled.
            StaysOpenOnClick = true,
            Header = content
        };
        menu.Items.Add(opacityItem);
        menu.Items.Add(new Separator());

        var openSettings = new MenuItem { Header = "開啟設定…", Foreground = Brushes.White };
        openSettings.Click += (_, _) => SettingsRequested?.Invoke();
        menu.Items.Add(openSettings);

        return menu;
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!OverlayContextMenuPolicy.CanOpen(Mode, IsPositionLocked))
        {
            // Locked overlays are click-through by design (see ApplyWindowInteractionStyle
            // and the WM_NCHITTEST handling in WndProc below): the whole overlay stops
            // receiving mouse input while locked, so WPF should never even dispatch this
            // event in that state. This guard is defence in depth, and the one place the
            // rule is written down and unit-tested (see OverlayContextMenuPolicy) instead of
            // relying only on that emergent side effect -- it must not be "fixed" by
            // weakening the lock.
            e.Handled = true;
            return;
        }

        if (_menuOpacitySlider is null)
        {
            return;
        }

        // The opacity may have changed since the menu was last open (e.g. via the Settings
        // dialog), so the slider is resynced to what is actually in effect right now rather
        // than wherever it was last left. Suppressed so this resync does not itself look
        // like a user edit and trigger a settings save (see OnMenuOpacityChanged).
        _suppressMenuOpacityChangedEvent = true;
        try
        {
            _menuOpacitySlider.Value = OverlayOpacityPolicy.Clamp(_settings.Opacity);
        }
        finally
        {
            _suppressMenuOpacityChangedEvent = false;
        }

        UpdateMenuOpacityValueLabel(_menuOpacitySlider.Value);
    }

    private void OnMenuOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var previewed = OverlayOpacityPolicy.Clamp(e.NewValue);
        Opacity = previewed;
        UpdateMenuOpacityValueLabel(previewed);
        if (_suppressMenuOpacityChangedEvent)
        {
            return;
        }

        _settings.Opacity = previewed;
        OpacityChanged?.Invoke(previewed);
    }

    private void UpdateMenuOpacityValueLabel(double opacity)
    {
        if (_menuOpacityValue is not null)
        {
            _menuOpacityValue.Text = FormatOpacityPercent(opacity);
        }
    }

    // Manual percentage formatting instead of "P0" -- some cultures insert a space before the
    // percent sign under that format specifier, which is not how percentages read elsewhere in
    // this UI (see SettingsWindow.FormatOpacityPercent, kept in sync with the same rule).
    private static string FormatOpacityPercent(double opacity) => $"{(int)Math.Round(opacity * 100)}%";

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
        _nameColumnHeaders.Clear();
        _metaColumnHeaders.Clear();
        _headerRun = null;
        _summaryRun = null;
        _dot = null;
        _compactLeft = null;
        _compactLeftDetail = null;
        _compactRight = null;
        _compactRightDetail = null;
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
        SizeToContent = SizeToContent.Manual;
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
        SizeToContent = SizeToContent.Manual;
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
                    12.5,
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
        // A fixed height cannot fit champ select and a live game without leaving dead space in
        // the shorter of the two. Let the content decide; ClampToWorkArea reads ActualHeight
        // while this is on. The old bottom history block used to add a third, taller state to
        // that same problem; removing it (see the header's OP.GG button) only shrank the range.
        Width = 520;
        SizeToContent = SizeToContent.Height;
        var root = Card();
        var layout = new DockPanel();
        var header = BuildHeader(allowSettings: true, allowOpGg: true);
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        var teamsGrid = new Grid { Margin = new Thickness(10, 0, 10, 8) };
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

    private Border BuildHeader(bool allowSettings, bool allowOpGg = false)
    {
        var border = new Border { Padding = new Thickness(14, 10, 10, 8) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // One line, not two: the phase name and the reading it's showing were on
        // separate rows, which cost height without adding anything the reader needed
        // both parts of at a glance. Two Run weights keep the phase name legible
        // without a second line.
        // The summary is the reading the player actually came for, so it is not dimmed
        // below the phase label beside it; only the separator recedes.
        _headerRun = new Run(string.Empty) { Foreground = Brush("#F5F7FB"), FontWeight = FontWeights.SemiBold };
        _summaryRun = new Run(string.Empty) { Foreground = Brush("#C8D6EA"), FontWeight = FontWeights.Medium };
        var text = new TextBlock
        {
            FontSize = 13.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        text.Inlines.Add(_headerRun);
        text.Inlines.Add(new Run("  ·  ") { Foreground = Brush("#78889D") });
        text.Inlines.Add(_summaryRun);
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

        if (allowOpGg)
        {
            // Mounted but collapsed by default: UpdateHistoryControls only sets it Visible once
            // a multi-search link can actually be built, which needs at least one revealed
            // player's identity from _historicalProfiles. That is populated as soon as the
            // in-game roster resolves -- with or without a configured Riot key, since even the
            // no-key PolicyDisabledHistoricalProfileProvider still returns one entry per
            // revealed player -- so the button appears whenever there is a live game to link to
            // and stays hidden the rest of the time rather than reserving space for nothing.
            _opGgButton = SmallButton("↗", "由你主動在瀏覽器開啟本場所有玩家的 OP.GG；Overlay 不讀回網頁資料");
            _opGgButton.Click += (_, _) =>
            {
                if (_opGgDestination is not null)
                {
                    OpenExternalLinkRequested?.Invoke(_opGgDestination);
                }
            };
            buttons.Children.Add(_opGgButton);
        }

        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        border.Child = grid;
        return border;
    }

    private TeamView CreateTeamView()
    {
        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(80, 38, 48, 67)),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7)
        };
        var stack = new StackPanel();
        var heading = new Grid { Margin = new Thickness(3, 1, 3, 6) };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = Text(string.Empty, 14.5, "#74B7FF", FontWeights.SemiBold);
        var score = Text(string.Empty, 14.5, "#F5F7FB", FontWeights.SemiBold);
        Grid.SetColumn(score, 1);
        heading.Children.Add(name);
        heading.Children.Add(score);
        stack.Children.Add(heading);
        stack.Children.Add(CreateColumnHeaderRow());
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

    /// <summary>
    /// One header row per team card, not one shared across both -- see the width comment in
    /// <see cref="CreatePlayerRow"/>: each card already owns its own Padding(7), and the row
    /// grid's own Padding(4,0,5,0) is relative to that. Building a single header spanning both
    /// cards would mean re-deriving that same offset twice inside one wider container instead of
    /// reusing it; a header scoped to each card can instead share <see cref="AddPlayerRowColumns"/>
    /// with the data rows below it, so the two are pixel-aligned by construction, never by
    /// coincidence. This costs no extra height over a shared header either: the two cards sit
    /// side by side in <see cref="BuildExpanded"/>'s teamsGrid, not stacked, so the panel only
    /// grows by one header row's height, not two.
    ///
    /// Deliberately labels all four data columns (name, meta, rank, score), not just the two the
    /// user asked for (meta/rank had none at all before this change) -- a row with two labelled
    /// columns and two silently unlabelled ones reads as broken formatting, not as "these two
    /// don't need one". The avatar column alone stays blank: it is a picture, and at 28px wide
    /// there is no room for a word that would say anything a glance at the portrait does not
    /// already say. Every label is smaller (10px vs. 12.5-17px for the data below it) and dimmer
    /// (#7A879C vs. the data columns' own colours) so the row of labels reads as subordinate
    /// captioning, not as a fifth row of data competing with the real numbers.
    /// </summary>
    private Grid CreateColumnHeaderRow()
    {
        var grid = new Grid { Margin = new Thickness(4, 0, 5, 3) };
        AddPlayerRowColumns(grid);

        var name = Text(
            PlayerNameDisplay.ColumnHeader(_settings.NameDisplayMode),
            10,
            "#7A879C",
            FontWeights.SemiBold);
        // Matches the champion cell's own Margin(2,0,6,0) below (see CreatePlayerRow) so the
        // label's left edge lines up with the text it is labelling, not just the column.
        name.Margin = new Thickness(2, 0, 6, 0);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        _nameColumnHeaders.Add(name);

        // "順位" during champ select, "裝備值" during a live game -- never "經濟" (AGENTS.md:
        // the client only exposes the local player's unspent gold, so a summed item-value total
        // is not the same thing as economy). Matches whichever of the two UpdatePlayerMeta is
        // actually filling the cell with for the phase this panel was built for; the choice is
        // fixed at build time and does not need to react to ApplySettings the way the name
        // header does, since only a structural rebuild (BuildModeVisual) ever changes
        // _visualWasChampSelect, and that rebuild calls this method fresh.
        var meta = Text(
            _visualWasChampSelect ? "順位" : "裝備值",
            10,
            "#7A879C",
            FontWeights.SemiBold,
            HorizontalAlignment.Right);
        Grid.SetColumn(meta, 2);
        grid.Children.Add(meta);
        _metaColumnHeaders.Add(meta);

        var rank = Text("牌位", 10, "#7A879C", FontWeights.SemiBold, HorizontalAlignment.Right);
        Grid.SetColumn(rank, 3);
        grid.Children.Add(rank);

        var score = Text("分數", 10, "#7A879C", FontWeights.SemiBold, HorizontalAlignment.Right);
        Grid.SetColumn(score, 4);
        grid.Children.Add(score);

        return grid;
    }

    /// <summary>
    /// The five column widths shared by every player row and by <see cref="CreateColumnHeaderRow"/>
    /// above them, so header and data can never drift out of alignment. Column widths were
    /// rebalanced, not just extended, when the rank column was added: avatar 34->28, meta
    /// 42->38, score 46->34 (each still comfortably fits its longest real value -- three
    /// digits, "#10", "99.9k"), freeing exactly the width the new rank column needs so the
    /// champion name's share of the row is unchanged.
    ///
    /// These five widths are unchanged again here for the cross-queue fallback mark (see
    /// OfficialRankDisplay.IsFromDifferentQueue): a dotted underline is a TextDecoration on
    /// the existing rank TextBlock, not additional characters, so it costs no horizontal
    /// space at all. Worked from BuildExpanded/CreateTeamView: window 520px, teamsGrid
    /// Margin(10,0,10,8) leaves 500px for two team columns plus a 12px gutter, so each team
    /// column is (500-12)/2 = 244px; the team card's Padding(7) leaves 244-14 = 230px; this
    /// row's own Padding(4,0,5,0) leaves 230-9 = 221px; the four OTHER fixed columns
    /// (28+38+25+34 = 125px) leave the champion column exactly 221-125 = 96px, same as
    /// before this change, because the rank column's own 25px did not move. See
    /// CrossQueueRankDecorations and UpdatePlayerRank.
    /// </summary>
    private static void AddPlayerRowColumns(Grid grid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
    }

    private PlayerRowView CreatePlayerRow()
    {
        // One line per player. The portrait already identifies who this is, the score's
        // colour already says how the number reads, and confidence is the same for every
        // row, so the name, the label and the per-row confidence were all repeating
        // what the reader could already see. They move to the tooltip and the header.
        var root = new Border
        {
            Height = 34,
            Margin = new Thickness(0, 0, 0, 3),
            // Trimmed from the original (5,0,7,0) to make room for the rank column below
            // without taking any width away from the champion name -- see the column
            // widths comment beside the rank TextBlock.
            Padding = new Thickness(4, 0, 5, 0),
            Background = new SolidColorBrush(Color.FromArgb(92, 22, 29, 42)),
            CornerRadius = new CornerRadius(6)
        };
        var grid = new Grid();
        AddPlayerRowColumns(grid);
        var avatar = CreateAvatar(26);
        grid.Children.Add(avatar.Root);
        var champion = Text(string.Empty, 13, "#EEF3FA", FontWeights.SemiBold);
        champion.Margin = new Thickness(2, 0, 6, 0);
        Grid.SetColumn(champion, 1);
        grid.Children.Add(champion);
        // Champ select shows this cell's pick number; a live game shows carried item value.
        // Sized and weighted like real data, not a caption: at 11px in mid grey it washed
        // out against a bright game behind the panel and could not be read at a glance.
        var meta = Text(
            string.Empty,
            13,
            "#B9C7DB",
            FontWeights.SemiBold,
            HorizontalAlignment.Right);
        Grid.SetColumn(meta, 2);
        grid.Children.Add(meta);
        // Official rank short code (e.g. "D4"); Core has already formatted it (see
        // OfficialRankDisplay), this adapter only ever displays the string. Colour alone is
        // not a legitimate way to separate it from the score (AGENTS.md rule 6 -- colour-blind
        // readers must not lose the distinction), so it also gets its own column (position),
        // a smaller SemiBold size against the score's larger Bold, and italics: a quoted-data
        // convention that reads as "sourced from elsewhere" the moment you see it, at zero
        // extra width or height. Hovering the row spells the same separation out in words (see
        // UpdateRowTooltip/OfficialRankDisplay.TooltipText) for anyone the typography alone
        // does not reach. Blank (never fetched, unranked, lookup failed) leaves the cell empty
        // rather than showing a word here; UpdatePlayerRank explains the state on hover instead.
        var rank = Text(string.Empty, 12.5, "#D9B36C", FontWeights.SemiBold, HorizontalAlignment.Right);
        rank.FontStyle = FontStyles.Italic;
        Grid.SetColumn(rank, 3);
        grid.Children.Add(rank);
        var score = Text(string.Empty, 17, "#E7EDF7", FontWeights.Bold, HorizontalAlignment.Right);
        Grid.SetColumn(score, 4);
        grid.Children.Add(score);
        root.Child = grid;
        return new PlayerRowView(root, avatar, champion, score, meta, rank);
    }

    private static StackPanel CompactTeam(
        out TextBlock heading,
        out TextBlock detail,
        HorizontalAlignment alignment)
    {
        var stack = new StackPanel { HorizontalAlignment = alignment };
        heading = Text(string.Empty, 14.5, "#DCE8F8", FontWeights.SemiBold);
        detail = Text(string.Empty, 13, "#BAC8DC", FontWeights.Medium);
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
        if (_headerRun is not null)
        {
            _headerRun.Text = _snapshot.Header;
        }

        if (_summaryRun is not null)
        {
            _summaryRun.Text = _snapshot.Summary;
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
        if (_headerRun is not null &&
            (diff.Fields & OverlaySnapshotFields.Header) != 0)
        {
            _headerRun.Text = _snapshot.Header;
        }

        if (_summaryRun is not null &&
            (diff.Fields & OverlaySnapshotFields.Summary) != 0)
        {
            _summaryRun.Text = _snapshot.Summary;
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
            // Collapsed, not Hidden: Expanded now sizes to content, so a team card that
            // reserves space while invisible (Hidden) would hold the window open at its
            // tallest state -- most visibly during EndOfGame, when both teams are gone.
            view.Root.Visibility = team is null ? Visibility.Collapsed : Visibility.Visible;
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

        // The OP.GG button no longer tracks a single active player (see UpdateHistoryControls),
        // so it has nothing left to react to here -- it only changes when
        // ApplyHistoricalProfiles/ClearHistoricalProfiles run, and both already call
        // UpdateHistoryControls directly.
    }

    private void UpdateTeamView(
        TeamView view,
        OverlayTeam? team,
        OverlayTeamDiff? diff = null)
    {
        // Collapsed, not Hidden: see UpdateExpanded's full-refresh branch above.
        view.Root.Visibility = team is null ? Visibility.Collapsed : Visibility.Visible;
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
        UpdatePlayerName(view, player);
        view.Score.Text = player.PerformanceScore.HasValue ? $"{player.PerformanceScore:0}" : "—";
        view.Score.Foreground = Brush(ScoreColor(player.PerformanceScore));
        UpdatePlayerMeta(view, player);
        UpdatePlayerRank(view, player);
        UpdateRowTooltip(view, player);
    }

    /// <summary>
    /// The name/champion/score block below is composed here, same as always. The official-rank
    /// block appended after it is not -- <see cref="OfficialRankDisplay.TooltipText"/> already
    /// arrives fully worded from Core (full tier name, LP, queue, source, fetch time, staleness
    /// in words, and the Riot-vs-this-game separation AGENTS.md rule 9 requires); this adapter
    /// only ever displays that string, it does not decide any of its wording.
    /// </summary>
    // All three lines are composed in Core (see RowTooltip) so they can be asserted directly;
    // this adapter only hands the string to WPF.
    private static void UpdateRowTooltip(PlayerRowView view, OverlayPlayer player) =>
        view.Root.ToolTip = RowTooltip.Compose(player);

    /// <summary>
    /// The name column's text: Core decides between the champion name and the Riot ID (and
    /// guarantees anonymous players never get the latter) -- this adapter only ever displays
    /// the string <see cref="PlayerNameDisplay.Resolve"/> returns. See
    /// <see cref="_settings"/>.NameDisplayMode and <see cref="RefreshPlayerNameDisplay"/> for
    /// how a setting change reaches an already-built row without a full rebuild.
    /// </summary>
    private void UpdatePlayerName(PlayerRowView view, OverlayPlayer player) =>
        view.Champion.Text = PlayerNameDisplay.Resolve(player, _settings.NameDisplayMode);

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
            text = $"#{pickOrder}";
        }
        else if (player.ItemGold is { } itemGold && itemGold > 0)
        {
            text = $"{itemGold / 1000d:0.0}k";
        }
        else
        {
            text = string.Empty;
        }

        view.Meta.Text = text;
        view.Meta.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// The rank column's text is the short code Core already formatted; a player with no
    /// resolved rank yet (never fetched, unranked, lookup failed) leaves the cell blank
    /// rather than showing a word here -- the human-readable status lives in the row's
    /// tooltip instead (see UpdateRowTooltip/OfficialRankDisplay.TooltipText), where it has
    /// room to be a full sentence instead of a 25px-wide word. A dotted underline is added
    /// when Core says this rank is a Solo/Flex fallback shown in a ranked queue's own game
    /// (see OfficialRankDisplay.IsFromDifferentQueue and CrossQueueRankDecorations above) --
    /// never for a no-ladder queue like ARAM, where every rank is a fallback and a mark on
    /// every row would just be noise.
    /// </summary>
    private static void UpdatePlayerRank(PlayerRowView view, OverlayPlayer player)
    {
        var text = player.OfficialRank?.ShortCode ?? string.Empty;
        view.Rank.Text = text;
        view.Rank.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        view.Rank.TextDecorations = player.OfficialRank?.IsFromDifferentQueue == true
            ? CrossQueueRankDecorations
            : null;
    }

    // A dotted (not solid) underline, in the rank text's own gold rather than a new colour, so
    // it reads as a shape/pattern distinction, not a colour-only one. Frozen once at startup --
    // TextDecorationCollection is shared by every row, never mutated per-player.
    private static TextDecorationCollection BuildCrossQueueRankDecorations()
    {
        // Offset and thickness are both literal pixels (not FontRecommended, which would scale
        // with the 12.5pt rank font unpredictably): 1px further below the text than a default
        // underline, 1px thick. At 12.5pt the rank text's own line height is roughly 17px, so
        // glyph plus underline together stay well inside the 34px row -- there was already
        // several pixels of unused space below a single line of 12.5pt text in a 34px row
        // before this, and the underline only needs one more.
        var pen = new Pen(Brush("#D9B36C"), 1) { DashStyle = DashStyles.Dot };
        pen.Freeze();
        var decorations = new TextDecorationCollection
        {
            new TextDecoration(TextDecorationLocation.Underline, pen, 1, TextDecorationUnit.Pixel, TextDecorationUnit.Pixel)
        };
        decorations.Freeze();
        return decorations;
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

        // DisplayName and IsAnonymous, not just ChampionName, because PlayerNameDisplay.Resolve
        // reads all three -- a Riot-ID-mode row must repaint if the revealed name changes or a
        // seat's anonymity flips, not only when the champion itself changes.
        if ((fields & (OverlayPlayerFields.ChampionName |
                       OverlayPlayerFields.DisplayName |
                       OverlayPlayerFields.IsAnonymous)) != 0)
        {
            UpdatePlayerName(view, player);
        }

        if ((fields & OverlayPlayerFields.PerformanceScore) != 0)
        {
            view.Score.Text = player.PerformanceScore.HasValue ? $"{player.PerformanceScore:0}" : "—";
            view.Score.Foreground = Brush(ScoreColor(player.PerformanceScore));
        }

        if ((fields & (OverlayPlayerFields.PickOrder | OverlayPlayerFields.ItemGold)) != 0)
        {
            UpdatePlayerMeta(view, player);
        }

        if ((fields & OverlayPlayerFields.OfficialRank) != 0)
        {
            UpdatePlayerRank(view, player);
        }

        if ((fields & (OverlayPlayerFields.DisplayName |
                       OverlayPlayerFields.ChampionName |
                       OverlayPlayerFields.PerformanceLabel |
                       OverlayPlayerFields.Confidence |
                       OverlayPlayerFields.IsAnonymous |
                       OverlayPlayerFields.OfficialRank)) != 0)
        {
            UpdateRowTooltip(view, player);
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
        if (_opGgButton is null)
        {
            return;
        }

        _opGgDestination = TryBuildRosterLink();
        // Collapsed, not Hidden: Expanded sizes to content (SizeToContent.Height), so Hidden
        // would still reserve the button's layout space and hold the window a few pixels
        // taller than it needs to be whenever there is nothing to link to yet (e.g. right at
        // game start, before the first historical lookup lands) or nothing left to link to
        // (e.g. EndOfGame, once ClearHistoricalProfiles runs).
        _opGgButton.Visibility = _opGgDestination is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // Builds one link for the whole revealed roster rather than the local player alone.
    // _historicalProfiles.Entries carries a RevealedPlayerIdentity per revealed player
    // regardless of provider -- including PolicyDisabledHistoricalProfileProvider, which is
    // what ships when no Riot key is configured -- so this works with or without a key.
    private Uri? TryBuildRosterLink()
    {
        if (_historicalProfiles is null || _historicalProfiles.Entries.Count == 0)
        {
            return null;
        }

        var identities = _historicalProfiles.Entries
            .Select(entry => entry.Identity)
            .ToArray();
        return OpGgProfileLinkBuilder.TryBuildMultiSearch(identities, out var action)
            ? action.Destination
            : null;
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
                CurrentDipSize(),
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

            element = GetTreeParent(element);
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

            element = GetTreeParent(element);
        }

        return false;
    }

    // Hit-testing over rendered label text can return an inline ContentElement
    // (e.g. Run) that is not a Visual/Visual3D, which VisualTreeHelper rejects.
    // Walk the logical tree for those nodes to get back to the visual tree.
    private static DependencyObject? GetTreeParent(DependencyObject element) =>
        element is Visual or Visual3D
            ? VisualTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);

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

    /// <summary>
    /// Expanded sizes its height to content, which leaves Height as NaN. Math.Max
    /// propagates NaN, so reading the two together without this would hand the
    /// placement clamp a NaN size and stop it keeping the window on screen.
    /// </summary>
    private DipSize CurrentDipSize() => new(
        double.IsNaN(Width) ? ActualWidth : Math.Max(ActualWidth, Width),
        double.IsNaN(Height) ? ActualHeight : Math.Max(ActualHeight, Height));

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
        TextBlock Score,
        TextBlock Meta,
        TextBlock Rank);

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
