using LolPerformanceOverlay.Core.Interaction;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class PointerInteractionTests
{
    [Fact]
    public void MovementAtThresholdRemainsClickAndDoesNotMoveWindow()
    {
        var interaction = new PointerInteractionStateMachine(5);

        AssertAction(interaction.HandleDown(new DipPoint(10, 20)), PointerActionKind.CapturePointer);
        Assert.Equal(0, interaction.HandleMove(new DipPoint(13, 24)).Count);
        var released = interaction.HandleUp(new DipPoint(13, 24));
        Assert.Equal(2, released.Count);
        Assert.Equal(new PointerAction(PointerActionKind.Click, Position: new DipPoint(13, 24)), released[0]);
        Assert.Equal(PointerActionKind.ReleasePointer, released[1].Kind);
    }

    [Fact]
    public void MovementPastThresholdBeginsOneDragAndNeverClicks()
    {
        var interaction = new PointerInteractionStateMachine(5);
        interaction.HandleDown(new DipPoint(-120, 30));

        Assert.Equal(
            new PointerAction(
                PointerActionKind.BeginDrag,
                new DipPoint(-120, 30),
                new DipPoint(-114, 30)),
            interaction.HandleMove(new DipPoint(-114, 30))[0]);
        Assert.Equal(
            new PointerAction(PointerActionKind.DragTo, Position: new DipPoint(-110, 40)),
            interaction.HandleMove(new DipPoint(-110, 40))[0]);
        var released = interaction.HandleUp(new DipPoint(-108, 42));

        Assert.Equal(2, released.Count);
        Assert.Equal(
            new PointerAction(PointerActionKind.EndDrag, Position: new DipPoint(-108, 42)),
            released[0]);
        Assert.Equal(PointerActionKind.ReleasePointer, released[1].Kind);
        Assert.False(Contains(released, PointerActionKind.Click));
        Assert.Equal(PointerInteractionState.Idle, interaction.State);
    }

    [Fact]
    public void PositionLockMakesTheHostGestureSurfaceFullyPassThrough()
    {
        var interaction = new PointerInteractionStateMachine(5);
        interaction.HandlePositionLock(true);
        Assert.Equal(0, interaction.HandleDown(new DipPoint(20, 20)).Count);
        Assert.Equal(0, interaction.HandleMove(new DipPoint(200, 200)).Count);
        Assert.Equal(0, interaction.HandleUp(new DipPoint(200, 200)).Count);
        Assert.True(interaction.IsPositionLocked);
    }

    [Fact]
    public void LockingDuringDragCancelsGestureAndReleasesCapture()
    {
        var interaction = new PointerInteractionStateMachine(5);
        interaction.HandleDown(new DipPoint(0, 0));
        interaction.HandleMove(new DipPoint(6, 0));

        var actions = interaction.HandlePositionLock(true);
        Assert.Equal(2, actions.Count);
        Assert.Equal(PointerActionKind.CancelGesture, actions[0].Kind);
        Assert.Equal(PointerCancellationReason.PositionLocked, actions[0].Reason);
        Assert.Equal(PointerActionKind.ReleasePointer, actions[1].Kind);
        Assert.Equal(PointerInteractionState.Idle, interaction.State);
    }

    [Fact]
    public void LockingWhilePressedAlsoCancelsCapture()
    {
        var interaction = new PointerInteractionStateMachine(5);
        interaction.HandleDown(new DipPoint(0, 0));

        var actions = interaction.HandlePositionLock(true);
        Assert.Equal(2, actions.Count);
        Assert.Equal(PointerActionKind.CancelGesture, actions[0].Kind);
        Assert.Equal(PointerCancellationReason.PositionLocked, actions[0].Reason);
        Assert.Equal(PointerActionKind.ReleasePointer, actions[1].Kind);
        Assert.Equal(PointerInteractionState.Idle, interaction.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelAndLostCaptureCannotLeaveAStuckGesture(bool lostCapture)
    {
        var interaction = new PointerInteractionStateMachine();
        interaction.HandleDown(new DipPoint(0, 0));
        interaction.HandleMove(new DipPoint(20, 0));

        var actions = lostCapture
            ? interaction.HandleLostCapture()
            : interaction.HandleCancel();

        Assert.Equal(PointerActionKind.CancelGesture, actions[0].Kind);
        Assert.Equal(
            lostCapture ? PointerCancellationReason.LostCapture : PointerCancellationReason.Cancelled,
            actions[0].Reason);
        Assert.Equal(lostCapture ? 1 : 2, actions.Count);
        Assert.Equal(PointerInteractionState.Idle, interaction.State);
        Assert.Equal(0, interaction.HandleUp(new DipPoint(20, 0)).Count);
    }

    [Fact]
    public void DragMoveHotPathDoesNotAllocate()
    {
        var interaction = new PointerInteractionStateMachine();
        interaction.HandleDown(new DipPoint(0, 0));
        interaction.HandleMove(new DipPoint(10, 0));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var observedDragMoves = 0;

        for (var index = 0; index < 10_000; index++)
        {
            var actions = interaction.HandleMove(new DipPoint(10 + index, index));
            if (actions[0].Kind == PointerActionKind.DragTo)
            {
                observedDragMoves++;
            }
        }

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(10_000, observedDragMoves);
        Assert.Equal(0, allocatedBytes);
    }

    private static void AssertAction(PointerActionBatch actions, PointerActionKind expected)
    {
        Assert.Equal(1, actions.Count);
        Assert.Equal(expected, actions[0].Kind);
    }

    private static bool Contains(PointerActionBatch actions, PointerActionKind kind)
    {
        for (var index = 0; index < actions.Count; index++)
        {
            if (actions[index].Kind == kind)
            {
                return true;
            }
        }

        return false;
    }
}
