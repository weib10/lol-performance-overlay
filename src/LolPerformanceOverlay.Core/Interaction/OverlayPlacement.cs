namespace LolPerformanceOverlay.Core.Interaction;

public readonly record struct DipSize(double Width, double Height)
{
    public bool IsValid =>
        double.IsFinite(Width) &&
        double.IsFinite(Height) &&
        Width > 0 &&
        Height > 0;
}

public readonly record struct PixelPoint(int X, int Y);

public readonly record struct OverlayPlacementResult(
    PixelPoint Position,
    string DisplayId,
    bool WasAdjusted);

/// <summary>
/// Keeps the overlay's top-left corner inside a monitor's work area. Everything here is in
/// physical pixels, because under PerMonitorV2 WPF's Left/Top are converted with whatever DPI
/// the window happens to have at that moment and go stale after a cross-DPI move; physical
/// pixels are the only coordinates that mean the same thing on every monitor. The window's
/// size is given in DIPs and converted with the DPI of the monitor it is being placed on,
/// because that is the size Windows gives it once it lands there.
/// </summary>
public static class OverlayPlacement
{
    /// <summary>Anything further than this from the origin is treated as a damaged value.</summary>
    public const int MaximumCoordinateMagnitude = 1_000_000;

    public const double DefaultMarginDips = 10;

    // Where the Dot first appears when nothing has been saved yet, in DIPs from the primary
    // work area's right and top edges. Kept from the WPF Left/Top default this replaces.
    private const double FirstRunRightOffsetDips = 58;
    private const double FirstRunTopOffsetDips = 96;

    /// <summary>
    /// Clamps <paramref name="currentBounds"/> into one work area. With a
    /// <paramref name="preferredDisplayId"/> that still exists, that monitor is used; otherwise
    /// the monitor sharing the most area with <paramref name="currentBounds"/> (the rule Windows
    /// uses to pick a window's DPI), or the nearest work area when it touches none.
    /// </summary>
    public static OverlayPlacementResult Clamp(
        PixelRect currentBounds,
        DipSize contentSize,
        IReadOnlyList<PhysicalDisplayWorkArea> displays,
        string? preferredDisplayId = null,
        double marginDips = DefaultMarginDips)
    {
        Validate(contentSize, displays, marginDips);
        if (!IsSane(currentBounds))
        {
            var primary = Primary(displays);
            return new OverlayPlacementResult(
                ClampInto(primary, contentSize, desired: null, marginDips),
                primary.Id,
                WasAdjusted: true);
        }

        var selected = Find(displays, preferredDisplayId) ?? SelectByArea(currentBounds, displays);
        var position = ClampInto(selected, contentSize, new PixelPoint(currentBounds.X, currentBounds.Y), marginDips);
        return new OverlayPlacementResult(
            position,
            selected.Id,
            position != new PixelPoint(currentBounds.X, currentBounds.Y));
    }

    /// <summary>"Reset overlay position": the primary work area's top-right corner.</summary>
    public static PixelPoint Reset(
        DipSize contentSize,
        IReadOnlyList<PhysicalDisplayWorkArea> displays,
        double marginDips = DefaultMarginDips)
    {
        Validate(contentSize, displays, marginDips);
        return ClampInto(Primary(displays), contentSize, desired: null, marginDips);
    }

    /// <summary>Where the overlay appears on a first launch, before anything has been saved.</summary>
    public static PixelPoint FirstRun(IReadOnlyList<PhysicalDisplayWorkArea> displays)
    {
        ValidateDisplays(displays);
        var primary = Primary(displays);
        return new PixelPoint(
            primary.WorkArea.X + primary.WorkArea.Width - ToPixels(FirstRunRightOffsetDips, primary.DpiX),
            primary.WorkArea.Y + ToPixels(FirstRunTopOffsetDips, primary.DpiY));
    }

    /// <summary>
    /// Converts a position saved by the system-DPI-aware versions (WPF DIPs) to physical pixels.
    /// A system-aware process sees every monitor scaled about the global origin by
    /// system DPI / monitor DPI, and WPF then divides by the system DPI, so an old saved value is
    /// physical × 96 / that monitor's DPI whatever the system DPI was. Measured on a
    /// 150 % + 100 % desktop (2026-09-30); a monitor layout this does not cover falls back to the
    /// primary monitor's DPI and is then pulled back on screen by <see cref="Clamp"/>.
    /// </summary>
    public static PixelPoint FromLegacyDips(DipPoint legacy, IReadOnlyList<PhysicalDisplayWorkArea> displays)
    {
        ValidateDisplays(displays);
        var chosen = Primary(displays);
        foreach (var display in displays)
        {
            var scaleX = Scale(display.DpiX);
            var scaleY = Scale(display.DpiY);
            var bounds = display.MonitorBounds;
            if (legacy.X >= bounds.X / scaleX &&
                legacy.X < (bounds.X + (double)bounds.Width) / scaleX &&
                legacy.Y >= bounds.Y / scaleY &&
                legacy.Y < (bounds.Y + (double)bounds.Height) / scaleY)
            {
                chosen = display;
                break;
            }
        }

        return new PixelPoint(
            ClampCoordinate(Math.Round(legacy.X * Scale(chosen.DpiX), MidpointRounding.AwayFromZero)),
            ClampCoordinate(Math.Round(legacy.Y * Scale(chosen.DpiY), MidpointRounding.AwayFromZero)));
    }

    /// <summary>
    /// The position to start from: the saved physical position, else a converted legacy
    /// position, else <see cref="FirstRun"/>. Out-of-range or non-finite values count as absent.
    /// </summary>
    public static PixelPoint ResolveStartup(
        double? positionX,
        double? positionY,
        double? legacyLeft,
        double? legacyTop,
        IReadOnlyList<PhysicalDisplayWorkArea> displays)
    {
        ValidateDisplays(displays);
        if (TryCoordinate(positionX, out var x) && TryCoordinate(positionY, out var y))
        {
            return new PixelPoint(x, y);
        }

        if (IsSaneCoordinate(legacyLeft) && IsSaneCoordinate(legacyTop))
        {
            return FromLegacyDips(new DipPoint(legacyLeft!.Value, legacyTop!.Value), displays);
        }

        return FirstRun(displays);
    }

    /// <summary>
    /// Picks the monitor a saved position belongs to. At startup the window still has the size
    /// Windows gave it on the primary monitor, so its current rectangle cannot be used. Each
    /// candidate is instead measured at its own DPI and ranked by the share of that rectangle it
    /// covers. Absolute area would not work: a higher-DPI monitor's rectangle is bigger and can
    /// win while covering only part of it. A position saved by this version always lies fully
    /// inside its monitor, so that monitor covers 100 % and always wins.
    /// </summary>
    public static string SelectDisplayForSavedPosition(
        PixelPoint saved,
        DipSize contentSize,
        IReadOnlyList<PhysicalDisplayWorkArea> displays)
    {
        Validate(contentSize, displays, DefaultMarginDips);
        PhysicalDisplayWorkArea? best = null;
        var bestRatio = 0d;
        foreach (var display in displays)
        {
            var width = ToPixelSize(contentSize.Width, display.DpiX);
            var height = ToPixelSize(contentSize.Height, display.DpiY);
            var ratio = Intersection(saved.X, saved.Y, width, height, display.MonitorBounds) / ((double)width * height);
            if (ratio > bestRatio || (ratio > 0 && ratio == bestRatio && display.IsPrimary))
            {
                best = display;
                bestRatio = ratio;
            }
        }

        return (best ?? Nearest(saved.X, saved.Y, displays)).Id;
    }

    internal static PhysicalDisplayWorkArea? Find(IReadOnlyList<PhysicalDisplayWorkArea> displays, string? id) =>
        id is null ? null : displays.FirstOrDefault(display => string.Equals(display.Id, id, StringComparison.Ordinal));

    internal static PhysicalDisplayWorkArea Primary(IReadOnlyList<PhysicalDisplayWorkArea> displays) =>
        displays.FirstOrDefault(display => display.IsPrimary) ?? displays[0];

    /// <summary>The monitor sharing the most area with a rectangle; null when it touches none.</summary>
    internal static PhysicalDisplayWorkArea? MostArea(
        long x,
        long y,
        long width,
        long height,
        IReadOnlyList<PhysicalDisplayWorkArea> displays)
    {
        PhysicalDisplayWorkArea? best = null;
        var bestArea = 0L;
        foreach (var display in displays)
        {
            var area = Intersection(x, y, width, height, display.MonitorBounds);
            if (area > bestArea || (area > 0 && area == bestArea && display.IsPrimary))
            {
                best = display;
                bestArea = area;
            }
        }

        return best;
    }

    /// <summary>Clamps a window of the given DIP size, placed on <paramref name="display"/>.</summary>
    internal static PixelPoint ClampInto(
        PhysicalDisplayWorkArea display,
        DipSize contentSize,
        PixelPoint? desired,
        double marginDips) =>
        ClampInto(
            display,
            ToPixelSize(contentSize.Width, display.DpiX),
            ToPixelSize(contentSize.Height, display.DpiY),
            desired,
            marginDips);

    internal static PixelPoint ClampInto(
        PhysicalDisplayWorkArea display,
        long width,
        long height,
        PixelPoint? desired,
        double marginDips)
    {
        var area = display.WorkArea;
        var marginX = (long)ToPixels(marginDips, display.DpiX);
        var marginY = (long)ToPixels(marginDips, display.DpiY);
        var horizontalMargin = width + 2 * marginX <= area.Width ? marginX : 0;
        var verticalMargin = height + 2 * marginY <= area.Height ? marginY : 0;
        var minimumLeft = area.X + horizontalMargin;
        var minimumTop = area.Y + verticalMargin;
        var maximumLeft = Math.Max(minimumLeft, area.X + (long)area.Width - width - horizontalMargin);
        var maximumTop = Math.Max(minimumTop, area.Y + (long)area.Height - height - verticalMargin);
        if (desired is not { } point)
        {
            return new PixelPoint((int)maximumLeft, (int)minimumTop);
        }

        return new PixelPoint(
            (int)Math.Clamp(point.X, minimumLeft, maximumLeft),
            (int)Math.Clamp(point.Y, minimumTop, maximumTop));
    }

    internal static long Intersection(long x, long y, long width, long height, PixelRect bounds)
    {
        var overlapWidth = Math.Min(x + width, bounds.X + (long)bounds.Width) - Math.Max(x, bounds.X);
        var overlapHeight = Math.Min(y + height, bounds.Y + (long)bounds.Height) - Math.Max(y, bounds.Y);
        return overlapWidth > 0 && overlapHeight > 0 ? overlapWidth * overlapHeight : 0;
    }

    internal static long ToPixelSize(double dips, uint dpi) => (long)Math.Ceiling(dips * Scale(dpi));

    internal static double Scale(uint dpi) => dpi > 0 ? dpi / 96d : 1d;

    internal static void ValidateDisplays(IReadOnlyList<PhysicalDisplayWorkArea> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        if (displays.Count == 0)
        {
            throw new ArgumentException("At least one display is required.", nameof(displays));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var display in displays)
        {
            if (string.IsNullOrWhiteSpace(display.Id) ||
                !ids.Add(display.Id) ||
                !display.MonitorBounds.IsValid ||
                !display.WorkArea.IsValid)
            {
                throw new ArgumentException("Every display must have a unique ID and valid bounds.", nameof(displays));
            }
        }
    }

    private static void Validate(DipSize contentSize, IReadOnlyList<PhysicalDisplayWorkArea> displays, double marginDips)
    {
        ValidateDisplays(displays);
        if (!contentSize.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(contentSize));
        }

        if (!double.IsFinite(marginDips) || marginDips < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(marginDips));
        }
    }

    private static PhysicalDisplayWorkArea SelectByArea(PixelRect bounds, IReadOnlyList<PhysicalDisplayWorkArea> displays) =>
        MostArea(bounds.X, bounds.Y, bounds.Width, bounds.Height, displays) ??
        Nearest(bounds.X + bounds.Width / 2L, bounds.Y + bounds.Height / 2L, displays);

    private static PhysicalDisplayWorkArea Nearest(long x, long y, IReadOnlyList<PhysicalDisplayWorkArea> displays)
    {
        PhysicalDisplayWorkArea? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var display in displays)
        {
            var area = display.WorkArea;
            double deltaX = x < area.X ? area.X - x : x > area.X + (long)area.Width ? x - (area.X + (long)area.Width) : 0;
            double deltaY = y < area.Y ? area.Y - y : y > area.Y + (long)area.Height ? y - (area.Y + (long)area.Height) : 0;
            var distance = deltaX * deltaX + deltaY * deltaY;
            if (distance < bestDistance || (distance == bestDistance && display.IsPrimary))
            {
                best = display;
                bestDistance = distance;
            }
        }

        return best!;
    }

    private static bool IsSane(PixelRect bounds) =>
        bounds.Width > 0 &&
        bounds.Height > 0 &&
        Math.Abs((long)bounds.X) <= MaximumCoordinateMagnitude &&
        Math.Abs((long)bounds.Y) <= MaximumCoordinateMagnitude &&
        bounds.Width <= MaximumCoordinateMagnitude &&
        bounds.Height <= MaximumCoordinateMagnitude;

    private static bool IsSaneCoordinate(double? value) =>
        value is { } number && double.IsFinite(number) && Math.Abs(number) <= MaximumCoordinateMagnitude;

    private static bool TryCoordinate(double? value, out int coordinate)
    {
        coordinate = 0;
        if (!IsSaneCoordinate(value))
        {
            return false;
        }

        coordinate = (int)Math.Round(value!.Value, MidpointRounding.AwayFromZero);
        return true;
    }

    private static int ClampCoordinate(double value) =>
        (int)Math.Clamp(value, -MaximumCoordinateMagnitude, MaximumCoordinateMagnitude);

    private static int ToPixels(double dips, uint dpi) =>
        (int)Math.Round(dips * Scale(dpi), MidpointRounding.AwayFromZero);
}
