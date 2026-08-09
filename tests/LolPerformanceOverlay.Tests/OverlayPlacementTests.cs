using LolPerformanceOverlay.Core.Interaction;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class OverlayPlacementTests
{
    private static readonly DisplayWorkArea[] WorkAreas =
    [
        new("left", new DipRect(-1920, 0, 1920, 1040)),
        new("primary", new DipRect(0, 0, 1920, 1040), true),
        new("above", new DipRect(0, -1080, 1920, 1040))
    ];

    [Fact]
    public void NegativeMonitorCoordinatesRemainOnTheirCurrentMonitor()
    {
        var result = OverlayPlacement.Clamp(
            new DipPoint(-1900, 1020),
            new DipSize(440, 102),
            WorkAreas,
            10);

        Assert.Equal("left", result.WorkAreaId);
        Assert.Equal(new DipPoint(-1900, 928), result.Position);
        Assert.True(result.WasAdjusted);
    }

    [Fact]
    public void RemovedMonitorFallsBackToNearestVisibleWorkArea()
    {
        var result = OverlayPlacement.Clamp(
            new DipPoint(4200, 500),
            new DipSize(700, 476),
            WorkAreas,
            10);

        Assert.Equal("primary", result.WorkAreaId);
        Assert.Equal(new DipPoint(1210, 500), result.Position);
    }

    [Fact]
    public void SizeChangeIsReclampedWithoutChangingDipCoordinateSystem()
    {
        var dot = OverlayPlacement.Clamp(
            new DipPoint(1870, 96),
            new DipSize(34, 34),
            WorkAreas,
            10);
        var expanded = OverlayPlacement.Clamp(
            dot.Position,
            new DipSize(700, 476),
            WorkAreas,
            10);

        Assert.Equal(new DipPoint(1870, 96), dot.Position);
        Assert.Equal(new DipPoint(1210, 96), expanded.Position);
        Assert.Equal("primary", expanded.WorkAreaId);
    }

    [Fact]
    public void InvalidSavedPositionUsesPrimaryWorkAreaDefault()
    {
        var result = OverlayPlacement.Clamp(
            new DipPoint(double.NaN, double.PositiveInfinity),
            new DipSize(440, 102),
            WorkAreas,
            10);

        Assert.Equal("primary", result.WorkAreaId);
        Assert.Equal(new DipPoint(1470, 10), result.Position);
    }

    [Fact]
    public void OversizedWindowMaximizesVisibilityInsteadOfThrowing()
    {
        var result = OverlayPlacement.Clamp(
            new DipPoint(-100, -100),
            new DipSize(2400, 1200),
            [new DisplayWorkArea("small", new DipRect(0, 0, 800, 600), true)],
            10);

        Assert.Equal(new DipPoint(0, 0), result.Position);
    }
}
