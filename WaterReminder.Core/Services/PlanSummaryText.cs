namespace WaterReminder.Services;

/// <summary>
/// Live, human-readable explanation of the current settings for the settings page.
/// It deliberately follows <see cref="WaterCalculator.CalculateDynamic"/> instead of listing fixed times.
/// </summary>
public static class PlanSummaryText
{
    public static string Describe(UserSettings s, DailyRecord record, DateTime now, DateTime? lastReminderAt = null)
    {
        if (s.DailyGoalMl <= 0 || WaterCalculator.WorkCupMl(s) <= 0)
            return "请先设置有效的每日目标和杯量。";

        var remaining = Math.Max(0, s.DailyGoalMl - record.ConsumedMl);
        if (remaining == 0)
            return $"今日目标已完成（{record.ConsumedMl:N0} / {s.DailyGoalMl:N0} ml）。";
        if (!WaterCalculator.IsActiveDay(s, now))
            return "今天休息，不发送提醒。";
        if (s.RemindersPaused)
            return "提醒已暂停；恢复后按当时的饮水进度重新计算下次提醒。";

        var plan = WaterCalculator.CalculateDynamic(s, record, now, lastReminderAt);
        var phase = WaterCalculator.PhaseAt(s, now);

        if (phase == WaterCalculator.DayPhase.NoDrinkWindow)
            return $"已进入下班前不喝水时段，今天不再提醒；还差 {remaining:N0} ml。";

        if (phase == WaterCalculator.DayPhase.AfterWork)
        {
            if (!s.MobilePushEnabled)
                return $"手机推送已关闭；还差 {remaining:N0} ml。";
            if (plan.NextReminder is not { } evening)
                return $"今日提醒已结束；还差 {remaining:N0} ml。";
            var timing = plan.SuggestedMl > 0 ? "现在该喝水了" : $"下次 {evening:HH:mm}";
            return $"下班后改用{PushWardMessageBuilder.ChannelLabel}：{timing}，按家庭杯 {WaterCalculator.HomeCupMl(s)} ml 补充，"
                 + $"约每 {FormatInterval(plan.Interval)} 一次，{s.QuietStart.ToString(@"hh\:mm")} 后免打扰。";
        }

        // Work hours: pace the work portion only. One home cup stays for the evening plan.
        var workLeft = WaterCalculator.WorkPortionMl(s, remaining);
        var home = WaterCalculator.HomeCupMl(s);
        var reservesEvening = s.QuietStart > s.WorkEnd;
        var desktop = plan.NextReminder switch
        {
            null when workLeft <= 0 && reservesEvening => "下班后再提醒",
            null => "今天不再提醒",
            _ when plan.SuggestedMl > 0 => "现在该喝水了",
            { } next => $"下次提醒 {next:HH:mm}",
        };
        string text;
        if (workLeft <= 0 && reservesEvening)
            text = $"还差 {remaining:N0} ml，工作时段不再提醒，留给下班后";
        else
        {
            text = $"还差 {remaining:N0} ml，工作时段约 {plan.TotalReminders} 次（工作杯 {WaterCalculator.WorkCupMl(s)} ml";
            if (reservesEvening) text += $"，预留家庭杯 {home:N0} ml 到下班后";
            text += "）";
        }
        text += $"；{desktop}";
        if (plan.Interval > TimeSpan.Zero) text += $"，间隔约 {FormatInterval(plan.Interval)}";
        text += "。工作时段桌面和手机会同时提醒";
        if (s.MobilePushEnabled)
            text += $"；下班后若未达标，按家庭杯 {home:N0} ml 只提醒手机";
        else
            text += "；手机推送已关闭，下班后不再提醒";
        return text + "。";
    }

    private static string FormatInterval(TimeSpan interval)
        => interval.TotalMinutes < 1 ? "不到 1 分钟" : $"{Math.Round(interval.TotalMinutes):0} 分钟";
}
