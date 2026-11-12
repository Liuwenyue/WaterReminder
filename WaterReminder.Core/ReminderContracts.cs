namespace WaterReminder;

/// <summary>Today's status for the Windows client. Contains no PushWard credential.</summary>
public sealed class StatusResponse
{
    public string Date { get; set; } = "";
    public string TimeZone { get; set; } = Services.ShanghaiClock.Id;
    public string Now { get; set; } = "";
    public int ConsumedMl { get; set; }
    public int GoalMl { get; set; }
    public int SuggestedMl { get; set; }
    public string? NextReminder { get; set; }
    public string Phase { get; set; } = "";
    public bool ShowDesktop { get; set; }
    /// <summary>True when this clock time is a phone reminder window (work or after work, before quiet).</summary>
    public bool PushWindow { get; set; }
    public bool RemindersPaused { get; set; }
    public string StatusText { get; set; } = "";
    public int WorkCupMl { get; set; }
    public int HomeCupMl { get; set; }
    public string WorkStart { get; set; } = "";
    public string WorkEnd { get; set; } = "";
    public string LunchStart { get; set; } = "";
    public string LunchEnd { get; set; } = "";
    public string QuietStart { get; set; } = "";
    public int MinReminderGapMinutes { get; set; }
    public DesktopAlertResponse? DesktopAlert { get; set; }
}

public sealed class DesktopAlertResponse
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public int SuggestedMl { get; set; }
}

/// <summary>Settings the Windows client is allowed to cache. Secrets are omitted.</summary>
public sealed class ClientSettingsResponse
{
    public int DailyGoalMl { get; set; }
    public int WorkCupMl { get; set; }
    public int HomeCupMl { get; set; }
    public string WorkStart { get; set; } = "";
    public string WorkEnd { get; set; } = "";
    public bool LunchEnabled { get; set; }
    public string LunchStart { get; set; } = "";
    public string LunchEnd { get; set; } = "";
    public int NoDrinkBeforeEndMinutes { get; set; }
    public string QuietStart { get; set; } = "";
    public int MinReminderGapMinutes { get; set; }
    public bool MobilePushEnabled { get; set; }
    public bool[] ActiveDays { get; set; } = [];
    public bool RemindersPaused { get; set; }
    public string? PauseUntil { get; set; }
}

public sealed class DrinkRequest
{
    public int? AmountMl { get; set; }
    public string? Source { get; set; }
}

public sealed class DrinkResponse
{
    public int RecordedMl { get; set; }
    public int ConsumedMl { get; set; }
    public int GoalMl { get; set; }
    public string Title { get; set; } = "已记录";
    public string Message { get; set; } = "";
}

public sealed class AckRequest
{
    public string? Id { get; set; }
}

/// <summary>Fields the desktop settings window may change. No credentials.</summary>
public sealed class SettingsUpdate
{
    public int DailyGoalMl { get; set; }
    public int WorkCupMl { get; set; }
    public int HomeCupMl { get; set; }
    public string WorkStart { get; set; } = "";
    public string WorkEnd { get; set; } = "";
    public bool LunchEnabled { get; set; }
    public string LunchStart { get; set; } = "";
    public string LunchEnd { get; set; } = "";
    public int NoDrinkBeforeEndMinutes { get; set; }
    public string QuietStart { get; set; } = "";
    public int MinReminderGapMinutes { get; set; }
    public bool MobilePushEnabled { get; set; }
    public bool[] ActiveDays { get; set; } = [];
    public bool RemindersPaused { get; set; }
    public string? PauseUntil { get; set; }
}
