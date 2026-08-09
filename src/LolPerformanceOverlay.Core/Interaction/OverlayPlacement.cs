namespace LolPerformanceOverlay.Core.Interaction;

public readonly record struct DipSize(double Width, double Height)
{
    public bool IsValid =>
        double.IsFinite(Width) &&
        double.IsFinite(Height) &&
        Width > 0 &&
        Height > 0;
}

public readonly record struct DipRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;

    public double Area => Math.Max(Width, 0) * Math.Max(Height, 0);

    public bool IsValid =>
        double.IsFinite(Left) &&
        double.IsFinite(Top) &&
        double.IsFinite(Width) &&
        double.IsFinite(Height) &&
        Width > 0 &&
        Height > 0;

    public bool Contains(DipPoint point) =>
        point.X >= Left &&
        point.X < Right &&
        point.Y >= Top &&
        point.Y < Bottom;

    public double IntersectionArea(DipRect other)
    {
        var width = Math.Max(0, Math.Min(Right, other.Right) - Math.Max(Left, other.Left));
        var height = Math.Max(0, Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top));
        return width * height;
    }

    public double DistanceSquaredTo(DipPoint point)
    {
        var deltaX = point.X < Left ? Left - point.X : point.X > Right ? point.X - Right : 0;
        var deltaY = point.Y < Top ? Top - point.Y : point.Y > Bottom ? point.Y - Bottom : 0;
        return deltaX * deltaX + deltaY * deltaY;
    }
}

public sealed record DisplayWorkArea(string Id, DipRect Bounds, bool IsPrimary = false)
{
    public string Id { get; } = string.IsNullOrWhiteSpace(Id)
        ? throw new ArgumentException("A work area must have an identifier.", nameof(Id))
        : Id;

    public DipRect Bounds { get; } = Bounds.IsValid
        ? Bounds
        : throw new ArgumentOutOfRangeException(nameof(Bounds));
}

public readonly record struct OverlayPlacementResult(
    DipPoint Position,
    string WorkAreaId,
    bool WasAdjusted);

/// <summary>
/// Keeps a top-left overlay position inside the best available work area. All values are WPF DIPs,
/// so platform adapters must convert physical monitor coordinates before calling this module.
/// </summary>
public static class OverlayPlacement
{
    public static OverlayPlacementResult Clamp(
        DipPoint desiredPosition,
        DipSize windowSize,
        IReadOnlyList<DisplayWorkArea> workAreas,
        double marginDips = 10)
    {
        ArgumentNullException.ThrowIfNull(workAreas);
        if (!windowSize.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSize));
        }

        if (workAreas.Count == 0)
        {
            throw new ArgumentException("At least one visible work area is required.", nameof(workAreas));
        }

        if (!double.IsFinite(marginDips) || marginDips < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(marginDips));
        }

        var desiredIsValid = double.IsFinite(desiredPosition.X) && double.IsFinite(desiredPosition.Y);
        var selected = desiredIsValid
            ? SelectWorkArea(desiredPosition, windowSize, workAreas)
            : workAreas.FirstOrDefault(area => area.IsPrimary) ?? workAreas[0];
        var bounds = selected.Bounds;
        var horizontalMargin = windowSize.Width + 2 * marginDips <= bounds.Width ? marginDips : 0;
        var verticalMargin = windowSize.Height + 2 * marginDips <= bounds.Height ? marginDips : 0;
        var minimumLeft = bounds.Left + horizontalMargin;
        var minimumTop = bounds.Top + verticalMargin;
        var maximumLeft = Math.Max(minimumLeft, bounds.Right - windowSize.Width - horizontalMargin);
        var maximumTop = Math.Max(minimumTop, bounds.Bottom - windowSize.Height - verticalMargin);
        var fallback = new DipPoint(maximumLeft, minimumTop);
        var source = desiredIsValid ? desiredPosition : fallback;
        var clamped = new DipPoint(
            Math.Clamp(source.X, minimumLeft, maximumLeft),
            Math.Clamp(source.Y, minimumTop, maximumTop));

        return new OverlayPlacementResult(clamped, selected.Id, clamped != desiredPosition);
    }

    private static DisplayWorkArea SelectWorkArea(
        DipPoint desiredPosition,
        DipSize windowSize,
        IReadOnlyList<DisplayWorkArea> workAreas)
    {
        var desiredBounds = new DipRect(
            desiredPosition.X,
            desiredPosition.Y,
            windowSize.Width,
            windowSize.Height);
        var center = new DipPoint(
            desiredPosition.X + windowSize.Width / 2,
            desiredPosition.Y + windowSize.Height / 2);
        var containing = workAreas.FirstOrDefault(area => area.Bounds.Contains(center));
        if (containing is not null)
        {
            return containing;
        }

        var byIntersection = workAreas
            .Select(area => (Area: area.Bounds.IntersectionArea(desiredBounds), WorkArea: area))
            .OrderByDescending(candidate => candidate.Area)
            .ThenByDescending(candidate => candidate.WorkArea.IsPrimary)
            .First();
        if (byIntersection.Area > 0)
        {
            return byIntersection.WorkArea;
        }

        return workAreas
            .OrderBy(area => area.Bounds.DistanceSquaredTo(center))
            .ThenByDescending(area => area.IsPrimary)
            .First();
    }
}
