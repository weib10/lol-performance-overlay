namespace LolPerformanceOverlay.Core;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008
}

public sealed record HotkeyGesture(HotkeyModifiers Modifiers, string KeyToken);

public static class HotkeyGestureParser
{
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || text.TrimEnd().EndsWith('+'))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        foreach (var token in parts[..^1])
        {
            var modifier = token.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "SHIFT" => HotkeyModifiers.Shift,
                "ALT" => HotkeyModifiers.Alt,
                "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
                _ => HotkeyModifiers.None
            };
            if (modifier == HotkeyModifiers.None || modifiers.HasFlag(modifier))
            {
                return false;
            }

            modifiers |= modifier;
        }

        var keyToken = parts[^1];
        if (modifiers == HotkeyModifiers.None || string.IsNullOrWhiteSpace(keyToken))
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, keyToken);
        return true;
    }
}
