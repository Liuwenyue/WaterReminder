namespace WaterReminder.Services;

/// <summary>
/// Owns today's record and every way it can change. Each mutation saves and raises <see cref="Changed"/>,
/// so the main window, tray tooltip and reminder timer recompute the dynamic next reminder immediately.
/// No WPF dependencies (unit-tested).
/// </summary>
public sealed class ProgressTracker
{
    private readonly Func<DateOnly, DailyRecord> _load;
    private readonly Action<DailyRecord> _save;
    private readonly Func<DateTime> _clock;

    public ProgressTracker(UserSettings settings, Func<DateOnly, DailyRecord> load, Action<DailyRecord> save, Func<DateTime>? clock = null)
    {
        Settings = settings;
        _load = load;
        _save = save;
        _clock = clock ?? (() => DateTime.Now);
        var now = _clock();
        Today = _load(DateOnly.FromDateTime(now));
        // Restore after a restart so the last reminder still restarts today's countdown.
        LastReminderAt = RestoreLastReminder(Today, now);
    }

    public UserSettings Settings { get; }
    public DailyRecord Today { get; private set; }
    /// <summary>Time the last reminder fired today (restarts the countdown when the user ignores it).</summary>
    public DateTime? LastReminderAt { get; private set; }
    public DateTime Now => _clock();

    /// <summary>Raised after anything that affects the next reminder (total changed, settings saved, reminder fired).</summary>
    public event Action? Changed;

    public ReminderPlan Plan() => WaterCalculator.CalculateDynamic(Settings, Today, Now, LastReminderAt);

    /// <summary>Reload from disk (another window may have saved). Raises Changed only when the total or events differ.</summary>
    public void Reload()
    {
        var now = Now;
        var fresh = _load(DateOnly.FromDateTime(now));
        // A different day's record has no (or a stale) reminder time: clear it, same as before.
        var restoredLast = RestoreLastReminder(fresh, now);
        var changed = fresh.Date != Today.Date || Signature(fresh) != Signature(Today) || restoredLast != LastReminderAt;
        Today = fresh;
        LastReminderAt = restoredLast;
        if (changed) Changed?.Invoke();
    }

    private static DateTime? RestoreLastReminder(DailyRecord record, DateTime now)
        => record.LastReminderAt is { } at && at.Date == now.Date ? at : null;

    private static string Signature(DailyRecord r) => string.Join(";", r.Events.Select(e => $"{e.Id}:{e.AmountMl}:{e.At.Ticks}"));

    /// <summary>Records a drink (cup button, keyboard, custom amount). Returns the amount actually recorded.</summary>
    public int Drink(int amount, string source)
    {
        if (amount == 0) return 0;
        Reload();
        if (amount > 0)
        {
            var remaining = Math.Max(0, Settings.DailyGoalMl - Today.ConsumedMl);
            if (remaining == 0) return 0;
            amount = Math.Min(amount, remaining);
        }
        else
        {
            amount = Math.Max(amount, -Today.ConsumedMl);
            if (amount == 0) return 0;
        }
        Today.Events.Add(new WaterEvent { AmountMl = amount, Source = source, At = Now });
        Commit();
        return amount;
    }

    /// <summary>Inline correction: makes today's total equal <paramref name="newTotal"/>. Returns the delta.</summary>
    public int Correct(int newTotal)
    {
        Reload();
        var delta = WaterCalculator.AdjustmentFor(Today, newTotal);
        if (delta == 0) return 0;
        Today.Events.Add(new WaterEvent { AmountMl = delta, Source = "adjust", At = Now });
        Commit();
        return delta;
    }

    public bool EditEvent(Guid id, int amount)
    {
        Reload();
        var target = Today.Events.FirstOrDefault(x => x.Id == id);
        if (target is null || target.AmountMl == amount) return false;
        target.AmountMl = amount;
        Commit();
        return true;
    }

    public bool DeleteEvent(Guid id)
    {
        Reload();
        if (Today.Events.RemoveAll(x => x.Id == id) == 0) return false;
        Commit();
        return true;
    }

    public bool ClearToday()
    {
        Reload();
        if (Today.Events.Count == 0) return false;
        Today.Events.Clear();
        Commit();
        return true;
    }

    /// <summary>Settings (goal, cup, hours, lunch, pause) were changed and saved elsewhere.</summary>
    public void SettingsChanged() => Changed?.Invoke();

    public void MarkReminded(DateTime at)
    {
        LastReminderAt = at;
        Today.LastReminderAt = at;
        _save(Today); // survive a restart: the next reminder keeps a full gap
        Changed?.Invoke();
    }

    private void Commit()
    {
        _save(Today);
        Changed?.Invoke();
    }
}
