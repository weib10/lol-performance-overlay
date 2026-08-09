using LolPerformanceOverlay.Core.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace LolPerformanceOverlay.Tests;

public sealed class InteractionPolicyComparisonTests
{
    private readonly ITestOutputHelper _output;

    public InteractionPolicyComparisonTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void FullSurfacePolicyWinsHeadlessCoverageWithoutInterceptingControls()
    {
        var replayTargets = Enumerable.Repeat(OverlaySurfaceKind.Background, 80)
            .Concat(Enumerable.Repeat(OverlaySurfaceKind.DedicatedGrip, 10))
            .Concat(Enumerable.Repeat(OverlaySurfaceKind.Control, 10))
            .ToArray();

        var fullSurfaceCoverage = replayTargets.Count(surface =>
            OverlayInteractionPolicyRules.CanStartPointerGesture(
                OverlayInteractionPolicy.FullSurfaceGesture,
                surface,
                positionLocked: false));
        var gripCoverage = replayTargets.Count(surface =>
            OverlayInteractionPolicyRules.CanStartPointerGesture(
                OverlayInteractionPolicy.DedicatedGripOnly,
                surface,
                positionLocked: false));

        _output.WriteLine(
            "headless_targets=100; full_surface_draggable=90; grip_only_draggable=10; control_interceptions=0");
        Assert.Equal(90, fullSurfaceCoverage);
        Assert.Equal(10, gripCoverage);
        Assert.False(OverlayInteractionPolicyRules.CanStartPointerGesture(
            OverlayInteractionPolicy.FullSurfaceGesture,
            OverlaySurfaceKind.Control,
            positionLocked: false));
    }

    [Theory]
    [InlineData(OverlayInteractionPolicy.FullSurfaceGesture)]
    [InlineData(OverlayInteractionPolicy.DedicatedGripOnly)]
    public void PositionLockMakesEveryNonControlSurfacePassThrough(OverlayInteractionPolicy policy)
    {
        Assert.False(OverlayInteractionPolicyRules.CanStartPointerGesture(
            policy,
            OverlaySurfaceKind.Background,
            positionLocked: true));
        Assert.False(OverlayInteractionPolicyRules.CanStartPointerGesture(
            policy,
            OverlaySurfaceKind.DedicatedGrip,
            positionLocked: true));
    }
}
