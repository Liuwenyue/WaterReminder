namespace WaterReminder.Services;

[Flags]
public enum PhoneChannel
{
    None,
    PushWard,
}

public static class ReminderRouting
{
    public static PhoneChannel Choose(bool mobileEnabled, bool pushWardConfigured)
    {
        if (!mobileEnabled || !pushWardConfigured) return PhoneChannel.None;
        return PhoneChannel.PushWard;
    }

    public static bool ShouldPublishPushWard(UserSettings settings, bool pushWardConfigured)
        => settings.MobilePushEnabled && pushWardConfigured;
}
