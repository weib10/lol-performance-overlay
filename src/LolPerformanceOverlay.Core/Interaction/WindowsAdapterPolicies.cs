namespace LolPerformanceOverlay.Core.Interaction;

public static class OverlayNativeStylePolicy
{
    public const int TransparentInputStyle = 0x00000020;

    public static int WithPositionLock(int extendedStyle, bool positionLocked) =>
        positionLocked
            ? extendedStyle | TransparentInputStyle
            : extendedStyle & ~TransparentInputStyle;
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);

    public int Bottom => checked(Y + Height);

    public bool IsValid => Width > 0 && Height > 0;
}

public sealed record PhysicalDisplayWorkArea(
    string Id,
    PixelRect MonitorBounds,
    PixelRect WorkArea,
    uint DpiX,
    uint DpiY,
    bool IsPrimary = false);

/// <summary>
/// Builds one continuous WPF-DIP desktop from all physical monitors at once. Monitor origins
/// cannot be divided independently by their DPI: doing so overlaps right-side monitors and leaves
/// false gaps on left/upper monitors. Adjacent physical edges are therefore anchored first, then
/// each monitor's work-area insets and dimensions are scaled by that monitor's DPI.
/// </summary>
public static class DisplayTopologyConverter
{
    public static IReadOnlyList<DisplayWorkArea> ToDips(
        IReadOnlyList<PhysicalDisplayWorkArea> physicalDisplays)
    {
        ArgumentNullException.ThrowIfNull(physicalDisplays);
        if (physicalDisplays.Count == 0)
        {
            throw new ArgumentException("At least one physical display is required.", nameof(physicalDisplays));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var display in physicalDisplays)
        {
            if (string.IsNullOrWhiteSpace(display.Id) ||
                !ids.Add(display.Id) ||
                !display.MonitorBounds.IsValid ||
                !display.WorkArea.IsValid)
            {
                throw new ArgumentException("Every physical display must have a unique ID and valid bounds.", nameof(physicalDisplays));
            }
        }

        var primaryIndex = -1;
        for (var index = 0; index < physicalDisplays.Count; index++)
        {
            if (physicalDisplays[index].IsPrimary)
            {
                primaryIndex = index;
                break;
            }
        }

        primaryIndex = primaryIndex < 0 ? 0 : primaryIndex;
        var monitorDips = new DipRect?[physicalDisplays.Count];
        var primary = physicalDisplays[primaryIndex];
        monitorDips[primaryIndex] = new DipRect(
            primary.MonitorBounds.X / Scale(primary.DpiX),
            primary.MonitorBounds.Y / Scale(primary.DpiY),
            primary.MonitorBounds.Width / Scale(primary.DpiX),
            primary.MonitorBounds.Height / Scale(primary.DpiY));

        var queue = new Queue<int>();
        queue.Enqueue(primaryIndex);
        while (queue.Count > 0)
        {
            var anchorIndex = queue.Dequeue();
            for (var candidateIndex = 0; candidateIndex < physicalDisplays.Count; candidateIndex++)
            {
                if (monitorDips[candidateIndex] is not null ||
                    !TryPlaceAdjacent(
                        physicalDisplays[anchorIndex],
                        monitorDips[anchorIndex]!.Value,
                        physicalDisplays[candidateIndex],
                        out var candidateDips))
                {
                    continue;
                }

                monitorDips[candidateIndex] = candidateDips;
                queue.Enqueue(candidateIndex);
            }
        }

        var primaryDips = monitorDips[primaryIndex]!.Value;
        for (var index = 0; index < physicalDisplays.Count; index++)
        {
            if (monitorDips[index] is not null)
            {
                continue;
            }

            var display = physicalDisplays[index];
            monitorDips[index] = new DipRect(
                primaryDips.Left +
                (display.MonitorBounds.X - primary.MonitorBounds.X) / Scale(primary.DpiX),
                primaryDips.Top +
                (display.MonitorBounds.Y - primary.MonitorBounds.Y) / Scale(primary.DpiY),
                display.MonitorBounds.Width / Scale(display.DpiX),
                display.MonitorBounds.Height / Scale(display.DpiY));
        }

        return physicalDisplays.Select((display, index) =>
        {
            var monitor = monitorDips[index]!.Value;
            var scaleX = Scale(display.DpiX);
            var scaleY = Scale(display.DpiY);
            return new DisplayWorkArea(
                display.Id,
                new DipRect(
                    monitor.Left + (display.WorkArea.X - display.MonitorBounds.X) / scaleX,
                    monitor.Top + (display.WorkArea.Y - display.MonitorBounds.Y) / scaleY,
                    display.WorkArea.Width / scaleX,
                    display.WorkArea.Height / scaleY),
                display.IsPrimary);
        }).ToArray();
    }

    private static bool TryPlaceAdjacent(
        PhysicalDisplayWorkArea anchor,
        DipRect anchorDips,
        PhysicalDisplayWorkArea candidate,
        out DipRect candidateDips)
    {
        var anchorBounds = anchor.MonitorBounds;
        var candidateBounds = candidate.MonitorBounds;
        var width = candidateBounds.Width / Scale(candidate.DpiX);
        var height = candidateBounds.Height / Scale(candidate.DpiY);

        if (candidateBounds == anchorBounds)
        {
            candidateDips = new DipRect(anchorDips.Left, anchorDips.Top, width, height);
            return true;
        }

        if (candidateBounds.Right == anchorBounds.X && candidateBounds.Bottom == anchorBounds.Y)
        {
            candidateDips = new DipRect(anchorDips.Left - width, anchorDips.Top - height, width, height);
            return true;
        }

        if (candidateBounds.X == anchorBounds.Right && candidateBounds.Bottom == anchorBounds.Y)
        {
            candidateDips = new DipRect(anchorDips.Right, anchorDips.Top - height, width, height);
            return true;
        }

        if (candidateBounds.Right == anchorBounds.X && candidateBounds.Y == anchorBounds.Bottom)
        {
            candidateDips = new DipRect(anchorDips.Left - width, anchorDips.Bottom, width, height);
            return true;
        }

        if (candidateBounds.X == anchorBounds.Right && candidateBounds.Y == anchorBounds.Bottom)
        {
            candidateDips = new DipRect(anchorDips.Right, anchorDips.Bottom, width, height);
            return true;
        }

        if (candidateBounds.X == anchorBounds.Right && VerticalOverlap(anchorBounds, candidateBounds))
        {
            candidateDips = new DipRect(
                anchorDips.Right,
                anchorDips.Top + (candidateBounds.Y - anchorBounds.Y) / Scale(anchor.DpiY),
                width,
                height);
            return true;
        }

        if (candidateBounds.Right == anchorBounds.X && VerticalOverlap(anchorBounds, candidateBounds))
        {
            candidateDips = new DipRect(
                anchorDips.Left - width,
                anchorDips.Top + (candidateBounds.Y - anchorBounds.Y) / Scale(anchor.DpiY),
                width,
                height);
            return true;
        }

        if (candidateBounds.Y == anchorBounds.Bottom && HorizontalOverlap(anchorBounds, candidateBounds))
        {
            candidateDips = new DipRect(
                anchorDips.Left + (candidateBounds.X - anchorBounds.X) / Scale(anchor.DpiX),
                anchorDips.Bottom,
                width,
                height);
            return true;
        }

        if (candidateBounds.Bottom == anchorBounds.Y && HorizontalOverlap(anchorBounds, candidateBounds))
        {
            candidateDips = new DipRect(
                anchorDips.Left + (candidateBounds.X - anchorBounds.X) / Scale(anchor.DpiX),
                anchorDips.Top - height,
                width,
                height);
            return true;
        }

        candidateDips = default;
        return false;
    }

    private static bool VerticalOverlap(PixelRect left, PixelRect right) =>
        Math.Min(left.Bottom, right.Bottom) > Math.Max(left.Y, right.Y);

    private static bool HorizontalOverlap(PixelRect top, PixelRect bottom) =>
        Math.Min(top.Right, bottom.Right) > Math.Max(top.X, bottom.X);

    private static double Scale(uint dpi) => dpi > 0 ? dpi / 96d : 1d;
}
