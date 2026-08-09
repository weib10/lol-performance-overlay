using LolPerformanceOverlay.Core;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class GlobalHotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+O")]
    [InlineData("Alt+F8")]
    [InlineData("Win+Shift+L")]
    public void ValidHotkeysParse(string value)
    {
        Assert.True(HotkeyGestureParser.TryParse(value, out var gesture));
        Assert.NotEqual(HotkeyModifiers.None, gesture.Modifiers);
        Assert.False(string.IsNullOrWhiteSpace(gesture.KeyToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("O")]
    [InlineData("Banana+O")]
    public void InvalidHotkeysAreRejected(string value)
    {
        Assert.False(HotkeyGestureParser.TryParse(value, out _));
    }
}
