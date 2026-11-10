namespace WaterReminder.Services;

/// <summary>
/// 提醒计时器的纯路由逻辑。手机通道只有 PushWard：开启推送且 Token 可用时才尝试发送。
/// 单独放在无 WPF 依赖的 partial 文件里，方便离线单测。
/// </summary>
public sealed partial class ReminderService
{
    public static bool ShouldPublishPushWard(UserSettings settings, bool pushWardConfigured)
        => settings.MobilePushEnabled && pushWardConfigured;
}
