using LolPerformanceOverlay.Services;
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
        Assert.True(GlobalHotkey.TryParse(value, out var modifiers, out var key));
        Assert.NotEqual(0u, modifiers);
        Assert.NotEqual(0, key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("O")]
    [InlineData("Banana+O")]
    public void InvalidHotkeysAreRejected(string value)
    {
        Assert.False(GlobalHotkey.TryParse(value, out _, out _));
    }
}
