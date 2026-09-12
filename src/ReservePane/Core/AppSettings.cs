namespace ReservePane.Core;

public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Custom,
}

public sealed record OverlayPosition(double X, double Y);

public sealed record AppSettings(
    TimeSpan PollInterval,
    TimeSpan IdleInterval,
    bool OverlayVisible,
    OverlayCorner OverlayCorner,
    string? OverlayMonitorId,
    OverlayPosition? OverlayPosition,
    string Hotkey,
    double WarningPercent,
    double CriticalPercent,
    bool Autostart)
{
    public static AppSettings Default { get; } = new(
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(5),
        false,
        OverlayCorner.BottomRight,
        null,
        null,
        "Ctrl+Alt+A",
        80,
        95,
        false);
}
