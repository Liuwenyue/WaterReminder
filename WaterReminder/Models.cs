namespace WaterReminder;

public sealed class UserSettings
{
    public int DailyGoalMl { get; set; } = 2000;
    public int CupCapacityMl { get; set; } = 300;
    public TimeSpan WorkStart { get; set; } = new(9, 0, 0);
    public TimeSpan WorkEnd { get; set; } = new(18, 0, 0);
    public bool LunchEnabled { get; set; } = true;
    public TimeSpan LunchStart { get; set; } = new(12, 0, 0);
    public TimeSpan LunchEnd { get; set; } = new(13, 30, 0);
    /// <summary>No drinking in the last N minutes before WorkEnd.</summary>
    public int NoDrinkBeforeEndMinutes { get; set; } = 30;
    public bool[] ActiveDays { get; set; } = [true, true, true, true, true, false, false];
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
