namespace WaterReminder.Services;

[Flags]
public enum PhoneChannel
{
    None,
    PushWard,
    WeChat,
}

public static class ReminderRouting
{
    public static PhoneChannel Choose(bool mobileEnabled, bool pushWardConfigured, bool wechatReady, PhoneDeliveryMode mode = PhoneDeliveryMode.Auto)
    {
        if (!mobileEnabled) return PhoneChannel.None;
        var push = pushWardConfigured ? PhoneChannel.PushWard : PhoneChannel.None;
        var wechat = wechatReady ? PhoneChannel.WeChat : PhoneChannel.None;
        return mode switch
        {
            PhoneDeliveryMode.WeChat => wechat,
            PhoneDeliveryMode.PushWard => push,
            PhoneDeliveryMode.Both => wechat | push,
            _ => wechatReady ? wechat : push,
        };
    }

    public static bool ShouldPublishPushWard(UserSettings settings, bool pushWardConfigured)
        => settings.MobilePushEnabled && pushWardConfigured && settings.PhoneMode != PhoneDeliveryMode.WeChat;
}
