using LolPerformanceOverlay.Core.Interaction;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class OverlayPlacementControllerTests
{
    private static readonly PhysicalDisplayWorkArea[] Desktop = OverlayPlacementTests.DevDesktop;
    private static readonly DipSize Dot = new(38, 38);
    private static readonly DipSize Compact = new(460, 112);

    [Fact]
    public void NothingButTheDragMovesTheWindowWhileADragIsInProgress()
    {
        var controller = new OverlayPlacementController();
        var offScreen = new PixelRect(-3000, 900, 38, 38);
        controller.BeginGesture(new PixelPoint(0, 0), new PixelPoint(-3000, 900));
        controller.BeginDrag();

        Assert.Null(controller.Clamp(offScreen, 96, Dot, Desktop));
        Assert.Null(controller.PrePosition(offScreen, 96, Compact, Desktop));

        Assert.NotNull(controller.EndGesture(offScreen, 96, Dot, Desktop));
        Assert.False(controller.IsDragging);
        Assert.Null(controller.Clamp(new PixelRect(-1910, 900, 38, 38), 96, Dot, Desktop));
    }

    [Fact]
    public void DroppingTheWindowWhollyOnAnotherMonitorMakesThatMonitorHomeWithoutMovingIt()
    {
        var controller = new OverlayPlacementController();
        controller.Reset(new PixelRect(100, 100, 57, 57), 144, Dot, Desktop);
        Assert.Equal("primary", controller.HomeDisplayId);

        controller.BeginGesture(new PixelPoint(120, 120), new PixelPoint(100, 100));
        controller.BeginDrag();
        var dropped = new PixelRect(-100, 1200, 38, 38);
        var move = controller.EndGesture(dropped, 96, Dot, Desktop);

        Assert.Null(move);
        Assert.Equal("secondary", controller.HomeDisplayId);

        // The next mode switch positions the bigger panel on the secondary, not back on the primary.
        var switchMove = controller.PrePosition(dropped, 96, Compact, Desktop);
        Assert.NotNull(switchMove);
        Assert.Equal(new PixelPoint(-470, 1200), switchMove!.Value.Final);
    }

    [Fact]
    public void ResetAndStartupHaveAlreadyChangedHomeWhenTheyReturn()
    {
        var controller = new OverlayPlacementController();
        controller.Startup(new StartupPosition(-1500, 900, null, null), new PixelRect(38, 38, 57, 57), 144, Dot, Desktop);
        Assert.Equal("secondary", controller.HomeDisplayId);

        var move = controller.Reset(new PixelRect(-1500, 900, 38, 38), 96, Dot, Desktop);
        Assert.Equal("primary", controller.HomeDisplayId);

        // The WM_DPICHANGED the reset's move triggers must keep the window on the primary.
        var landed = new PixelRect(move.Final.X, move.Final.Y, 38, 38);
        var answer = controller.OnDpiChanged(landed, landed with { Width = 57, Height = 57 }, 144, Dot, Desktop);
        Assert.Equal(move.Final, new PixelPoint(answer.Rect.X, answer.Rect.Y));
    }

    [Fact]
    public void AnUnpluggedHomeIsReplacedAndPluggingItBackDoesNotPullTheOverlayBack()
    {
        var controller = new OverlayPlacementController();
        controller.Startup(new StartupPosition(-1500, 900, null, null), new PixelRect(0, 0, 57, 57), 96, Dot, Desktop);
        PhysicalDisplayWorkArea[] primaryOnly = [OverlayPlacementTests.Primary150];

        var afterUnplug = controller.Clamp(new PixelRect(-1500, 900, 38, 38), 96, Dot, primaryOnly);
        Assert.Equal("primary", controller.HomeDisplayId);
        Assert.NotNull(afterUnplug);

        var onPrimary = new PixelRect(afterUnplug!.Value.Final.X, afterUnplug.Value.Final.Y, 57, 57);
        Assert.Null(controller.Clamp(onPrimary, 144, Dot, Desktop));
        Assert.Equal("primary", controller.HomeDisplayId);
    }

    [Fact]
    public void ADpiChangeMidDragRebasesTheDragOnTheReturnedRectangle()
    {
        var controller = new OverlayPlacementController();
        controller.BeginGesture(new PixelPoint(50, 1000), new PixelPoint(40, 990));
        controller.BeginDrag();
        var at = controller.DragTo(new PixelPoint(-40, 1000));

        var current = new PixelRect(at.X, at.Y, 57, 57);
        var answer = controller.OnDpiChanged(current, current with { Width = 38, Height = 38 }, 96, Dot, Desktop).Rect;

        Assert.NotEqual(at, new PixelPoint(answer.X, answer.Y));
        Assert.Equal(new PixelPoint(answer.X, answer.Y), controller.DragTo(new PixelPoint(-40, 1000)));
    }

    [Fact]
    public void AResizeDrivenDpiChangeMidDragDoesNotOffsetTheWindowFromTheCursor()
    {
        var controller = new OverlayPlacementController();
        controller.BeginGesture(new PixelPoint(50, 1000), new PixelPoint(40, 990));
        controller.BeginDrag();
        var at = controller.DragTo(new PixelPoint(-10, 1000));

        // The Replay switched modes and the new size moved most of the window across; the cursor
        // has not moved since the last DragTo.
        var current = new PixelRect(at.X, at.Y, 690, 168);
        var answer = controller.OnDpiChanged(current, current with { Width = 460, Height = 112 }, 96, Compact, Desktop).Rect;

        Assert.Equal(new PixelPoint(answer.X, answer.Y), controller.DragTo(new PixelPoint(-10, 1000)));
    }

    [Fact]
    public void TheCountersMove()
    {
        var controller = new OverlayPlacementController();
        var current = new PixelRect(-100, 900, 57, 57);
        var suggested = current with { Width = 38, Height = 38 };

        var first = controller.OnDpiChanged(current, suggested, 96, Dot, Desktop);
        var second = controller.OnDpiChanged(current, suggested, 96, Dot, Desktop);
        Assert.True(first.StartedChain);
        Assert.False(second.StartedChain);
        Assert.Equal(0, controller.ForcedDpiPlacementCount);

        controller.OnDpiChanged(current, suggested, 96, Dot, Desktop);
        Assert.Equal(1, controller.ForcedDpiPlacementCount);
        Assert.Equal(3, controller.DpiChangeCount);

        controller.EndDpiChain();
        Assert.True(controller.OnDpiChanged(current, suggested, 96, Dot, Desktop).StartedChain);
        Assert.Equal(1, controller.ForcedDpiPlacementCount);

        var move = controller.Reset(current, 96, Dot, Desktop);
        controller.CompleteMove(move.TargetDpi, move.Final);
        Assert.Equal(0, controller.UntriggeredStagingCount);
        controller.CompleteMove(96, move.Final);
        Assert.Equal(1, controller.UntriggeredStagingCount);
    }

    [Fact]
    public void TheDragFollowsFromWhereTheWindowWasMovedBetweenThePressAndTheThreshold()
    {
        var controller = new OverlayPlacementController();
        var cursorAtPress = new PixelPoint(-40, 1790);
        controller.Startup(new StartupPosition(-48, 1764, null, null), new PixelRect(0, 0, 57, 57), 96, Dot, Desktop);
        controller.BeginGesture(cursorAtPress, new PixelPoint(-48, 1754));

        // An automatic switch to Compact pre-positions the panel before the drag threshold is hit.
        var move = controller.PrePosition(new PixelRect(-48, 1754, 38, 38), 96, Compact, Desktop);
        Assert.NotNull(move);
        controller.CompleteMove(96, move!.Value.Final);

        controller.BeginDrag();
        var cursor = new PixelPoint(-30, 1780);
        Assert.Equal(
            new PixelPoint(move.Value.Final.X + 10, move.Value.Final.Y - 10),
            controller.DragTo(cursor));
    }

    [Fact]
    public void TheDragAlsoFollowsARectangleSetByADpiChangeBeforeTheThreshold()
    {
        var controller = new OverlayPlacementController();
        controller.Startup(new StartupPosition(-1500, 900, null, null), new PixelRect(0, 0, 57, 57), 96, Dot, Desktop);
        controller.BeginGesture(new PixelPoint(-50, 910), new PixelPoint(-60, 900));

        // Compact grew from the secondary's right edge until most of it was on the primary.
        var current = new PixelRect(-60, 900, 460, 112);
        var answer = controller.OnDpiChanged(current, current with { Width = 690, Height = 168 }, 144, Compact, Desktop).Rect;
        Assert.Equal(new PixelPoint(-470, 900), new PixelPoint(answer.X, answer.Y));

        controller.BeginDrag();
        Assert.Equal(new PixelPoint(-465, 900), controller.DragTo(new PixelPoint(-45, 910)));
    }

    [Fact]
    public void AWindowStuckAtTheWrongDpiIsMovedEvenThoughItsPositionIsRight()
    {
        PhysicalDisplayWorkArea[] displays =
        [
            new("primary", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96, 96, IsPrimary: true),
            new("laptop", new PixelRect(1920, 0, 3840, 2160), new PixelRect(1920, 0, 3840, 2100), 240, 240)
        ];
        var controller = new OverlayPlacementController();
        controller.Reset(new PixelRect(1390, 10, 1300, 113), 240, new DipSize(520, 45), displays);

        var move = controller.Clamp(new PixelRect(1390, 10, 1300, 113), 240, new DipSize(520, 45), displays);

        Assert.NotNull(move);
        Assert.NotNull(move!.Value.Staging);
        Assert.Equal(new PixelPoint(1390, 10), move.Value.Final);
    }

    [Fact]
    public void AMoveWithinOneDpiNeverStages()
    {
        var controller = new OverlayPlacementController();
        controller.Reset(new PixelRect(100, 100, 57, 57), 144, Dot, Desktop);

        var move = controller.Clamp(new PixelRect(4000, 100, 57, 57), 144, Dot, Desktop);

        Assert.NotNull(move);
        Assert.Null(move!.Value.Staging);
    }

    [Fact]
    public void ThePhysicalDragOffsetIsNotScaledAcrossMonitors()
    {
        var anchor = new PhysicalDragAnchor(new PixelPoint(100, 900), new PixelPoint(80, 880));

        Assert.Equal(new PixelPoint(-1420, 980), anchor.WindowPositionFor(new PixelPoint(-1400, 1000)));
        Assert.Equal(new PixelPoint(7, 8), anchor.RebasedTo(new PixelPoint(1, 2), new PixelPoint(7, 8)).WindowPositionFor(new PixelPoint(1, 2)));
    }

    [Fact]
    public void DpiTransitionKeepsTheSuggestedSizeAndCentreWhileDragging()
    {
        var current = new PixelRect(-100, 900, 57, 57);
        var suggested = current with { Width = 38, Height = 38 };

        var rect = DpiTransition.Resolve(
            current, suggested, 96, Desktop, DpiTransitionMode.Dragging, "primary", default, Dot, 10, forceInsideTarget: false);

        Assert.Equal(38, rect.Width);
        Assert.Equal(38, rect.Height);
        Assert.Equal(new PixelPoint(-100 + 9, 900 + 9), new PixelPoint(rect.X, rect.Y));
    }

    [Fact]
    public void DpiTransitionAimsAnAnchoredStepAtTheHomeDpiClampFromTheChainsOrigin()
    {
        var current = new PixelRect(-530, 900, 520, 45);
        var suggested = current with { Width = 780, Height = 68 };

        var rect = DpiTransition.Resolve(
            current, suggested, 144, Desktop, DpiTransitionMode.Anchored, "secondary", new PixelPoint(-48, 900), new DipSize(520, 45), 10, forceInsideTarget: false);

        Assert.Equal(new PixelPoint(-530, 900), new PixelPoint(rect.X, rect.Y));
        Assert.Equal(780, rect.Width);
    }

    [Fact]
    public void ForcingPutsTheWholeRectangleOnTheTargetMonitor()
    {
        var current = new PixelRect(-50, 900, 57, 57);
        var suggested = current with { Width = 60, Height = 60 };

        var rect = DpiTransition.Resolve(
            current, suggested, 96, Desktop, DpiTransitionMode.Dragging, null, default, Dot, 10, forceInsideTarget: true);

        Assert.Equal(new PixelRect(-60, 900, 60, 60), rect);
    }
}
