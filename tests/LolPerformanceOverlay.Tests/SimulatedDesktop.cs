using LolPerformanceOverlay.Core.Interaction;

namespace LolPerformanceOverlay.Tests;

/// <summary>
/// A small model of how Windows treats a PerMonitorV2 window: after every move or resize the
/// monitor holding most of the window decides its DPI, and a change sends WM_DPICHANGED with a
/// suggested rectangle that keeps the top-left and has the new DPI's size. Whatever rectangle the
/// app hands back is applied and checked again at once, which is how a bad answer loops forever
/// inside a single SetWindowPos. <see cref="Rewrite"/> = false hands the suggestion back
/// unchanged, which is what the window did before the hook existed.
/// </summary>
internal sealed class SimulatedDesktop(
    IReadOnlyList<PhysicalDisplayWorkArea> displays,
    OverlayPlacementController controller,
    DipSize content,
    PixelPoint topLeft,
    uint dpi)
{
    public const int LoopCap = 50;

    public IReadOnlyList<PhysicalDisplayWorkArea> Displays { get; } = displays;

    public OverlayPlacementController Controller { get; } = controller;

    public DipSize Content { get; private set; } = content;

    public uint Dpi { get; private set; } = dpi;

    public PixelRect Rect { get; private set; } = new(
        topLeft.X,
        topLeft.Y,
        (int)Math.Ceiling(content.Width * dpi / 96d),
        (int)Math.Ceiling(content.Height * dpi / 96d));

    public PixelPoint TopLeft => new(Rect.X, Rect.Y);

    public bool Rewrite { get; init; } = true;

    public int DpiChanges { get; private set; }

    public int LongestChain { get; private set; }

    public bool LoopDetected { get; private set; }

    /// <summary>The content changed size and WPF resized the window, keeping its top-left.</summary>
    public void Resize(DipSize newContent)
    {
        Content = newContent;
        Apply(new PixelRect(Rect.X, Rect.Y, Width(newContent, Dpi), Height(newContent, Dpi)));
    }

    public void MoveTo(PixelPoint position) =>
        Apply(new PixelRect(position.X, position.Y, Rect.Width, Rect.Height));

    /// <summary>Applies a move the way the window's ApplyMove does.</summary>
    public void ApplyMove(PlacementMove move)
    {
        Controller.EndDpiChain();
        if (move.Staging is { } staging)
        {
            MoveTo(staging);
            Controller.EndDpiChain();
        }

        MoveTo(move.Final);
        Controller.CompleteMove(Dpi, TopLeft);
    }

    /// <summary>Drags by whole pixels along a straight line from one cursor point to another.</summary>
    public void DragAlong(PixelPoint from, PixelPoint to)
    {
        var steps = Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));
        for (var step = 1; step <= steps; step++)
        {
            var cursor = new PixelPoint(
                from.X + (to.X - from.X) * step / steps,
                from.Y + (to.Y - from.Y) * step / steps);
            Controller.EndDpiChain();
            MoveTo(Controller.DragTo(cursor));
        }
    }

    private void Apply(PixelRect rect)
    {
        Rect = rect;
        var chain = 0;
        while (true)
        {
            var owner = OverlayPlacement.MostArea(Rect.X, Rect.Y, Rect.Width, Rect.Height, Displays);
            if (owner is null || owner.DpiX == Dpi)
            {
                break;
            }

            if (++chain > LoopCap)
            {
                LoopDetected = true;
                break;
            }

            var newDpi = owner.DpiX;
            var suggested = new PixelRect(Rect.X, Rect.Y, Width(Content, newDpi), Height(Content, newDpi));
            var answer = Rewrite
                ? Controller.OnDpiChanged(Rect, suggested, newDpi, Content, Displays).Rect
                : suggested;
            Dpi = newDpi;
            DpiChanges++;
            Rect = answer;
        }

        LongestChain = Math.Max(LongestChain, chain);

        // The window queues EndDpiChain on the first message of a chain; the chain is synchronous,
        // so that queued call runs right after it.
        Controller.EndDpiChain();
    }

    private static int Width(DipSize size, uint dpi) => (int)Math.Ceiling(size.Width * dpi / 96d);

    private static int Height(DipSize size, uint dpi) => (int)Math.Ceiling(size.Height * dpi / 96d);
}
