using LolPerformanceOverlay.Core;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class HotkeyRegistrationPolicyTests
{
    [Fact]
    public void RequestedGestureWinsWithoutTryingFallback()
    {
        var attempts = new List<string>();

        var result = HotkeyRegistrationPolicy.Register("Ctrl+Shift+O", "Ctrl+Shift+F9", gesture =>
        {
            attempts.Add(gesture);
            return true;
        });

        Assert.Equal(HotkeyRegistrationStatus.RequestedRegistered, result.Status);
        Assert.Equal("Ctrl+Shift+O", result.RegisteredGesture);
        Assert.Equal(["Ctrl+Shift+O"], attempts);
    }

    [Fact]
    public void ReportsFallbackOnlyWhenFallbackActuallyRegisters()
    {
        var result = HotkeyRegistrationPolicy.Register(
            "Ctrl+Shift+O",
            "Ctrl+Shift+F9",
            gesture => gesture.EndsWith("F9", StringComparison.Ordinal));

        Assert.Equal(HotkeyRegistrationStatus.FallbackRegistered, result.Status);
        Assert.Equal("Ctrl+Shift+F9", result.RegisteredGesture);
    }

    [Fact]
    public void BothFailuresAreExplicitAndNeverClaimFallback()
    {
        var result = HotkeyRegistrationPolicy.Register(
            "Ctrl+Shift+O",
            "Ctrl+Shift+F9",
            _ => false);

        Assert.Equal(HotkeyRegistrationStatus.Unavailable, result.Status);
        Assert.Null(result.RegisteredGesture);
    }
}
