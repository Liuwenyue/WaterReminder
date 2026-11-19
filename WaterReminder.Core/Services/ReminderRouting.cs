namespace WaterReminder.Services;

/// <summary>
/// Phone delivery. WeChat replaces PushWard once the server has an app secret and template.
/// Until then, an already configured PushWard token keeps working.
/// </summary>
public enum PhoneChannel
{
    None,
    PushWard,
    WeChat,
}

public static class ReminderRouting
{
    public static PhoneChannel Choose(bool mobileEnabled, bool pushWardConfigured, bool wechatConfigured)
    {
        if (!mobileEnabled) return PhoneChannel.None;
        if (wechatConfigured) return PhoneChannel.WeChat;
        return pushWardConfigured ? PhoneChannel.PushWard : PhoneChannel.None;
    }

    public static bool ShouldPublishPushWard(UserSettings settings, bool pushWardConfigured)
        => Choose(settings.MobilePushEnabled, pushWardConfigured, wechatConfigured: false) == PhoneChannel.PushWard;
}
