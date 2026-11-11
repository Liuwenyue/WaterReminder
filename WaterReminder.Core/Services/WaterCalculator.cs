namespace WaterReminder.Services;

public static class WaterCalculator
{
    public sealed record ReminderSlot(DateTime At, int DueMl);

    public enum DayPhase { BeforeWork, Working, Lunch, NoDrinkWindow, AfterWork }

    /// <summary>Last moment reminders may fire (WorkEnd − no-drink window).</summary>
    public static TimeSpan Cutoff(UserSettings s) => s.WorkEnd - TimeSpan.FromMinutes(Math.Max(0, s.NoDrinkBeforeEndMinutes));

    /// <summary>Lunch clipped to [WorkStart, cutoff]; null when disabled or outside.</summary>
    public static (TimeSpan Start, TimeSpan End)? EffectiveLunch(UserSettings s)
    {
        if (!s.LunchEnabled || s.LunchEnd <= s.LunchStart) return null;
        var start = s.LunchStart < s.WorkStart ? s.WorkStart : s.LunchStart;
        var cutoff = Cutoff(s);
        var end = s.LunchEnd > cutoff ? cutoff : s.LunchEnd;
        return end > start ? (start, end) : null;
    }

    /// <summary>
    /// Today's reminder times. Available time = [WorkStart, cutoff) minus lunch, length L.
    /// With n = ceil(goal / cup), reminder k (1..n) sits at available offset k·L/(n+½),
    /// so reminders are evenly spaced and the last one lands half an interval before the cutoff.
    /// Times are rounded to 5 minutes, never inside lunch (moved to lunch end) or after the cutoff;
    /// colliding reminders are merged. Due amount grows by one cup per reminder and reaches the goal at the last one.
    /// </summary>
    public static IReadOnlyList<ReminderSlot> Schedule(UserSettings s, DateTime day)
    {
        var result = new List<ReminderSlot>();
        if (s.DailyGoalMl <= 0 || s.CupCapacityMl <= 0) return result;
        var cutoff = Cutoff(s);
        if (cutoff <= s.WorkStart) return result;
        var lunch = EffectiveLunch(s);
        var lunchLen = lunch is { } l ? l.End - l.Start : TimeSpan.Zero;
        var available = (cutoff - s.WorkStart - lunchLen).TotalMinutes;
        if (available <= 0) return result;

        var n = (int)Math.Ceiling(s.DailyGoalMl / (double)s.CupCapacityMl);
        var step = available / (n + 0.5);
        for (var k = 1; k <= n; k++)
        {
            var t = s.WorkStart + TimeSpan.FromMinutes(k * step);
            var beforeLunch = true;
            if (lunch is { } ln && t >= ln.Start) { t += lunchLen; beforeLunch = false; }
            var rounded = RoundTo5(t);
            if (lunch is { } lu)
            {
                if (beforeLunch && rounded >= lu.Start) rounded = FloorTo5(t);          // don't round into lunch
                if (rounded >= lu.Start && rounded < lu.End) rounded = lu.End;           // shift to lunch end
            }
            if (rounded >= cutoff) rounded = FloorTo5(cutoff - TimeSpan.FromMinutes(1));
            if (rounded <= s.WorkStart) rounded = s.WorkStart + TimeSpan.FromMinutes(5);
            var due = k == n ? s.DailyGoalMl : Math.Min(s.DailyGoalMl, k * s.CupCapacityMl);
            var at = day.Date + rounded;
            if (result.Count > 0 && result[^1].At >= at) result[^1] = result[^1] with { DueMl = due }; // merge
            else result.Add(new ReminderSlot(at, due));
        }
        return result;
    }

    private static TimeSpan RoundTo5(TimeSpan t) => TimeSpan.FromMinutes(Math.Round(t.TotalMinutes / 5.0, MidpointRounding.AwayFromZero) * 5);
    private static TimeSpan FloorTo5(TimeSpan t) => TimeSpan.FromMinutes(Math.Floor(t.TotalMinutes / 5.0) * 5);

    public static DayPhase PhaseAt(UserSettings s, DateTime now)
    {
        var t = now.TimeOfDay;
        if (t < s.WorkStart) return DayPhase.BeforeWork;
        if (t >= s.WorkEnd) return DayPhase.AfterWork;
        if (t >= Cutoff(s)) return DayPhase.NoDrinkWindow;
        if (EffectiveLunch(s) is { } l && t >= l.Start && t < l.End) return DayPhase.Lunch;
        return DayPhase.Working;
    }

    [Obsolete("Use CalculateDynamic instead.")]
    public static ReminderPlan Calculate(UserSettings settings, DailyRecord record, DateTime now)
    {
        var slots = Schedule(settings, now);
        if (slots.Count == 0) return new ReminderPlan(0, TimeSpan.Zero, null, 0, 0);
        var interval = slots.Count > 1 ? TimeSpan.FromTicks((slots[^1].At - slots[0].At).Ticks / (slots.Count - 1)) : slots[0].At - now.Date.Add(settings.WorkStart);
        var due = 0;
        DateTime? next = null;
        foreach (var slot in slots)
        {
            if (slot.At <= now) due = slot.DueMl;
            else { next = slot.At; break; }
        }
        if (now.TimeOfDay >= Cutoff(settings)) { due = settings.DailyGoalMl; next = null; }
        return new ReminderPlan(slots.Count, interval, next, due, Math.Max(0, due - record.ConsumedMl));
    }

    public static readonly TimeSpan MinReminderGap = TimeSpan.FromMinutes(20);

    /// <summary>Work-hours cup; falls back to the legacy single cup when the split field is unset.</summary>
    public static int WorkCupMl(UserSettings s)
        => s.WorkCupCapacityMl > 0 ? s.WorkCupCapacityMl : s.CupCapacityMl > 0 ? s.CupCapacityMl : 370;

    /// <summary>Evening cup used outside work hours.</summary>
    public static int HomeCupMl(UserSettings s) => s.HomeCupCapacityMl > 0 ? s.HomeCupCapacityMl : 800;

    /// <summary>
    /// Cup in effect right now: the work cup on an active workday between WorkStart and WorkEnd,
    /// otherwise the home cup (evenings, weekends, before work).
    /// </summary>
    public static int CurrentCupMl(UserSettings s, DateTime now)
        => IsActiveDay(s, now) && now.TimeOfDay >= s.WorkStart && now.TimeOfDay < s.WorkEnd
            ? WorkCupMl(s)
            : HomeCupMl(s);

    /// <summary>Desktop popup/sound/tray balloon is allowed only during work hours (excluding lunch and the cutoff window).</summary>
    public static bool ShouldShowDesktop(UserSettings s, DateTime now)
        => IsActiveDay(s, now) && !s.RemindersPaused && PhaseAt(s, now) == DayPhase.Working;

    /// <summary>
    /// Phone push goes out in every reminder window: work hours, and after work until quiet.
    /// Lunch and the pre-end no-drink window are not reminder windows. After quiet hours,
    /// and on rest days, nothing is sent. Desktop popups stay work-hours only.
    /// </summary>
    public static bool ShouldSendMobilePush(UserSettings s, DateTime now)
    {
        if (!s.MobilePushEnabled || !IsActiveDay(s, now) || s.RemindersPaused) return false;
        var t = now.TimeOfDay;
        if (t < s.WorkStart || t >= s.QuietStart) return false;
        var phase = PhaseAt(s, now);
        return phase is DayPhase.Working or DayPhase.AfterWork;
    }

    /// <summary>Drinking minutes in [from, cutoff) excluding lunch.</summary>
    public static double AvailableMinutes(UserSettings s, DateTime from)
    {
        var day = from.Date;
        var start = from.TimeOfDay < s.WorkStart ? s.WorkStart : from.TimeOfDay;
        var cutoff = Cutoff(s);
        if (start >= cutoff) return 0;
        var total = (cutoff - start).TotalMinutes;
        if (EffectiveLunch(s) is { } l)
        {
            var os = start > l.Start ? start : l.Start;
            var oe = cutoff < l.End ? cutoff : l.End;
            if (oe > os) total -= (oe - os).TotalMinutes;
        }
        return Math.Max(0, total);
    }

    /// <summary>Clock time reached after spending <paramref name="minutes"/> of drinking time from <paramref name="from"/>, skipping lunch.</summary>
    public static DateTime AddAvailable(UserSettings s, DateTime from, double minutes)
    {
        var t = from.TimeOfDay < s.WorkStart ? from.Date + s.WorkStart : from;
        if (EffectiveLunch(s) is { } l)
        {
            var ls = t.Date + l.Start; var le = t.Date + l.End;
            if (t >= ls && t < le) t = le;
            else if (t < ls && t.AddMinutes(minutes) >= ls) return le.AddMinutes(minutes - (ls - t).TotalMinutes);
        }
        return t.AddMinutes(minutes);
    }

    /// <summary>
    /// Dynamic plan from live progress. remaining = goal − consumed, cups = ceil(remaining / cup).
    /// Countdown restarts at the latest of: work start, last positive drink/correction, last fired reminder.
    /// Interval = available drinking time (anchor → cutoff, minus lunch) / (cups + ½), at least <see cref="MinReminderGap"/>;
    /// next = anchor + interval skipping lunch, never inside lunch and never at/after the cutoff.
    /// If next ≤ now a reminder is due now (SuggestedMl &gt; 0).
    /// </summary>
    public static ReminderPlan CalculateDynamic(UserSettings s, DailyRecord record, DateTime now, DateTime? lastReminderAt = null)
    {
        var workCup = WorkCupMl(s);
        if (s.DailyGoalMl <= 0 || workCup <= 0) return new ReminderPlan(0, TimeSpan.Zero, null, 0, 0);
        var remaining = Math.Max(0, s.DailyGoalMl - record.ConsumedMl);
        if (remaining == 0) return new ReminderPlan(0, TimeSpan.Zero, null, s.DailyGoalMl, 0);
        var day = now.Date;
        var cutoff = day + Cutoff(s);

        // After work (and before quiet hours) plan the remaining cups with the home cup.
        // Note: before WorkEnd this branch never runs, so the daytime plan below is unchanged.
        if (now >= day + s.WorkEnd && now.TimeOfDay < s.QuietStart)
            return CalculateEvening(s, record, now, lastReminderAt, remaining);

        var cups = (int)Math.Ceiling(remaining / (double)workCup);
        if (now >= cutoff || cutoff <= day + s.WorkStart) return new ReminderPlan(cups, TimeSpan.Zero, null, s.DailyGoalMl, 0);

        var anchor = day + s.WorkStart;
        var lastDrink = record.Events.Where(e => e.AmountMl > 0 && e.At.Date == day && e.At <= now).Select(e => (DateTime?)e.At).Max();
        if (lastDrink > anchor) anchor = lastDrink.Value;
        if (lastReminderAt is { } lr && lr.Date == day && lr > anchor && lr <= now) anchor = lr;
        if (anchor >= cutoff) return new ReminderPlan(cups, TimeSpan.Zero, null, s.DailyGoalMl, 0);

        var available = AvailableMinutes(s, anchor);
        var interval = TimeSpan.FromMinutes(available / (cups + 0.5));
        if (interval < MinReminderGap) interval = MinReminderGap;
        var next = CeilToMinute(AddAvailable(s, anchor, interval.TotalMinutes));
        if (next >= cutoff)
        {
            // squeeze the last reminder in before the cutoff, but keep the minimum gap
            var latest = cutoff.AddMinutes(-5);
            next = latest >= AddAvailable(s, anchor, MinReminderGap.TotalMinutes) ? latest : DateTime.MaxValue;
            if (next == DateTime.MaxValue) return new ReminderPlan(cups, interval, null, s.DailyGoalMl, 0);
        }
        if (EffectiveLunch(s) is { } l)
        {
            if (next >= day + l.Start && next < day + l.End) next = day + l.End;
            if (now >= day + l.Start && now < day + l.End && next < day + l.End) next = day + l.End; // overdue during lunch -> right after lunch
        }

        var dueNow = next <= now;
        var suggested = dueNow ? Math.Min(workCup, remaining) : 0;
        return new ReminderPlan(cups, interval, next, record.ConsumedMl + suggested, suggested);
    }

    /// <summary>
    /// Evening plan: remaining home cups spread over [WorkEnd, QuietStart), anchored at the latest of
    /// WorkEnd, the last positive drink/correction today and the last fired reminder.
    /// Never schedules at/after QuietStart; drops the reminder when even the minimum gap does not fit.
    /// </summary>
    private static ReminderPlan CalculateEvening(UserSettings s, DailyRecord record, DateTime now, DateTime? lastReminderAt, int remaining)
    {
        var day = now.Date;
        var homeCup = HomeCupMl(s);
        var cups = (int)Math.Ceiling(remaining / (double)homeCup);
        var quiet = day + s.QuietStart;

        var anchor = day + s.WorkEnd;
        var lastDrink = record.Events.Where(e => e.AmountMl > 0 && e.At.Date == day && e.At <= now).Select(e => (DateTime?)e.At).Max();
        if (lastDrink > anchor) anchor = lastDrink.Value;
        if (lastReminderAt is { } lr && lr.Date == day && lr > anchor && lr <= now) anchor = lr;
        if (anchor >= quiet) return new ReminderPlan(cups, TimeSpan.Zero, null, s.DailyGoalMl, 0);

        var available = (quiet - anchor).TotalMinutes;
        var interval = TimeSpan.FromMinutes(available / (cups + 0.5));
        if (interval < MinReminderGap) interval = MinReminderGap;
        var next = CeilToMinute(anchor + interval);
        if (next >= quiet)
        {
            // squeeze the last one in just before quiet hours, but keep the minimum gap
            var latest = quiet.AddMinutes(-5);
            next = latest >= anchor + MinReminderGap ? latest : DateTime.MaxValue;
            if (next == DateTime.MaxValue) return new ReminderPlan(cups, interval, null, s.DailyGoalMl, 0);
        }

        var dueNow = next <= now;
        var suggested = dueNow ? Math.Min(homeCup, remaining) : 0;
        return new ReminderPlan(cups, interval, next, record.ConsumedMl + suggested, suggested);
    }

    private static DateTime CeilToMinute(DateTime t)
        => new(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute + (t.Ticks % TimeSpan.TicksPerMinute == 0 ? 0 : TimeSpan.TicksPerMinute));

    /// <summary>Status text shared by the main window (full), compact view and tray tooltip (compact).</summary>
    public static string StatusLine(UserSettings s, DailyRecord record, ReminderPlan plan, DateTime now, bool compact)
    {
        if (record.ConsumedMl >= s.DailyGoalMl) return compact ? "今日目标已完成" : "太棒了，今天的喝水目标已完成！";
        if (s.RemindersPaused)
            return compact ? "提醒已暂停" : s.PauseUntil is null || s.PauseUntil.Value.Date > now.Date ? "今日提醒已暂停" : $"提醒已暂停至 {s.PauseUntil:HH:mm}";
        if (!IsActiveDay(s, now)) return "今天休息，不提醒";
        var phase = PhaseAt(s, now);
        if (phase == DayPhase.NoDrinkWindow) return "下班前不再提醒";
        if (plan.NextReminder is null) return "今日提醒已结束";
        if (phase == DayPhase.Lunch) return compact ? $"午休中 · 下次 {plan.NextReminder:HH:mm}" : $"午休中 · 下次提醒 {plan.NextReminder:HH:mm}";
        var cup = Math.Min(CurrentCupMl(s, now), s.DailyGoalMl - record.ConsumedMl);
        if (plan.SuggestedMl > 0) return compact ? $"该喝水了 · {plan.SuggestedMl} ml" : $"该喝水了，建议现在补充 {plan.SuggestedMl} ml";
        return compact ? $"下次提醒 {plan.NextReminder:HH:mm}" : $"下次提醒 {plan.NextReminder:HH:mm} · 建议 {cup} ml";
    }

    /// <summary>The most recent reminder time at or before <paramref name="now"/>, if any.</summary>
    public static DateTime? CurrentSlot(UserSettings settings, DateTime now)
        => Schedule(settings, now).LastOrDefault(x => x.At <= now)?.At;

    public static bool IsActiveDay(UserSettings settings, DateTime date)
        => settings.ActiveDays[(int)date.DayOfWeek == 0 ? 6 : (int)date.DayOfWeek - 1];

    public const int MaxDailyTotalMl = 20000;

    /// <summary>Delta to record so that today's total becomes <paramref name="newTotal"/>.</summary>
    public static int AdjustmentFor(DailyRecord record, int newTotal) => newTotal - record.Events.Sum(x => x.AmountMl);

    /// <summary>Parses a user-typed daily total ("1,200", " 800 ml"). Returns an error message on failure.</summary>
    public static bool TryParseTotal(string? text, out int total, out string? error)
    {
        total = 0; error = null;
        var t = (text ?? "").Trim().Replace(",", "").Replace("，", "");
        if (t.EndsWith("ml", StringComparison.OrdinalIgnoreCase)) t = t[..^2].Trim();
        if (t.Length == 0) { error = "请输入总量"; return false; }
        if (!int.TryParse(t, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out total))
        { error = "请输入 0 或正整数"; return false; }
        if (total > MaxDailyTotalMl) { error = $"最多 {MaxDailyTotalMl:N0} ml"; return false; }
        return true;
    }
}
