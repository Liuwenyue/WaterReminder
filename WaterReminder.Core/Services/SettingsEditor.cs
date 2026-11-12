using System.Globalization;
using System.Text.Json;

namespace WaterReminder.Services;

/// <summary>Field errors for the desktop settings window. Empty strings mean that field is fine.</summary>
public sealed class SettingsValidation
{
    public string? GoalError { get; init; }
    public string? TimeError { get; init; }
    public string? LunchError { get; init; }
    public string? NoDrinkError { get; init; }
    public string? QuietError { get; init; }
    public string? GapError { get; init; }
    public string? DaysError { get; init; }

    public bool Ok => GoalError is null && TimeError is null && LunchError is null
        && NoDrinkError is null && QuietError is null && GapError is null && DaysError is null;

    public string? FirstError => GoalError ?? TimeError ?? LunchError ?? NoDrinkError ?? QuietError ?? GapError ?? DaysError;
}

/// <summary>
/// Validates and applies the reminder fields the desktop settings window edits.
/// Never reads or writes the phone token or the client API token.
/// </summary>
public static class SettingsEditor
{
    public const string SecretRejected = "不能在设置里提交密钥。";
    public const string Unreadable = "设置内容无法识别。";

    private static readonly string[] SecretNames =
    [
        "pushwardapitoken", "apitoken", "token", "password", "secret", "authorization",
    ];

    public static SettingsUpdate FromResponse(ClientSettingsResponse settings) => new()
    {
        DailyGoalMl = settings.DailyGoalMl,
        WorkCupMl = settings.WorkCupMl,
        HomeCupMl = settings.HomeCupMl,
        WorkStart = settings.WorkStart,
        WorkEnd = settings.WorkEnd,
        LunchEnabled = settings.LunchEnabled,
        LunchStart = settings.LunchStart,
        LunchEnd = settings.LunchEnd,
        NoDrinkBeforeEndMinutes = settings.NoDrinkBeforeEndMinutes,
        QuietStart = settings.QuietStart,
        MinReminderGapMinutes = settings.MinReminderGapMinutes,
        MobilePushEnabled = settings.MobilePushEnabled,
        ActiveDays = settings.ActiveDays is { Length: 7 } days ? (bool[])days.Clone() : [],
    };

    public static bool TryParse(string? json, out SettingsUpdate? update, out string? error)
    {
        update = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = Unreadable;
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = Unreadable;
                return false;
            }

            if (ContainsSecret(doc.RootElement))
            {
                error = SecretRejected;
                return false;
            }

            update = JsonSerializer.Deserialize<SettingsUpdate>(json, ReminderApiClient.Json);
            if (update is null)
            {
                error = Unreadable;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = Unreadable;
            return false;
        }
    }

    public static SettingsValidation Validate(SettingsUpdate update)
    {
        string? goal = null;
        if (update.DailyGoalMl is < 100 or > 10000) goal = "每日目标需为 100–10000 ml 的整数。";
        else if (update.WorkCupMl is < 50 or > 3000 || update.HomeCupMl is < 50 or > 3000)
            goal = "工作杯和家庭杯容量需为 50–3000 ml 的整数。";

        var startOk = TryParseClock(update.WorkStart, out var workStart);
        var endOk = TryParseClock(update.WorkEnd, out var workEnd);
        string? time = null;
        if (!startOk || !endOk) time = "请输入有效时间，例如 09:00。";
        else if (workEnd <= workStart) time = "下班时间必须晚于上班时间。";

        var lunchStartOk = TryParseClock(update.LunchStart, out var lunchStart);
        var lunchEndOk = TryParseClock(update.LunchEnd, out var lunchEnd);
        string? lunch = null;
        if (update.LunchEnabled && time is null)
        {
            if (!lunchStartOk || !lunchEndOk) lunch = "请输入有效的午休时间。";
            else if (lunchEnd <= lunchStart) lunch = "午休结束必须晚于午休开始。";
            else if (lunchStart < workStart || lunchEnd > workEnd) lunch = "午休需在上下班时间之内。";
        }
        else if (!lunchStartOk || !lunchEndOk)
        {
            lunch = "请输入有效的午休时间。";
        }

        string? gap = update.MinReminderGapMinutes is < 5 or > 180 ? "最小间隔需为 5–180 分钟。" : null;

        string? noDrink = null;
        if (update.NoDrinkBeforeEndMinutes is < 0 or > 240) noDrink = "请输入 0–240 分钟。";
        else if (time is null && update.NoDrinkBeforeEndMinutes >= (workEnd - workStart).TotalMinutes)
            noDrink = "不喝水时段必须短于上班时长。";

        string? quiet = null;
        var quietOk = TryParseClock(update.QuietStart, out var quietStart);
        if (!quietOk) quiet = "请输入有效的免打扰开始时间。";
        else if (time is null && quietStart <= workEnd) quiet = "免打扰开始必须晚于下班时间。";

        string? days = update.ActiveDays is { Length: 7 } list && list.Any(x => x) ? null : "至少选择一天。";

        if (goal is null && time is null && lunch is null && noDrink is null && quiet is null && gap is null && days is null)
        {
            var probe = new UserSettings();
            Apply(probe, update, workStart, workEnd, lunchStart, lunchEnd, quietStart);
            if (WaterCalculator.Schedule(probe, DateTime.Today).Count == 0)
                noDrink = "去掉午休和下班前时段后没有可喝水的时间。";
        }

        return new SettingsValidation
        {
            GoalError = goal,
            TimeError = time,
            LunchError = lunch,
            NoDrinkError = noDrink,
            QuietError = quiet,
            GapError = gap,
            DaysError = days,
        };
    }

    /// <summary>Copies the editable fields onto <paramref name="target"/>. Leaves the phone token untouched.</summary>
    public static bool TryApply(UserSettings target, SettingsUpdate update, out string? error)
    {
        var validation = Validate(update);
        error = validation.FirstError;
        if (!validation.Ok || error is not null) return false;
        if (!TryParseClock(update.WorkStart, out var workStart)
            || !TryParseClock(update.WorkEnd, out var workEnd)
            || !TryParseClock(update.LunchStart, out var lunchStart)
            || !TryParseClock(update.LunchEnd, out var lunchEnd)
            || !TryParseClock(update.QuietStart, out var quietStart))
        {
            error = "请输入有效时间，例如 09:00。";
            return false;
        }

        var token = target.PushWardApiToken;
        Apply(target, update, workStart, workEnd, lunchStart, lunchEnd, quietStart);
        target.PushWardApiToken = token;
        return true;
    }

    private static void Apply(UserSettings target, SettingsUpdate update, TimeSpan workStart, TimeSpan workEnd, TimeSpan lunchStart, TimeSpan lunchEnd, TimeSpan quietStart)
    {
        target.DailyGoalMl = update.DailyGoalMl;
        target.WorkCupCapacityMl = update.WorkCupMl;
        target.CupCapacityMl = update.WorkCupMl;
        target.HomeCupCapacityMl = update.HomeCupMl;
        target.WorkStart = workStart;
        target.WorkEnd = workEnd;
        target.LunchEnabled = update.LunchEnabled;
        target.LunchStart = lunchStart;
        target.LunchEnd = lunchEnd;
        target.NoDrinkBeforeEndMinutes = update.NoDrinkBeforeEndMinutes;
        target.QuietStart = quietStart;
        target.MinReminderGapMinutes = update.MinReminderGapMinutes;
        target.MobilePushEnabled = update.MobilePushEnabled;
        target.ActiveDays = (bool[])update.ActiveDays.Clone();
    }

    public static bool TryParseClock(string? text, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim().Replace('：', ':');
        if (!TimeSpan.TryParseExact(value, ["h\\:mm", "hh\\:mm", "h\\:mm\\:ss", "hh\\:mm\\:ss"], CultureInfo.InvariantCulture, out time))
            return false;
        return time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
    }

    private static bool ContainsSecret(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsSecretName(property.Name) || ContainsSecret(property.Value)) return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsSecret(item)) return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool IsSecretName(string name)
    {
        var compact = name.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (SecretNames.Contains(compact, StringComparer.OrdinalIgnoreCase)) return true;
        return compact.Contains("pushover", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("ntfy", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("pushward", StringComparison.OrdinalIgnoreCase);
    }
}
