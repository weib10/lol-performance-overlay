namespace LolPerformanceOverlay.Core.Interaction;

/// <summary>
/// Single authority for the overlay's opacity range and default. SettingsStore.Load,
/// OverlayWindow.ApplySettings, and both live-preview surfaces (the Settings dialog's slider
/// and the right-click menu's slider) all clamp through here so the range and the default only
/// exist in one place instead of being repeated at every call site.
/// </summary>
public static class OverlayOpacityPolicy
{
    public const double Minimum = 0.35;
    public const double Maximum = 1.0;
    public const double Default = 0.92;

    public static double Clamp(double opacity) =>
        double.IsFinite(opacity) ? Math.Clamp(opacity, Minimum, Maximum) : Default;
}

/// <summary>
/// Drives live opacity preview while a slider is being dragged, shared by the Settings dialog
/// and the overlay's right-click context menu so "what does this position mean" has one answer
/// in both places. <see cref="PriorOpacity"/> is the (clamped) value in effect before the
/// control opened. <see cref="Preview"/> is called on every drag tick and returns the clamped
/// value the caller should apply to the overlay immediately, so the user sees the effect while
/// still dragging instead of only after releasing. <see cref="Cancel"/> discards whatever was
/// previewed and returns to <see cref="PriorOpacity"/>, so a dialog dismissed without saving
/// never leaves the overlay at a value the user never confirmed -- a live preview that cannot
/// be backed out of is worse than no preview at all.
/// </summary>
public sealed class OpacityPreviewSession
{
    public OpacityPreviewSession(double priorOpacity)
    {
        PriorOpacity = OverlayOpacityPolicy.Clamp(priorOpacity);
        CurrentOpacity = PriorOpacity;
    }

    public double PriorOpacity { get; }

    public double CurrentOpacity { get; private set; }

    public double Preview(double candidateOpacity)
    {
        CurrentOpacity = OverlayOpacityPolicy.Clamp(candidateOpacity);
        return CurrentOpacity;
    }

    public double Cancel()
    {
        CurrentOpacity = PriorOpacity;
        return CurrentOpacity;
    }
}

/// <summary>
/// Gates the overlay's right-click context menu. Locked position already makes the whole
/// window click-through at the Win32 level (see OverlayNativeStylePolicy.WithPositionLock and
/// OverlayWindow's WM_NCHITTEST handling), so WPF never even dispatches ContextMenuOpening in
/// that state -- that is correct behaviour, not a bug, because a locked overlay is supposed to
/// stop receiving mouse input entirely, and a right-click reopening the menu would quietly
/// defeat the lock. The window's ContextMenuOpening handler still calls this explicitly as
/// defence in depth rather than relying only on that emergent side effect, and this is the one
/// place the rule is written down and unit-tested.
/// </summary>
public static class OverlayContextMenuPolicy
{
    /// <summary>
    /// True unless position is locked. <paramref name="mode"/> is accepted because the call
    /// site always has it in hand, but it never gates the decision on its own: the menu is
    /// attached to the window itself, not to any one mode's built content (see
    /// OverlayWindow.BuildModeVisual), and it opens at the pointer rather than inside the
    /// overlay's own bounds, so Dot being small is not a reason to refuse it either.
    /// </summary>
    public static bool CanOpen(OverlayMode mode, bool positionLocked) => !positionLocked;
}
