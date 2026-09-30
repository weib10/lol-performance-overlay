namespace LolPerformanceOverlay.Core.Interaction;

/// <summary>What settings.json held for the overlay's position when the app started.</summary>
public readonly record struct StartupPosition(
    double? PositionX,
    double? PositionY,
    double? LegacyLeft,
    double? LegacyTop);

/// <summary>
/// A move the app makes on its own. When <see cref="Staging"/> is set the window goes there
/// first and then to <see cref="Final"/>; see <see cref="OverlayPlacementController"/>.
/// </summary>
public readonly record struct PlacementMove(PixelPoint? Staging, PixelPoint Final, uint TargetDpi);

public readonly record struct DpiChangeResult(PixelRect Rect, bool StartedChain);

/// <summary>
/// Owns every rule about where the overlay may be put, so the window adapter only reads and
/// writes Win32 and those rules can be tested here.
/// <list type="bullet">
/// <item>Home: the monitor the user last put the overlay on. It changes only at startup, on
/// reset and when a drag ends (or when home disappears); every other clamp keeps the overlay
/// on it, so a mode switch or a taller panel never hops to the neighbouring monitor.</item>
/// <item>Nothing but the drag itself moves the window while a drag is in progress.</item>
/// <item>Home is updated before a move is returned, so the WM_DPICHANGED that move triggers
/// already treats the destination as home.</item>
/// <item>A move onto a monitor of another DPI that would not by itself make Windows switch the
/// window's DPI (its old, larger size still mostly on the old monitor) goes through a staging
/// point fully inside the destination first.</item>
/// </list>
/// </summary>
public sealed class OverlayPlacementController
{
    private const int ForceAfterDpiChanges = 3;

    private readonly double _marginDips;
    private PixelPoint _cursorOrigin;
    private PixelPoint _lastDragCursor;
    private PixelPoint _windowTopLeft;
    private PhysicalDragAnchor? _anchor;
    private int _chainLength;
    private PixelPoint _chainOrigin;
    private uint _lastTargetDpi;

    public OverlayPlacementController(double marginDips = OverlayPlacement.DefaultMarginDips)
    {
        if (!double.IsFinite(marginDips) || marginDips < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(marginDips));
        }

        _marginDips = marginDips;
    }

    public string? HomeDisplayId { get; private set; }

    public bool IsDragging { get; private set; }

    /// <summary>Every WM_DPICHANGED handled; shows the other two counters were read after real DPI changes.</summary>
    public int DpiChangeCount { get; private set; }

    /// <summary>Chains that reached the third WM_DPICHANGED and were forced onto the target monitor. Expected 0.</summary>
    public int ForcedDpiPlacementCount { get; private set; }

    /// <summary>Moves after which the window did not have the destination's DPI. Expected 0.</summary>
    public int UntriggeredStagingCount { get; private set; }

    /// <summary>
    /// Whether the position <see cref="Startup"/> chose should be written back to settings: only
    /// a legacy DIP position that landed where it converts to. A position pulled back on screen
    /// is not written, so an overlay saved on a monitor that is unplugged today returns to it
    /// when the monitor is back.
    /// </summary>
    public bool StartupMigratesLegacyPosition { get; private set; }

    public PlacementMove Startup(
        StartupPosition saved,
        PixelRect current,
        uint currentWindowDpi,
        DipSize contentSize,
        IReadOnlyList<PhysicalDisplayWorkArea> displays)
    {
        var start = OverlayPlacement.ResolveStartup(saved.PositionX, saved.PositionY, saved.LegacyLeft, saved.LegacyTop, displays);
        var home = OverlayPlacement.SelectDisplayForSavedPosition(start, contentSize, displays);
        var result = OverlayPlacement.Clamp(
            new PixelRect(start.X, start.Y, current.Width, current.Height),
            contentSize,
            displays,
            home,
            _marginDips);
        HomeDisplayId = result.DisplayId;
        StartupMigratesLegacyPosition = !result.WasAdjusted &&
            !OverlayPlacement.HasCoordinates(saved.PositionX, saved.PositionY) &&
            OverlayPlacement.HasCoordinates(saved.LegacyLeft, saved.LegacyTop);
        return Move(current, currentWindowDpi, result.Position, result.DisplayId, displays);
    }

    public PlacementMove Reset(
        PixelRect current,
        uint currentWindowDpi,
        DipSize contentSize,
        IReadOnlyList<PhysicalDisplayWorkArea> displays)
    {
        var position = OverlayPlacement.Reset(contentSize, displays, _marginDips);
        var primary = OverlayPlacement.Primary(displays);
        HomeDisplayId = primary.Id;
        return Move(current, currentWindowDpi, position, primary.Id, displays);
    }

    /// <summary>
    /// Keeps the window inside home. Null means nothing to do: the position is right and the
    /// window already has home's DPI. Always null while dragging.
    /// </summary>
    public PlacementMove? Clamp(
        PixelRect current,
        uint currentWindowDpi,
        DipSize contentSize,
        IReadOnlyList<PhysicalDisplayWorkArea> displays)
    {
        if (IsDragging)
        {
            return null;
        }

        var result = OverlayPlacement.Clamp(current, contentSize, displays, HomeDisplayId, _marginDips);
        HomeDisplayId = result.DisplayId;
        return MoveIfNeeded(current, currentWindowDpi, result, displays);
    }

    /// <summary>
    /// Where to put the window before it is resized to <paramref name="newContentSize"/>, so it
    /// grows inside home and never changes DPI on the way. Null while dragging.
    /// </summary>
    public PlacementMove? PrePosition(
        PixelRect current,
        uint currentWindowDpi,
        DipSize newContentSize,
        IReadOnlyList<PhysicalDisplayWorkArea> displays) =>
        Clamp(current, currentWindowDpi, newContentSize, displays);

    /// <summary>Called on pointer-down, after the state machine's actions have been carried out.</summary>
    public void BeginGesture(PixelPoint cursor, PixelPoint windowTopLeft)
    {
        _cursorOrigin = cursor;
        _lastDragCursor = cursor;
        _windowTopLeft = windowTopLeft;
        _anchor = null;
    }

    /// <summary>
    /// Called when the drag threshold is crossed. The window may have moved since the press (an
    /// automatic mode switch, a champ-select rebuild), so the drag starts from where it is now,
    /// as tracked here, while the cursor offset still counts from the press.
    /// </summary>
    public void BeginDrag()
    {
        IsDragging = true;
        _anchor = new PhysicalDragAnchor(_cursorOrigin, _windowTopLeft);
    }

    public PixelPoint DragTo(PixelPoint cursor)
    {
        _lastDragCursor = cursor;
        if (_anchor is { } anchor)
        {
            _windowTopLeft = anchor.WindowPositionFor(cursor);
        }

        return _windowTopLeft;
    }

    /// <summary>
    /// The drag ended or was cancelled. Home becomes the monitor holding most of the window,
    /// even when the window needs no adjustment there.
    /// </summary>
    public PlacementMove? EndGesture(
        PixelRect current,
        uint currentWindowDpi,
        DipSize contentSize,
        IReadOnlyList<PhysicalDisplayWorkArea> displays)
    {
        IsDragging = false;
        _anchor = null;
        var result = OverlayPlacement.Clamp(current, contentSize, displays, preferredDisplayId: null, _marginDips);
        HomeDisplayId = result.DisplayId;
        return MoveIfNeeded(current, currentWindowDpi, result, displays);
    }

    public DpiChangeResult OnDpiChanged(
        PixelRect current,
        PixelRect suggested,
        uint newDpi,
        DipSize contentSize,
        IReadOnlyList<PhysicalDisplayWorkArea> displays)
    {
        DpiChangeCount++;
        var startedChain = _chainLength == 0;
        if (startedChain)
        {
            _chainOrigin = new PixelPoint(current.X, current.Y);
        }

        _chainLength++;
        var force = _chainLength >= ForceAfterDpiChanges;
        if (force)
        {
            ForcedDpiPlacementCount++;
        }

        var rect = DpiTransition.Resolve(
            current,
            suggested,
            newDpi,
            displays,
            IsDragging ? DpiTransitionMode.Dragging : DpiTransitionMode.Anchored,
            HomeDisplayId,
            _chainOrigin,
            contentSize,
            _marginDips,
            force,
            IsDragging ? _lastDragCursor : null);
        _windowTopLeft = new PixelPoint(rect.X, rect.Y);
        if (IsDragging && _anchor is { } anchor)
        {
            // The last DragTo's cursor, not the cursor now: if a resize started this chain the
            // cursor may have moved since, and the next DragTo must carry on from this rect.
            _anchor = anchor.RebasedTo(_lastDragCursor, _windowTopLeft);
        }

        return new DpiChangeResult(rect, startedChain);
    }

    /// <summary>
    /// Ends a chain of WM_DPICHANGED. The window calls this before each of its own moves and
    /// resizes, and once after the first message of a chain (the chain is synchronous, so that
    /// queued call only runs after it).
    /// </summary>
    public void EndDpiChain() => _chainLength = 0;

    /// <summary>Called after a <see cref="PlacementMove"/> has been applied.</summary>
    public void CompleteMove(uint windowDpiAfter, PixelPoint windowTopLeftAfter)
    {
        _windowTopLeft = windowTopLeftAfter;
        if (windowDpiAfter != _lastTargetDpi)
        {
            UntriggeredStagingCount++;
        }
    }

    private PlacementMove? MoveIfNeeded(
        PixelRect current,
        uint currentWindowDpi,
        OverlayPlacementResult result,
        IReadOnlyList<PhysicalDisplayWorkArea> displays)
    {
        var target = OverlayPlacement.Find(displays, result.DisplayId)!;
        return !result.WasAdjusted && target.DpiX == currentWindowDpi
            ? null
            : Move(current, currentWindowDpi, result.Position, result.DisplayId, displays);
    }

    private PlacementMove Move(
        PixelRect current,
        uint currentWindowDpi,
        PixelPoint final,
        string targetId,
        IReadOnlyList<PhysicalDisplayWorkArea> displays)
    {
        var target = OverlayPlacement.Find(displays, targetId)!;
        _lastTargetDpi = target.DpiX;
        PixelPoint? staging = null;
        if (target.DpiX != currentWindowDpi)
        {
            // Windows switches the DPI only if the window, at its current size, ends up mostly on
            // a monitor of another DPI. If moving straight to Final would leave it mostly on a
            // monitor of the current DPI, go through a point fully inside the target first.
            var owner = OverlayPlacement.MostArea(final.X, final.Y, current.Width, current.Height, displays);
            if (owner is null || owner.DpiX == currentWindowDpi)
            {
                var centre = new PixelPoint(
                    (int)(target.WorkArea.X + ((long)target.WorkArea.Width - current.Width) / 2),
                    (int)(target.WorkArea.Y + ((long)target.WorkArea.Height - current.Height) / 2));
                staging = OverlayPlacement.ClampInto(target, current.Width, current.Height, centre, marginDips: 0);
            }
        }

        return new PlacementMove(staging, final, target.DpiX);
    }
}
