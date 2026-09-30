namespace LolPerformanceOverlay.Core.Interaction;

/// <summary>
/// A drag in physical pixels: the cursor where the button went down, and the window's top-left
/// when the drag threshold was crossed. The offset is kept in pixels, not DIPs, so a drag that
/// crosses monitors of different DPI never rescales it.
/// </summary>
public readonly record struct PhysicalDragAnchor(PixelPoint CursorOrigin, PixelPoint WindowOrigin)
{
    public PixelPoint WindowPositionFor(PixelPoint cursor) => new(
        (int)Math.Clamp((long)WindowOrigin.X + cursor.X - CursorOrigin.X, int.MinValue, int.MaxValue),
        (int)Math.Clamp((long)WindowOrigin.Y + cursor.Y - CursorOrigin.Y, int.MinValue, int.MaxValue));

    /// <summary>Re-anchors after the window was put somewhere else mid-drag, so the drag carries on from there.</summary>
    public PhysicalDragAnchor RebasedTo(PixelPoint cursor, PixelPoint windowTopLeft) =>
        new(cursor, windowTopLeft);
}

public enum DpiTransitionMode
{
    Dragging,
    Anchored
}

/// <summary>
/// Chooses the rectangle to hand back to Windows for a WM_DPICHANGED. Windows re-checks which
/// monitor holds most of the window as soon as that rectangle is applied, inside the same
/// SetWindowPos, and sends another WM_DPICHANGED if the answer changed, with nothing to stop
/// the loop. So whatever this returns must, at the new size, leave most of the window on the
/// monitor whose DPI it now has, or end the chain by returning to home.
/// </summary>
public static class DpiTransition
{
    public static PixelRect Resolve(
        PixelRect current,
        PixelRect suggested,
        uint newDpi,
        IReadOnlyList<PhysicalDisplayWorkArea> displays,
        DpiTransitionMode mode,
        string? homeDisplayId,
        PixelPoint chainOrigin,
        DipSize contentSize,
        double marginDips,
        bool forceInsideTarget,
        PixelPoint? dragCursor = null)
    {
        OverlayPlacement.ValidateDisplays(displays);
        var target = OverlayPlacement.MostArea(current.X, current.Y, current.Width, current.Height, displays);
        if (target is null || !suggested.IsValid)
        {
            // Windows does not send this while the window touches no monitor; keep its suggestion.
            return suggested;
        }

        if (forceInsideTarget)
        {
            return InsideMonitor(target, suggested.Width, suggested.Height, new PixelPoint(suggested.X, suggested.Y));
        }

        return mode == DpiTransitionMode.Dragging
            ? WhileDragging(current, suggested, target, displays, dragCursor)
            : Anchored(suggested, newDpi, target, displays, homeDisplayId, chainOrigin, contentSize, marginDips);
    }

    /// <summary>
    /// First choice: scale about the cursor, so the point the user is holding stays under it.
    /// That only stands if the new-size window still belongs to the new monitor; holding the
    /// trailing end, it would shrink back onto the old one and the DPI would flip straight back.
    /// Then the window's centre is kept instead: a window crossing one edge has most of its area
    /// on the side its centre is on. Keeping the top-left (Windows' own suggestion) flips back
    /// whenever the higher-DPI monitor is on the left or above.
    /// </summary>
    private static PixelRect WhileDragging(
        PixelRect current,
        PixelRect suggested,
        PhysicalDisplayWorkArea target,
        IReadOnlyList<PhysicalDisplayWorkArea> displays,
        PixelPoint? dragCursor)
    {
        if (dragCursor is { } cursor &&
            cursor.X >= current.X && cursor.X <= (long)current.X + current.Width &&
            cursor.Y >= current.Y && cursor.Y <= (long)current.Y + current.Height)
        {
            var aboutCursor = new PixelRect(
                cursor.X - (int)Math.Round((cursor.X - current.X) * (double)suggested.Width / current.Width),
                cursor.Y - (int)Math.Round((cursor.Y - current.Y) * (double)suggested.Height / current.Height),
                suggested.Width,
                suggested.Height);
            if (ReferenceEquals(Owner(aboutCursor, displays), target))
            {
                return aboutCursor;
            }
        }

        var centred = new PixelRect(
            current.X + (int)Math.Floor((current.Width - (double)suggested.Width) / 2),
            current.Y + (int)Math.Floor((current.Height - (double)suggested.Height) / 2),
            suggested.Width,
            suggested.Height);
        if (ReferenceEquals(Owner(centred, displays), target))
        {
            return centred;
        }

        // Monitors that only partly overlap on the other axis can still give the old monitor the
        // larger share with the centre kept; push the window onto the new one instead.
        return InsideMonitor(target, centred.Width, centred.Height, new PixelPoint(centred.X, centred.Y));
    }

    /// <summary>
    /// Not dragging: the window grew or was moved into another monitor by something other than
    /// the user. Every message in the chain aims at P, the home-DPI window clamped into home from
    /// where the chain started, so Windows bounces back to home at most once and the chain ends
    /// exactly at P with home's margin.
    /// </summary>
    private static PixelRect Anchored(
        PixelRect suggested,
        uint newDpi,
        PhysicalDisplayWorkArea target,
        IReadOnlyList<PhysicalDisplayWorkArea> displays,
        string? homeDisplayId,
        PixelPoint chainOrigin,
        DipSize contentSize,
        double marginDips)
    {
        var home = OverlayPlacement.Find(displays, homeDisplayId) ?? target;
        var anchor = OverlayPlacement.ClampInto(home, contentSize, chainOrigin, marginDips);
        var atAnchor = new PixelRect(anchor.X, anchor.Y, suggested.Width, suggested.Height);
        if (newDpi == home.DpiX || ReferenceEquals(Owner(atAnchor, displays), home))
        {
            return atAnchor;
        }

        // At the new DPI's size the window at P would mostly sit on another monitor (for example a
        // home far smaller than the other monitor's scale). Pull that size into home for this one
        // step; the message that brings the DPI back to home's then lands on P.
        var inside = OverlayPlacement.ClampInto(home, suggested.Width, suggested.Height, anchor, marginDips);
        return new PixelRect(inside.X, inside.Y, suggested.Width, suggested.Height);
    }

    private static PhysicalDisplayWorkArea? Owner(PixelRect rect, IReadOnlyList<PhysicalDisplayWorkArea> displays) =>
        OverlayPlacement.MostArea(rect.X, rect.Y, rect.Width, rect.Height, displays);

    private static PixelRect InsideMonitor(PhysicalDisplayWorkArea monitor, int width, int height, PixelPoint desired)
    {
        var bounds = monitor.MonitorBounds;
        var maximumLeft = Math.Max(bounds.X, bounds.X + (long)bounds.Width - width);
        var maximumTop = Math.Max(bounds.Y, bounds.Y + (long)bounds.Height - height);
        return new PixelRect(
            (int)Math.Clamp(desired.X, bounds.X, maximumLeft),
            (int)Math.Clamp(desired.Y, bounds.Y, maximumTop),
            width,
            height);
    }
}
