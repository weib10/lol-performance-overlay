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

public enum PointerActionKind
{
    CapturePointer,
    ReleasePointer,
    Click,
    BeginDrag,
    DragTo,
    EndDrag,
    CancelGesture
}

public readonly record struct PointerAction(
    PointerActionKind Kind,
    DipPoint Origin = default,
    DipPoint Position = default,
    PointerCancellationReason Reason = default);

/// <summary>
/// A fixed-size value batch. Pointer moves never allocate an input object, action object, array, or
/// iterator on the drag hot path.
/// </summary>
public readonly struct PointerActionBatch
{
    private readonly PointerAction _first;
    private readonly PointerAction _second;
    private readonly PointerAction _third;

    private PointerActionBatch(
        int count,
        PointerAction first,
        PointerAction second = default,
        PointerAction third = default)
    {
        Count = count;
        _first = first;
        _second = second;
        _third = third;
    }

    public int Count { get; }

    public PointerAction this[int index] => index switch
    {
        0 when Count > 0 => _first,
        1 when Count > 1 => _second,
        2 when Count > 2 => _third,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public Enumerator GetEnumerator() => new(this);

    internal static PointerActionBatch One(PointerAction first) => new(1, first);

    internal static PointerActionBatch Two(PointerAction first, PointerAction second) =>
        new(2, first, second);

    internal static PointerActionBatch Three(
        PointerAction first,
        PointerAction second,
        PointerAction third) =>
        new(3, first, second, third);

    public struct Enumerator
    {
        private readonly PointerActionBatch _batch;
        private int _index;

        internal Enumerator(PointerActionBatch batch)
        {
            _batch = batch;
            _index = -1;
        }

        public PointerAction Current => _batch[_index];

        public bool MoveNext()
        {
            _index++;
            return _index < _batch.Count;
        }
    }
}

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
/// A pointer remains captured until release or cancellation.
/// </summary>
public sealed class PointerInteractionStateMachine
{
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

    public PointerActionBatch HandleDown(DipPoint position)
    {
        if (IsPositionLocked)
        {
            return default;
        }

        _origin = position;
        if (State == PointerInteractionState.Idle)
        {
            State = PointerInteractionState.Pressed;
            return PointerActionBatch.One(new PointerAction(PointerActionKind.CapturePointer));
        }

        State = PointerInteractionState.Pressed;
        return PointerActionBatch.Three(
            new PointerAction(
                PointerActionKind.CancelGesture,
                Reason: PointerCancellationReason.Interrupted),
            new PointerAction(PointerActionKind.ReleasePointer),
            new PointerAction(PointerActionKind.CapturePointer));
    }

    public PointerActionBatch HandleMove(DipPoint position)
    {
        if (State == PointerInteractionState.Idle || IsPositionLocked)
        {
            return default;
        }

        if (State == PointerInteractionState.Pressed)
        {
            if (_origin.DistanceSquaredTo(position) <= _dragThresholdSquared)
            {
                return default;
            }

            State = PointerInteractionState.Dragging;
            return PointerActionBatch.One(new PointerAction(
                PointerActionKind.BeginDrag,
                _origin,
                position));
        }

        return PointerActionBatch.One(new PointerAction(PointerActionKind.DragTo, Position: position));
    }

    public PointerActionBatch HandleUp(DipPoint position)
    {
        var previousState = State;
        if (previousState == PointerInteractionState.Idle)
        {
            return default;
        }

        State = PointerInteractionState.Idle;
        return previousState == PointerInteractionState.Dragging
            ? PointerActionBatch.Two(
                new PointerAction(PointerActionKind.EndDrag, Position: position),
                new PointerAction(PointerActionKind.ReleasePointer))
            : PointerActionBatch.Two(
                new PointerAction(PointerActionKind.Click, Position: position),
                new PointerAction(PointerActionKind.ReleasePointer));
    }

    public PointerActionBatch HandleCancel() =>
        Cancel(PointerCancellationReason.Cancelled, releaseCapture: true);

    public PointerActionBatch HandleLostCapture() =>
        Cancel(PointerCancellationReason.LostCapture, releaseCapture: false);

    public PointerActionBatch HandlePositionLock(bool isLocked)
    {
        if (isLocked == IsPositionLocked)
        {
            return default;
        }

        IsPositionLocked = isLocked;
        return isLocked && State != PointerInteractionState.Idle
            ? Cancel(PointerCancellationReason.PositionLocked, releaseCapture: true)
            : default;
    }

    private PointerActionBatch Cancel(
        PointerCancellationReason reason,
        bool releaseCapture)
    {
        if (State == PointerInteractionState.Idle)
        {
            return default;
        }

        State = PointerInteractionState.Idle;
        var cancel = new PointerAction(PointerActionKind.CancelGesture, Reason: reason);
        return releaseCapture
            ? PointerActionBatch.Two(cancel, new PointerAction(PointerActionKind.ReleasePointer))
            : PointerActionBatch.One(cancel);
    }
}
