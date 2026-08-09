using LolPerformanceOverlay.Core.Interaction;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class PointerInteractionTests
{
    [Fact]
    public void MovementAtThresholdRemainsClickAndDoesNotMoveWindow()
    {
        var interaction = new PointerInteractionStateMachine(5);

        Assert.Collection(
            interaction.Handle(new PointerDown(new DipPoint(10, 20))),
            action => Assert.IsType<CapturePointer>(action));
        Assert.Empty(interaction.Handle(new PointerMove(new DipPoint(13, 24))));
        Assert.Collection(
            interaction.Handle(new PointerUp(new DipPoint(13, 24))),
            action => Assert.Equal(new Click(new DipPoint(13, 24)), action),
            action => Assert.IsType<ReleasePointer>(action));
    }

    [Fact]
    public void MovementPastThresholdBeginsOneDragAndNeverClicks()
    {
        var interaction = new PointerInteractionStateMachine(5);
        interaction.Handle(new PointerDown(new DipPoint(-120, 30)));

        Assert.Collection(
            interaction.Handle(new PointerMove(new DipPoint(-114, 30))),
            action => Assert.Equal(
                new BeginDrag(new DipPoint(-120, 30), new DipPoint(-114, 30)),
                action));
        Assert.Collection(
            interaction.Handle(new PointerMove(new DipPoint(-110, 40))),
            action => Assert.Equal(new DragTo(new DipPoint(-110, 40)), action));
        var released = interaction.Handle(new PointerUp(new DipPoint(-108, 42)));

        Assert.Collection(
            released,
            action => Assert.Equal(new EndDrag(new DipPoint(-108, 42)), action),
            action => Assert.IsType<ReleasePointer>(action));
        Assert.DoesNotContain(released, action => action is Click);
        Assert.Equal(PointerInteractionState.Idle, interaction.State);
    }

    [Fact]
    public void PositionLockMakesTheHostGestureSurfaceFullyPassThrough()
    {
        var interaction = new PointerInteractionStateMachine(5);
        interaction.Handle(new PositionLockChanged(true));
        Assert.Empty(interaction.Handle(new PointerDown(new DipPoint(20, 20))));
        Assert.Empty(interaction.Handle(new PointerMove(new DipPoint(200, 200))));
        Assert.Empty(interaction.Handle(new PointerUp(new DipPoint(200, 200))));
        Assert.True(interaction.IsPositionLocked);
    }

    [Fact]
    public void LockingDuringDragCancelsGestureAndReleasesCapture()
    {
        var interaction = new PointerInteractionStateMachine(5);
        interaction.Handle(new PointerDown(new DipPoint(0, 0)));
        interaction.Handle(new PointerMove(new DipPoint(6, 0)));

        Assert.Collection(
            interaction.Handle(new PositionLockChanged(true)),
            action => Assert.Equal(
                new CancelGesture(PointerCancellationReason.PositionLocked),
                action),
            action => Assert.IsType<ReleasePointer>(action));
        Assert.Equal(PointerInteractionState.Idle, interaction.State);
    }

    [Fact]
    public void LockingWhilePressedAlsoCancelsCapture()
    {
        var interaction = new PointerInteractionStateMachine(5);
        interaction.Handle(new PointerDown(new DipPoint(0, 0)));

        Assert.Collection(
            interaction.Handle(new PositionLockChanged(true)),
            action => Assert.Equal(
                new CancelGesture(PointerCancellationReason.PositionLocked),
                action),
            action => Assert.IsType<ReleasePointer>(action));
        Assert.Equal(PointerInteractionState.Idle, interaction.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelAndLostCaptureCannotLeaveAStuckGesture(bool lostCapture)
    {
        var interaction = new PointerInteractionStateMachine();
        interaction.Handle(new PointerDown(new DipPoint(0, 0)));
        interaction.Handle(new PointerMove(new DipPoint(20, 0)));

        var actions = interaction.Handle(lostCapture ? new PointerLostCapture() : new PointerCancel());

        Assert.Equal(
            new CancelGesture(
                lostCapture
                    ? PointerCancellationReason.LostCapture
                    : PointerCancellationReason.Cancelled),
            actions[0]);
        Assert.Equal(lostCapture ? 1 : 2, actions.Count);
        Assert.Equal(PointerInteractionState.Idle, interaction.State);
        Assert.Empty(interaction.Handle(new PointerUp(new DipPoint(20, 0))));
    }
}
