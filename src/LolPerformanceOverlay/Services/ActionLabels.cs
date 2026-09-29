namespace LolPerformanceOverlay.Services;

/// <summary>
/// Names of the actions shared by the tray menu, the settings window, and 先看這裡.html. Friends
/// follow the guide by these exact words, so a Windows test checks that every name the guide
/// quotes for the tray is one of these.
/// </summary>
internal static class ActionLabels
{
    public const string ShowOrCycle = "顯示／切換";
    public const string ResetPosition = "重設 Overlay 位置";
    public const string LockPosition = "鎖定 Overlay 位置";
    public const string Settings = "設定";
    public const string StartWithWindows = "登入 Windows 後自動啟動";
    public const string Exit = "結束";

    public static IReadOnlyList<string> TrayMenu { get; } =
        [ShowOrCycle, ResetPosition, LockPosition, Settings, StartWithWindows, Exit];
}
