using LolPerformanceOverlay.Core;
using LolPerformanceOverlay.Core.Interaction;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class OverlayOpacityTests
{
    [Theory]
    [InlineData(0.35)]
    [InlineData(0.5)]
    [InlineData(0.92)]
    [InlineData(1.0)]
    public void ClampLeavesValuesInsideTheRangeUnchanged(double opacity)
    {
        Assert.Equal(opacity, OverlayOpacityPolicy.Clamp(opacity));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.349999)]
    [InlineData(-5)]
    public void ClampRaisesValuesBelowTheMinimumUpToIt(double opacity)
    {
        Assert.Equal(OverlayOpacityPolicy.Minimum, OverlayOpacityPolicy.Clamp(opacity));
    }

    [Theory]
    [InlineData(1.000001)]
    [InlineData(1.5)]
    [InlineData(50)]
    public void ClampLowersValuesAboveTheMaximumDownToIt(double opacity)
    {
        Assert.Equal(OverlayOpacityPolicy.Maximum, OverlayOpacityPolicy.Clamp(opacity));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ClampFallsBackToTheDefaultForNonFiniteInput(double opacity)
    {
        Assert.Equal(OverlayOpacityPolicy.Default, OverlayOpacityPolicy.Clamp(opacity));
    }

    [Fact]
    public void PreviewSessionClampsTheInitialPriorOpacity()
    {
        var session = new OpacityPreviewSession(5.0);

        Assert.Equal(OverlayOpacityPolicy.Maximum, session.PriorOpacity);
        Assert.Equal(OverlayOpacityPolicy.Maximum, session.CurrentOpacity);
    }

    [Fact]
    public void PreviewReturnsTheClampedCandidateAndUpdatesCurrentOpacity()
    {
        var session = new OpacityPreviewSession(0.92);

        Assert.Equal(0.6, session.Preview(0.6));
        Assert.Equal(0.6, session.CurrentOpacity);

        Assert.Equal(OverlayOpacityPolicy.Minimum, session.Preview(0.1));
        Assert.Equal(OverlayOpacityPolicy.Maximum, session.Preview(2));
    }

    [Fact]
    public void CancelAfterDraggingResolvesBackToThePriorOpacityNotTheLastPreview()
    {
        var session = new OpacityPreviewSession(0.92);

        session.Preview(0.5);
        session.Preview(0.35);
        session.Preview(0.8);

        var restored = session.Cancel();

        Assert.Equal(0.92, restored);
        Assert.Equal(0.92, session.CurrentOpacity);
        Assert.Equal(0.92, session.PriorOpacity);
    }

    [Fact]
    public void CancelWithoutAnyPriorDragStillReturnsThePriorOpacity()
    {
        var session = new OpacityPreviewSession(0.7);

        Assert.Equal(0.7, session.Cancel());
    }

    [Fact]
    public void ContextMenuIsRefusedWhilePositionIsLocked()
    {
        Assert.False(OverlayContextMenuPolicy.CanOpen(OverlayMode.Dot, positionLocked: true));
        Assert.False(OverlayContextMenuPolicy.CanOpen(OverlayMode.Compact, positionLocked: true));
        Assert.False(OverlayContextMenuPolicy.CanOpen(OverlayMode.Expanded, positionLocked: true));
    }

    [Theory]
    [InlineData(OverlayMode.Dot)]
    [InlineData(OverlayMode.Compact)]
    [InlineData(OverlayMode.Expanded)]
    public void ContextMenuIsAllowedInEveryUnlockedMode(OverlayMode mode)
    {
        Assert.True(OverlayContextMenuPolicy.CanOpen(mode, positionLocked: false));
    }
}
