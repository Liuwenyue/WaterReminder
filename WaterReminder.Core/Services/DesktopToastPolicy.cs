namespace WaterReminder.Services;

/// <summary>
/// Decides whether the Windows tray client may show a desktop toast.
/// After work, lunch, quiet hours, and a repeated alert id stay silent.
/// The client does not compute the schedule; it only applies this gate to the server status.
/// </summary>
public static class DesktopToastPolicy
{
    public static bool ShouldToast(bool showDesktop, string? phase, string? alertId, string? lastShownId)
    {
        if (!showDesktop) return false;
        if (!string.Equals(phase, nameof(WaterCalculator.DayPhase.Working), StringComparison.Ordinal))
            return false;
        if (string.IsNullOrWhiteSpace(alertId)) return false;
        if (string.Equals(alertId.Trim(), lastShownId, StringComparison.Ordinal)) return false;
        return true;
    }
}
