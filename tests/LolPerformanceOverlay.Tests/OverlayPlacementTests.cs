using LolPerformanceOverlay.Core.Interaction;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class OverlayPlacementTests
{
    // The development desktop this change was measured on: a 150 % 4K primary and a 100 % 1080p
    // monitor to its lower left, so every secondary coordinate is negative on X.
    internal static readonly PhysicalDisplayWorkArea Primary150 = new(
        "primary",
        new PixelRect(0, 0, 3840, 2160),
        new PixelRect(0, 0, 3840, 2088),
        144,
        144,
        IsPrimary: true);

    internal static readonly PhysicalDisplayWorkArea LowerLeft100 = new(
        "secondary",
        new PixelRect(-1920, 732, 1920, 1080),
        new PixelRect(-1920, 732, 1920, 1032),
        96,
        96);

    internal static readonly PhysicalDisplayWorkArea[] DevDesktop = [Primary150, LowerLeft100];

    private static readonly DipSize Dot = new(38, 38);
    private static readonly DipSize Expanded = new(520, 300);

    [Fact]
    public void AWindowAlreadyInsideTheSecondaryStaysWhereItIs()
    {
        var result = OverlayPlacement.Clamp(new PixelRect(-1500, 900, 38, 38), Dot, DevDesktop);

        Assert.Equal("secondary", result.DisplayId);
        Assert.Equal(new PixelPoint(-1500, 900), result.Position);
        Assert.False(result.WasAdjusted);
    }

    [Fact]
    public void PullingBackFromTheSecondarysLeftEdgeUsesThatMonitorsMargin()
    {
        var result = OverlayPlacement.Clamp(new PixelRect(-1930, 900, 38, 38), Dot, DevDesktop);

        Assert.Equal("secondary", result.DisplayId);
        Assert.Equal(-1910, result.Position.X);
        Assert.True(result.WasAdjusted);
    }

    [Fact]
    public void TheMonitorSharingTheMostAreaWinsOverTheOneHoldingTheCentre()
    {
        // Crosses x = 0 just below the secondary's top edge. The centre (-10, 750) is on the
        // secondary, but the primary, which also spans the rows above 732, holds more of it
        // (40 x 300 px against 60 x 168 px). Windows picks the DPI the same way.
        var result = OverlayPlacement.Clamp(new PixelRect(-60, 600, 100, 300), new DipSize(100, 300), DevDesktop);

        Assert.Equal("primary", result.DisplayId);
        Assert.Equal(new PixelPoint(15, 600), result.Position);
    }

    [Fact]
    public void ClampingItsOwnResultAgainAtTheLandingDpiChangesNothing()
    {
        var first = OverlayPlacement.Clamp(new PixelRect(-60, 600, 100, 300), new DipSize(100, 300), DevDesktop);
        var again = OverlayPlacement.Clamp(
            new PixelRect(first.Position.X, first.Position.Y, 150, 450),
            new DipSize(100, 300),
            DevDesktop);

        Assert.Equal(first.Position, again.Position);
        Assert.False(again.WasAdjusted);
    }

    [Fact]
    public void AnExpandedPanelMovedOntoTheSecondaryIsSizedAtThatMonitorsDpi()
    {
        // At 150 % the panel is 780 px wide; on the 100 % secondary it will be 520 px, so it may
        // sit 10 px from that monitor's right edge rather than 790.
        var result = OverlayPlacement.Clamp(
            new PixelRect(-400, 900, 780, 450),
            Expanded,
            DevDesktop,
            preferredDisplayId: "secondary");

        Assert.Equal(-530, result.Position.X);
    }

    [Fact]
    public void APreferredMonitorKeepsAGrowingPanelOnItInsteadOfTheNeighbour()
    {
        // Dot at the secondary's right edge switched to Expanded: Windows has already given the
        // window the primary's DPI (780 px, mostly on the primary).
        var current = new PixelRect(-60, 900, 780, 68);
        var home = OverlayPlacement.Clamp(current, new DipSize(520, 45), DevDesktop, preferredDisplayId: "secondary");
        var byArea = OverlayPlacement.Clamp(current, new DipSize(520, 45), DevDesktop);

        Assert.Equal("secondary", home.DisplayId);
        Assert.Equal(new PixelPoint(-530, 900), home.Position);
        Assert.Equal("primary", byArea.DisplayId);
    }

    [Fact]
    public void APreferredMonitorAboveKeepsAPanelThatGrewDownIntoTheOneBelow()
    {
        PhysicalDisplayWorkArea[] stacked =
        [
            new("top", new PixelRect(0, -1080, 1920, 1080), new PixelRect(0, -1080, 1920, 1080), 96, 96),
            new("bottom", new PixelRect(0, 0, 2880, 1620), new PixelRect(0, 0, 2880, 1560), 144, 144, IsPrimary: true)
        ];

        var result = OverlayPlacement.Clamp(
            new PixelRect(100, -100, 780, 450),
            Expanded,
            stacked,
            preferredDisplayId: "top");

        Assert.Equal("top", result.DisplayId);
        Assert.Equal(new PixelPoint(100, -1080 + 1080 - 10 - 300), result.Position);
    }

    [Fact]
    public void PositioningForTheNewSizeFirstKeepsTheWholeResizedWindowOnHome()
    {
        var position = OverlayPlacement.Clamp(new PixelRect(-48, 1700, 38, 38), Expanded, DevDesktop, "secondary").Position;

        var bounds = LowerLeft100.WorkArea;
        Assert.InRange(position.X, bounds.X, bounds.X + bounds.Width - 520);
        Assert.InRange(position.Y, bounds.Y, bounds.Y + bounds.Height - 300);
    }

    [Fact]
    public void AnUnpluggedMonitorsPositionFallsBackToTheNearestWorkArea()
    {
        var result = OverlayPlacement.Clamp(new PixelRect(9000, 500, 780, 450), Expanded, DevDesktop);

        Assert.Equal("primary", result.DisplayId);
        Assert.Equal(new PixelPoint(3840 - 15 - 780, 500), result.Position);
    }

    [Fact]
    public void FirstRunKeepsTheOldDefaultOffsetsAtThePrimarysScale()
    {
        Assert.Equal(new PixelPoint(3840 - 87, 144), OverlayPlacement.FirstRun(DevDesktop));
    }

    [Fact]
    public void ResetGoesToThePrimaryWorkAreasTopRightCorner()
    {
        Assert.Equal(new PixelPoint(3840 - 15 - 57, 15), OverlayPlacement.Reset(Dot, DevDesktop));
    }

    [Fact]
    public void AWindowLargerThanTheWorkAreaIsAlignedTopLeftInsteadOfThrowing()
    {
        PhysicalDisplayWorkArea[] small =
        [
            new("small", new PixelRect(0, 0, 800, 600), new PixelRect(0, 0, 800, 600), 96, 96, IsPrimary: true)
        ];

        var result = OverlayPlacement.Clamp(new PixelRect(-100, -100, 2400, 1200), new DipSize(2400, 1200), small);

        Assert.Equal(new PixelPoint(0, 0), result.Position);
    }

    [Fact]
    public void LegacyDipsOnEachMonitorAreScaledByThatMonitorsDpi()
    {
        Assert.Equal(new PixelPoint(3000, 750), OverlayPlacement.FromLegacyDips(new DipPoint(2000, 500), DevDesktop));
        Assert.Equal(new PixelPoint(-1500, 900), OverlayPlacement.FromLegacyDips(new DipPoint(-1500, 900), DevDesktop));
    }

    [Fact]
    public void LegacyDipsOutsideEveryOldMonitorUseThePrimaryDpiAndAreThenPulledOnScreen()
    {
        var converted = OverlayPlacement.FromLegacyDips(new DipPoint(-3000, 100), DevDesktop);
        var placed = OverlayPlacement.Clamp(new PixelRect(converted.X, converted.Y, 57, 57), Dot, DevDesktop);

        Assert.Equal(new PixelPoint(-4500, 150), converted);
        Assert.Equal("secondary", placed.DisplayId);
        Assert.Equal(new PixelPoint(-1910, 742), placed.Position);
    }

    [Fact]
    public void StartupPrefersTheSavedPositionThenTheLegacyOneThenFirstRun()
    {
        Assert.Equal(new PixelPoint(-48, 900), OverlayPlacement.ResolveStartup(-48, 900, 2000, 500, DevDesktop));
        Assert.Equal(new PixelPoint(3000, 750), OverlayPlacement.ResolveStartup(null, null, 2000, 500, DevDesktop));
        Assert.Equal(OverlayPlacement.FirstRun(DevDesktop), OverlayPlacement.ResolveStartup(null, null, null, null, DevDesktop));
    }

    [Fact]
    public void OutOfRangeOrNonFiniteSavedValuesCountAsAbsent()
    {
        var firstRun = OverlayPlacement.FirstRun(DevDesktop);

        Assert.Equal(firstRun, OverlayPlacement.ResolveStartup(int.MaxValue, 0, null, null, DevDesktop));
        Assert.Equal(firstRun, OverlayPlacement.ResolveStartup(null, null, 1e308, 0, DevDesktop));
        Assert.Equal(firstRun, OverlayPlacement.ResolveStartup(double.NaN, 5, double.PositiveInfinity, 5, DevDesktop));
        Assert.Equal(new PixelPoint(3000, 750), OverlayPlacement.ResolveStartup(12.5, null, 2000, 500, DevDesktop));
    }

    [Fact]
    public void ExtremeBoundsReturnTheResetPositionInsteadOfOverflowing()
    {
        var result = OverlayPlacement.Clamp(new PixelRect(int.MaxValue - 5, int.MaxValue - 5, 57, 57), Dot, DevDesktop);

        Assert.Equal(OverlayPlacement.Reset(Dot, DevDesktop), result.Position);
        Assert.True(result.WasAdjusted);
    }

    [Fact]
    public void MissingDisplaysOrAnInvalidSizeAreRejected()
    {
        Assert.Throws<ArgumentException>(() => OverlayPlacement.Clamp(new PixelRect(0, 0, 1, 1), Dot, []));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OverlayPlacement.Clamp(new PixelRect(0, 0, 1, 1), new DipSize(double.NaN, 1), DevDesktop));
    }

    [Fact]
    public void ASavedDotAgainstTheSecondarysEdgeBelongsToTheSecondaryAtAThreeTimesPrimary()
    {
        PhysicalDisplayWorkArea[] desktop =
        [
            new("primary", new PixelRect(0, 0, 3840, 2160), new PixelRect(0, 0, 3840, 2100), 288, 288, IsPrimary: true),
            new("left", new PixelRect(-1920, 0, 1920, 1080), new PixelRect(-1920, 0, 1920, 1040), 96, 96)
        ];

        Assert.Equal("left", OverlayPlacement.SelectDisplayForSavedPosition(new PixelPoint(-48, 500), Dot, desktop));
    }

    [Theory]
    [InlineData(1872, "external")]
    [InlineData(1500, "external")]
    public void ASavedDotBesideAHigherDpiLaptopStaysOnTheExternalMonitor(int savedX, string expected)
    {
        PhysicalDisplayWorkArea[] desktop =
        [
            new("external", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96, 96, IsPrimary: true),
            new("laptop", new PixelRect(1920, 0, 2880, 1800), new PixelRect(1920, 0, 2880, 1740), 192, 192)
        ];

        Assert.Equal(expected, OverlayPlacement.SelectDisplayForSavedPosition(new PixelPoint(savedX, 10), Dot, desktop));
    }

    [Fact]
    public void ASavedDotWithTheHigherDpiLaptopOnTheLeftStaysOnTheExternalMonitor()
    {
        PhysicalDisplayWorkArea[] desktop =
        [
            new("laptop", new PixelRect(-2880, 0, 2880, 1800), new PixelRect(-2880, 0, 2880, 1740), 192, 192),
            new("external", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96, 96, IsPrimary: true)
        ];

        Assert.Equal("external", OverlayPlacement.SelectDisplayForSavedPosition(new PixelPoint(10, 10), Dot, desktop));
    }

    [Fact]
    public void ASavedPointOnNoMonitorIsRankedByCoverageNotByAbsoluteArea()
    {
        // The top-left is in the empty corner left of the 300 % monitor and above the 100 % one.
        // At 300 % the Dot is 114 px and 1,805 px of it land on the big monitor (14 %); at 100 % it
        // is 38 px and 361 px land on the small one (25 %). Absolute area would pick the big one.
        PhysicalDisplayWorkArea[] desktop =
        [
            new("big", new PixelRect(0, 0, 3840, 2160), new PixelRect(0, 0, 3840, 2160), 288, 288, IsPrimary: true),
            new("small", new PixelRect(-1920, 2160, 1920, 1080), new PixelRect(-1920, 2160, 1920, 1080), 96, 96)
        ];
        var saved = new PixelPoint(-19, 2141);

        Assert.Equal("small", OverlayPlacement.SelectDisplayForSavedPosition(saved, Dot, desktop));
    }

    [Fact]
    public void ASavedPointTouchingNoMonitorGoesToTheNearestWorkArea()
    {
        Assert.Equal("secondary", OverlayPlacement.SelectDisplayForSavedPosition(new PixelPoint(-3000, 1000), Dot, DevDesktop));
    }
}
