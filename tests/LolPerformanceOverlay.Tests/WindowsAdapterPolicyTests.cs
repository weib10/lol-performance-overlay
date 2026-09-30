using LolPerformanceOverlay.Core.Interaction;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class WindowsAdapterPolicyTests
{
    [Fact]
    public void PositionLockAddsAndUnlockRemovesWholeWindowTransparentInputStyle()
    {
        const int baseStyle = 0x08000080;

        var locked = OverlayNativeStylePolicy.WithPositionLock(baseStyle, positionLocked: true);
        var unlocked = OverlayNativeStylePolicy.WithPositionLock(locked, positionLocked: false);

        Assert.NotEqual(0, locked & OverlayNativeStylePolicy.TransparentInputStyle);
        Assert.Equal(baseStyle, unlocked);
    }
}
