namespace WaterReminder;

public sealed class UserSettings
{
    public int DailyGoalMl { get; set; } = 3000;
    /// <summary>Legacy single-cup value. Kept in sync with <see cref="WorkCupCapacityMl"/> so old JSON still loads.</summary>
    public int CupCapacityMl { get; set; } = 370;
    private int? _workCupCapacityMl;
    /// <summary>Work-hours cup. When the new field is unset (older settings.json), falls back to the legacy cup.</summary>
    public int WorkCupCapacityMl { get => _workCupCapacityMl ?? CupCapacityMl; set => _workCupCapacityMl = value; }
    /// <summary>Cup used outside work hours (evening phone reminders and desktop clicks).</summary>
    public int HomeCupCapacityMl { get; set; } = 800;
    internal bool WorkCupCapacityWasSet => _workCupCapacityMl.HasValue;
    public TimeSpan WorkStart { get; set; } = new(8, 30, 0);
    public TimeSpan WorkEnd { get; set; } = new(17, 30, 0);
    public bool LunchEnabled { get; set; } = true;
    public TimeSpan LunchStart { get; set; } = new(11, 50, 0);
    public TimeSpan LunchEnd { get; set; } = new(13, 30, 0);
    /// <summary>No drinking in the last N minutes before WorkEnd.</summary>
    public int NoDrinkBeforeEndMinutes { get; set; } = 30;
    /// <summary>Minimum minutes between reminders. The calculator used a fixed 20 until this became configurable.</summary>
    public int MinReminderGapMinutes { get; set; } = 20;
    /// <summary>Start of complete silence after work (also the end of evening push reminders).</summary>
    public TimeSpan QuietStart { get; set; } = new(23, 0, 0);
    public bool[] ActiveDays { get; set; } = [true, true, true, true, true, false, false];
    // Phone push is PushWard only. The headless server reads the token from
    // WATERREMINDER_PUSHWARD_TOKEN or from its data directory (never from the Windows client).
    // The legacy WPF app still loads it from %LocalAppData%\WaterReminder\settings.json.
    // Unknown keys in older settings.json files (removed ntfy / Pushover fields) are ignored
    // on load and are not written back. Never commit the token.
    public bool MobilePushEnabled { get; set; } = true;
    /// <summary>PushWard integration token. Server data directory or environment only; the tray client never sees it.</summary>
    public string PushWardApiToken { get; set; } = "";
    public bool SoundEnabled { get; set; } = true;
    public bool AutoStart { get; set; } = true;
    public bool FloatingWindow { get; set; } = true;
    public bool FirstRun { get; set; } = true;
    public bool RemindersPaused { get; set; }
    public DateTime? PauseUntil { get; set; }
    public bool CompactMode { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    /// <summary>Where the user last dragged the compact window (DIPs).</summary>
    public double? CompactLeft { get; set; }
    public double? CompactTop { get; set; }
    /// <summary>Where the user last dragged the expanded window (DIPs).</summary>
    public double? ExpandedLeft { get; set; }
    public double? ExpandedTop { get; set; }
}

public sealed class DailyRecord
{
    public string Date { get; set; } = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
    public List<WaterEvent> Events { get; set; } = [];
    /// <summary>Last reminder fired on this day. Persisted so a restart does not break the minimum gap.</summary>
    public DateTime? LastReminderAt { get; set; }
    public int ConsumedMl => Math.Max(0, Events.Sum(x => x.AmountMl));
}

public sealed class WaterEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime At { get; set; } = DateTime.Now;
    public int AmountMl { get; set; }
    public string Source { get; set; } = "desktop";
}

public sealed record ReminderPlan(int TotalReminders, TimeSpan Interval, DateTime? NextReminder, int DueMl, int SuggestedMl);
