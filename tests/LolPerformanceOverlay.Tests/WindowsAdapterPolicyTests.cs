using LolPerformanceOverlay.Core.Interaction;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class WindowsAdapterPolicyTests
{
    [Fact]
    public void PositionLockAddsAndUnlockRemovesWholeWindowTransparentInputStyle()
    {
        const int baseStyle = 0x08000080;

        var locked = OverlayNativeStylePolicy.WithPositionLock(baseStyle, positionLocked: true);
        var unlocked = OverlayNativeStylePolicy.WithPositionLock(locked, positionLocked: false);

        Assert.NotEqual(0, locked & OverlayNativeStylePolicy.TransparentInputStyle);
        Assert.Equal(baseStyle, unlocked);
    }

    [Fact]
    public void MixedDpiDisplaysOnBothSidesFormOneContinuousDesktop()
    {
        var displays = DisplayTopologyConverter.ToDips(
        [
            Display("left", new PixelRect(-2560, 0, 2560, 1440), 144),
            Display("primary", new PixelRect(0, 0, 1920, 1080), 96, isPrimary: true),
            Display("right", new PixelRect(1920, 0, 2560, 1440), 144)
        ]).ToDictionary(display => display.Id);

        Assert.Equal(displays["left"].Bounds.Right, displays["primary"].Bounds.Left, precision: 6);
        Assert.Equal(displays["primary"].Bounds.Right, displays["right"].Bounds.Left, precision: 6);
        Assert.Equal(2560 / 1.5, displays["left"].Bounds.Width, precision: 6);
        Assert.Equal(2560 / 1.5, displays["right"].Bounds.Width, precision: 6);
    }

    [Fact]
    public void MixedDpiDisplaysAboveAndBelowFormOneContinuousDesktop()
    {
        var displays = DisplayTopologyConverter.ToDips(
        [
            Display("above", new PixelRect(0, -1440, 2560, 1440), 144),
            Display("primary", new PixelRect(0, 0, 1920, 1080), 96, isPrimary: true),
            Display("below", new PixelRect(0, 1080, 2560, 1440), 144)
        ]).ToDictionary(display => display.Id);

        Assert.Equal(displays["above"].Bounds.Bottom, displays["primary"].Bounds.Top, precision: 6);
        Assert.Equal(displays["primary"].Bounds.Bottom, displays["below"].Bounds.Top, precision: 6);
    }

    [Fact]
    public void WorkAreaInsetsAreScaledWithoutBreakingMonitorAnchors()
    {
        var displays = DisplayTopologyConverter.ToDips(
        [
            new PhysicalDisplayWorkArea(
                "primary",
                new PixelRect(0, 0, 2560, 1440),
                new PixelRect(0, 0, 2560, 1360),
                144,
                144,
                true)
        ]);

        var workArea = Assert.Single(displays).Bounds;
        Assert.Equal(2560 / 1.5, workArea.Width, precision: 6);
        Assert.Equal(1360 / 1.5, workArea.Height, precision: 6);
    }

    [Fact]
    public void CornerTouchingMixedDpiDisplayAnchorsBothAxes()
    {
        var displays = DisplayTopologyConverter.ToDips(
        [
            Display("diagonal", new PixelRect(-2560, -1440, 2560, 1440), 144),
            Display("primary", new PixelRect(0, 0, 1920, 1080), 96, isPrimary: true)
        ]).ToDictionary(display => display.Id);

        Assert.Equal(displays["primary"].Bounds.Left, displays["diagonal"].Bounds.Right, precision: 6);
        Assert.Equal(displays["primary"].Bounds.Top, displays["diagonal"].Bounds.Bottom, precision: 6);
    }

    private static PhysicalDisplayWorkArea Display(
        string id,
        PixelRect bounds,
        uint dpi,
        bool isPrimary = false) =>
        new(id, bounds, bounds, dpi, dpi, isPrimary);
}
