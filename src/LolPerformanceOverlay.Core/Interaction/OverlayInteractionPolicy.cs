namespace LolPerformanceOverlay.Core.Interaction;

public enum OverlayInteractionPolicy
{
    FullSurfaceGesture,
    DedicatedGripOnly
}

public enum OverlaySurfaceKind
{
    Background,
    DedicatedGrip,
    Control
}

/// <summary>
/// Headless hit-policy seam used to compare complete interaction designs before the Windows
/// adapter maps actual WPF elements to background, grip, or control surfaces.
/// </summary>
public static class OverlayInteractionPolicyRules
{
    public static bool CanStartPointerGesture(
        OverlayInteractionPolicy policy,
        OverlaySurfaceKind surface,
        bool positionLocked)
    {
        if (positionLocked || surface == OverlaySurfaceKind.Control)
        {
            return false;
        }

        return policy == OverlayInteractionPolicy.FullSurfaceGesture ||
               surface == OverlaySurfaceKind.DedicatedGrip;
    }
}
