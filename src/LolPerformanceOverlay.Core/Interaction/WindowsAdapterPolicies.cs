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
