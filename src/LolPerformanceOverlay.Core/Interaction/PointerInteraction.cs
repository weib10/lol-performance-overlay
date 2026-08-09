namespace LolPerformanceOverlay.Core.Interaction;

public readonly record struct DipPoint(double X, double Y)
{
    public double DistanceSquaredTo(DipPoint other)
    {
        var deltaX = other.X - X;
        var deltaY = other.Y - Y;
        return deltaX * deltaX + deltaY * deltaY;
    }
}

public abstract record PointerInput;

public sealed record PointerDown(DipPoint Position) : PointerInput;

public sealed record PointerMove(DipPoint Position) : PointerInput;

public sealed record PointerUp(DipPoint Position) : PointerInput;

public sealed record PointerCancel : PointerInput;

public sealed record PointerLostCapture : PointerInput;

public sealed record PositionLockChanged(bool IsLocked) : PointerInput;

public abstract record PointerAction;

public sealed record CapturePointer : PointerAction;

public sealed record ReleasePointer : PointerAction;

public sealed record Click(DipPoint Position) : PointerAction;

public sealed record BeginDrag(DipPoint Origin, DipPoint Position) : PointerAction;

public sealed record DragTo(DipPoint Position) : PointerAction;

public sealed record EndDrag(DipPoint Position) : PointerAction;

public sealed record CancelGesture(PointerCancellationReason Reason) : PointerAction;

public enum PointerCancellationReason
{
    Cancelled,
    LostCapture,
    PositionLocked,
    Interrupted
}

public enum PointerInteractionState
{
    Idle,
    Pressed,
    Dragging
}

/// <summary>
/// Converts pointer input in device-independent pixels into mutually exclusive click and drag actions.
/// A pointer must remain captured from <see cref="CapturePointer"/> until release or cancellation.
/// </summary>
public sealed class PointerInteractionStateMachine
{
    private static readonly IReadOnlyList<PointerAction> NoActions = Array.Empty<PointerAction>();
    private readonly double _dragThresholdSquared;
    private DipPoint _origin;

    public PointerInteractionStateMachine(double dragThresholdDips = 5)
    {
        if (!double.IsFinite(dragThresholdDips) || dragThresholdDips <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dragThresholdDips));
        }

        _dragThresholdSquared = dragThresholdDips * dragThresholdDips;
    }

    public PointerInteractionState State { get; private set; }

    public bool IsPositionLocked { get; private set; }

    public IReadOnlyList<PointerAction> Handle(PointerInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input switch
        {
            PointerDown down => HandleDown(down),
            PointerMove move => HandleMove(move),
            PointerUp up => HandleUp(up),
            PointerCancel => Cancel(PointerCancellationReason.Cancelled, releaseCapture: true),
            PointerLostCapture => Cancel(PointerCancellationReason.LostCapture, releaseCapture: false),
            PositionLockChanged changed => HandleLockChanged(changed),
            _ => throw new ArgumentOutOfRangeException(nameof(input))
        };
    }

    private IReadOnlyList<PointerAction> HandleDown(PointerDown input)
    {
        if (IsPositionLocked)
        {
            return NoActions;
        }

        _origin = input.Position;
        if (State == PointerInteractionState.Idle)
        {
            State = PointerInteractionState.Pressed;
            return [new CapturePointer()];
        }

        State = PointerInteractionState.Pressed;
        return
        [
            new CancelGesture(PointerCancellationReason.Interrupted),
            new ReleasePointer(),
            new CapturePointer()
        ];
    }

    private IReadOnlyList<PointerAction> HandleMove(PointerMove input)
    {
        if (State == PointerInteractionState.Idle || IsPositionLocked)
        {
            return NoActions;
        }

        if (State == PointerInteractionState.Pressed)
        {
            if (_origin.DistanceSquaredTo(input.Position) <= _dragThresholdSquared)
            {
                return NoActions;
            }

            State = PointerInteractionState.Dragging;
            return [new BeginDrag(_origin, input.Position)];
        }

        return [new DragTo(input.Position)];
    }

    private IReadOnlyList<PointerAction> HandleUp(PointerUp input)
    {
        var previousState = State;
        if (previousState == PointerInteractionState.Idle)
        {
            return NoActions;
        }

        State = PointerInteractionState.Idle;
        return previousState == PointerInteractionState.Dragging
            ? [new EndDrag(input.Position), new ReleasePointer()]
            : [new Click(input.Position), new ReleasePointer()];
    }

    private IReadOnlyList<PointerAction> HandleLockChanged(PositionLockChanged input)
    {
        if (input.IsLocked == IsPositionLocked)
        {
            return NoActions;
        }

        IsPositionLocked = input.IsLocked;
        return input.IsLocked && State != PointerInteractionState.Idle
            ? Cancel(PointerCancellationReason.PositionLocked, releaseCapture: true)
            : NoActions;
    }

    private IReadOnlyList<PointerAction> Cancel(
        PointerCancellationReason reason,
        bool releaseCapture)
    {
        if (State == PointerInteractionState.Idle)
        {
            return NoActions;
        }

        State = PointerInteractionState.Idle;
        return releaseCapture
            ? [new CancelGesture(reason), new ReleasePointer()]
            : [new CancelGesture(reason)];
    }
}
