using LolPerformanceOverlay.Core.Interaction;
using Xunit;

namespace LolPerformanceOverlay.Tests;

/// <summary>
/// Drives <see cref="OverlayPlacementController"/> through <see cref="SimulatedDesktop"/>, so
/// every rule for WM_DPICHANGED is checked against the loop Windows would really run.
/// </summary>
public sealed class DpiChainSimulationTests
{
    private static readonly DipSize Dot = new(38, 38);
    private static readonly DipSize ExpandedIdle = new(520, 45);
    private static readonly DipSize ExpandedInGame = new(520, 300);

    public static TheoryData<string, bool> Arrangements => new()
    {
        { "low-left", true },
        { "low-left", false },
        { "low-right", true },
        { "low-right", false },
        { "low-above", true },
        { "low-above", false },
        { "low-below", true },
        { "low-below", false },
        { "partial-overlap", true },
        { "partial-overlap", false }
    };

    [Theory]
    [MemberData(nameof(Arrangements))]
    public void DraggingAcrossAndBackChangesDpiOncePerCrossingInEveryArrangement(string arrangement, bool expanded)
    {
        var (displays, start, end) = Arrangement(arrangement);
        var content = expanded ? ExpandedInGame : Dot;
        var controller = new OverlayPlacementController();
        var desktop = new SimulatedDesktop(displays, controller, content, start, 144);

        controller.BeginGesture(start, desktop.TopLeft);
        controller.BeginDrag();
        desktop.DragAlong(start, end);
        desktop.DragAlong(end, start);

        Assert.False(desktop.LoopDetected);
        Assert.Equal(0, controller.ForcedDpiPlacementCount);
        Assert.Equal(2, desktop.DpiChanges);
        Assert.Equal(1, desktop.LongestChain);
    }

    [Fact]
    public void KeepingTheTopLeftLoopsForeverWhenTheHigherDpiMonitorIsOnTheLeft()
    {
        var (displays, start, end) = Arrangement("low-right");
        var controller = new OverlayPlacementController();
        var desktop = new SimulatedDesktop(displays, controller, ExpandedInGame, start, 144) { Rewrite = false };

        controller.BeginGesture(start, desktop.TopLeft);
        controller.BeginDrag();
        desktop.DragAlong(start, end);

        Assert.True(desktop.LoopDetected);
    }

    [Fact]
    public void APanelGrowingIntoTheLowerDpiMonitorBelowStaysOnHomeAtItsMargin()
    {
        PhysicalDisplayWorkArea[] displays =
        [
            new("top", new PixelRect(0, 0, 2880, 1620), new PixelRect(0, 0, 2880, 1560), 144, 144, IsPrimary: true),
            new("bottom", new PixelRect(0, 1620, 1920, 1080), new PixelRect(0, 1620, 1920, 1040), 96, 96)
        ];

        var desktop = Grow(displays, new PixelPoint(200, 1440), 144, ExpandedInGame);

        Assert.Equal(new PixelPoint(200, 1560 - 15 - 450), desktop.TopLeft);
        Assert.Equal(144u, desktop.Dpi);
    }

    [Fact]
    public void KeepingTheTopLeftLoopsForeverWhenAPanelGrowsFromAHigherDpiMonitorAbove()
    {
        PhysicalDisplayWorkArea[] displays =
        [
            new("top", new PixelRect(0, 0, 2880, 1620), new PixelRect(0, 0, 2880, 1560), 144, 144, IsPrimary: true),
            new("bottom", new PixelRect(0, 1620, 1920, 1080), new PixelRect(0, 1620, 1920, 1040), 96, 96)
        ];
        var controller = new OverlayPlacementController();
        var desktop = new SimulatedDesktop(displays, controller, ExpandedIdle, new PixelPoint(200, 1440), 144) { Rewrite = false };

        desktop.Resize(ExpandedInGame);

        Assert.True(desktop.LoopDetected);
    }

    [Fact]
    public void APanelGrowingIntoTheHigherDpiMonitorBelowLandsExactlyAtHomesMargin()
    {
        PhysicalDisplayWorkArea[] displays =
        [
            new("top", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96, 96, IsPrimary: true),
            new("bottom", new PixelRect(0, 1080, 2880, 1620), new PixelRect(0, 1080, 2880, 1560), 144, 144)
        ];

        var desktop = Grow(displays, new PixelPoint(200, 985), 96, ExpandedInGame);

        Assert.Equal(new PixelPoint(200, 1040 - 10 - 300), desktop.TopLeft);
        Assert.Equal(96u, desktop.Dpi);
    }

    [Fact]
    public void ADotWidenedInPlaceAtTheSecondarysEdgeKeepsTheSecondarysMargin()
    {
        var desktop = Grow(OverlayPlacementTests.DevDesktop, new PixelPoint(-48, 900), 96, ExpandedIdle, Dot);

        Assert.Equal(new PixelPoint(-530, 900), desktop.TopLeft);
        Assert.Equal(96u, desktop.Dpi);
    }

    [Fact]
    public void GrowingPastHomesHeightAtTheNewDpiStillEndsOnHome()
    {
        PhysicalDisplayWorkArea[] displays =
        [
            new("home", new PixelRect(0, 0, 1280, 720), new PixelRect(0, 0, 1280, 680), 96, 96, IsPrimary: true),
            new("below", new PixelRect(0, 720, 3840, 2160), new PixelRect(0, 720, 3840, 2100), 192, 192)
        ];

        var desktop = Grow(displays, new PixelPoint(100, 625), 96, new DipSize(520, 400));

        Assert.Equal(new PixelPoint(100, 680 - 10 - 400), desktop.TopLeft);
        Assert.Equal(96u, desktop.Dpi);
    }

    [Theory]
    [InlineData(192u)]
    [InlineData(240u)]
    public void TheFallbackStepStillEndsAtTheHomeDpiClampFromWhereTheChainStarted(uint belowDpi)
    {
        var width = (int)(1920 * belowDpi / 96d);
        PhysicalDisplayWorkArea[] displays =
        [
            new("home", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1080), 96, 96, IsPrimary: true),
            new("below", new PixelRect(0, 1080, width, width * 9 / 16), new PixelRect(0, 1080, width, width * 9 / 16), belowDpi, belowDpi)
        ];

        var desktop = Grow(displays, new PixelPoint(1390, 1025), 96, ExpandedInGame);

        Assert.Equal(new PixelPoint(1390, 770), desktop.TopLeft);
        Assert.Equal(96u, desktop.Dpi);
    }

    [Fact]
    public void ResettingFromA250PercentLaptopReachesThePrimaryDpiThroughTheStagingPoint()
    {
        var displays = WithLaptopRight();
        var controller = new OverlayPlacementController();
        var desktop = new SimulatedDesktop(displays, controller, ExpandedIdle, new PixelPoint(2500, 200), 240);
        controller.EndGesture(desktop.Rect, desktop.Dpi, ExpandedIdle, displays);

        var move = controller.Reset(desktop.Rect, desktop.Dpi, ExpandedIdle, displays);
        desktop.ApplyMove(move);

        Assert.NotNull(move.Staging);
        Assert.Equal(96u, desktop.Dpi);
        Assert.Equal(OverlayPlacement.Reset(ExpandedIdle, displays), desktop.TopLeft);
        Assert.Equal(0, controller.UntriggeredStagingCount);
        Assert.Equal(0, controller.ForcedDpiPlacementCount);
    }

    [Fact]
    public void WithoutTheStagingPointTheResetLeavesTheWindowAtTheLaptopsDpi()
    {
        var displays = WithLaptopRight();
        var controller = new OverlayPlacementController();
        var desktop = new SimulatedDesktop(displays, controller, ExpandedIdle, new PixelPoint(2500, 200), 240);

        var move = controller.Reset(desktop.Rect, desktop.Dpi, ExpandedIdle, displays);
        desktop.ApplyMove(move with { Staging = null });

        Assert.Equal(240u, desktop.Dpi);
        Assert.Equal(1, controller.UntriggeredStagingCount);
    }

    [Fact]
    public void StartingOnThePrimaryWithASavedSpotOnA100PercentNeighbourEndsThereAtItsDpi()
    {
        PhysicalDisplayWorkArea[] displays =
        [
            new("primary", new PixelRect(0, 0, 3840, 2160), new PixelRect(0, 0, 3840, 2100), 288, 288, IsPrimary: true),
            new("left", new PixelRect(-1920, 0, 1920, 1080), new PixelRect(-1920, 0, 1920, 1040), 96, 96)
        ];
        var controller = new OverlayPlacementController();
        var desktop = new SimulatedDesktop(displays, controller, Dot, new PixelPoint(100, 100), 288);

        var move = controller.Startup(new StartupPosition(-48, 500, null, null), desktop.Rect, desktop.Dpi, Dot, displays);
        desktop.ApplyMove(move);

        Assert.Equal("left", controller.HomeDisplayId);
        Assert.Equal(96u, desktop.Dpi);
        Assert.Equal(new PixelPoint(-48, 500), desktop.TopLeft);
        Assert.Equal(0, controller.UntriggeredStagingCount);
    }

    private static SimulatedDesktop Grow(
        IReadOnlyList<PhysicalDisplayWorkArea> displays,
        PixelPoint topLeft,
        uint dpi,
        DipSize grownTo,
        DipSize? from = null)
    {
        var controller = new OverlayPlacementController();
        var desktop = new SimulatedDesktop(displays, controller, from ?? ExpandedIdle, topLeft, dpi);
        controller.EndGesture(desktop.Rect, desktop.Dpi, desktop.Content, displays);
        Assert.Equal(topLeft, desktop.TopLeft);

        desktop.Resize(grownTo);

        Assert.False(desktop.LoopDetected);
        Assert.InRange(desktop.DpiChanges, 0, 2);
        Assert.Equal(0, controller.ForcedDpiPlacementCount);
        return desktop;
    }

    private static PhysicalDisplayWorkArea[] WithLaptopRight() =>
    [
        new("primary", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96, 96, IsPrimary: true),
        new("laptop", new PixelRect(1920, 0, 3840, 2160), new PixelRect(1920, 0, 3840, 2100), 240, 240)
    ];

    /// <summary>A pair of monitors, a cursor start well inside the 150 % one, and an end well inside the 100 % one.</summary>
    private static (PhysicalDisplayWorkArea[] Displays, PixelPoint Start, PixelPoint End) Arrangement(string name) => name switch
    {
        "low-left" =>
        (
            [
                new("high", new PixelRect(0, 0, 2880, 1620), new PixelRect(0, 0, 2880, 1620), 144, 144, IsPrimary: true),
                new("low", new PixelRect(-1920, 0, 1920, 1080), new PixelRect(-1920, 0, 1920, 1080), 96, 96)
            ],
            new PixelPoint(1000, 200),
            new PixelPoint(-1500, 200)
        ),
        "low-right" =>
        (
            [
                new("high", new PixelRect(0, 0, 2880, 1620), new PixelRect(0, 0, 2880, 1620), 144, 144, IsPrimary: true),
                new("low", new PixelRect(2880, 0, 1920, 1080), new PixelRect(2880, 0, 1920, 1080), 96, 96)
            ],
            new PixelPoint(1000, 200),
            new PixelPoint(3800, 200)
        ),
        "low-above" =>
        (
            [
                new("high", new PixelRect(0, 0, 2880, 1620), new PixelRect(0, 0, 2880, 1620), 144, 144, IsPrimary: true),
                new("low", new PixelRect(0, -1080, 1920, 1080), new PixelRect(0, -1080, 1920, 1080), 96, 96)
            ],
            new PixelPoint(200, 1000),
            new PixelPoint(200, -900)
        ),
        "low-below" =>
        (
            [
                new("high", new PixelRect(0, 0, 2880, 1620), new PixelRect(0, 0, 2880, 1620), 144, 144, IsPrimary: true),
                new("low", new PixelRect(0, 1620, 1920, 1080), new PixelRect(0, 1620, 1920, 1080), 96, 96)
            ],
            new PixelPoint(200, 300),
            new PixelPoint(200, 2200)
        ),
        // A short 150 % laptop beside a taller 100 % monitor, dragged along the laptop's bottom
        // edge so the window hangs below it: keeping the centre alone would leave the old monitor
        // with the larger share.
        "partial-overlap" =>
        (
            [
                new("high", new PixelRect(0, 0, 1800, 1200), new PixelRect(0, 0, 1800, 1200), 144, 144, IsPrimary: true),
                new("low", new PixelRect(1800, -400, 1920, 2000), new PixelRect(1800, -400, 1920, 2000), 96, 96)
            ],
            new PixelPoint(100, 1000),
            new PixelPoint(2600, 1000)
        ),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
}
