namespace WaterReminder.Services;

/// <summary>
/// Pure routing for the phone channel. The only channel is PushWard, and only when
/// push is enabled and a token is available. No WPF dependency.
/// </summary>
public static class ReminderRouting
{
    public static bool ShouldPublishPushWard(UserSettings settings, bool pushWardConfigured)
        => settings.MobilePushEnabled && pushWardConfigured;
}
