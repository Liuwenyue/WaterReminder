namespace WaterReminder.Services;

/// <summary>User-facing label for a record's source (kept UI-free so it can be unit-tested).</summary>
public static class RecordLabels
{
    public static string Describe(string? source, int amountMl) => source switch
    {
        "cup" => "喝一杯",
        "keyboard" => "快捷键记录",
        "custom" => amountMl < 0 ? "手动修正" : "自定义",
        "adjust" => "修正",
        PushWardMessageBuilder.RecordSource => PushWardMessageBuilder.ChannelLabel,
        "toast" or "notification" => "通知中记录",
        _ => "手动记录",
    };
}
