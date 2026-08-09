namespace LolPerformanceOverlay.Core;

public enum HotkeyRegistrationStatus
{
    RequestedRegistered,
    FallbackRegistered,
    Unavailable
}

public sealed record HotkeyRegistrationResult(
    HotkeyRegistrationStatus Status,
    string? RegisteredGesture);

public static class HotkeyRegistrationPolicy
{
    public static HotkeyRegistrationResult Register(
        string requestedGesture,
        string fallbackGesture,
        Func<string, bool> tryRegister)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedGesture);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackGesture);
        ArgumentNullException.ThrowIfNull(tryRegister);

        if (tryRegister(requestedGesture))
        {
            return new HotkeyRegistrationResult(
                HotkeyRegistrationStatus.RequestedRegistered,
                requestedGesture);
        }

        if (!string.Equals(requestedGesture, fallbackGesture, StringComparison.OrdinalIgnoreCase) &&
            tryRegister(fallbackGesture))
        {
            return new HotkeyRegistrationResult(
                HotkeyRegistrationStatus.FallbackRegistered,
                fallbackGesture);
        }

        return new HotkeyRegistrationResult(HotkeyRegistrationStatus.Unavailable, null);
    }
}
