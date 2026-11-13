using WaterReminder.Services;
using WaterReminder;
using Xunit;

namespace WaterReminder.Tests;

/// <summary>Dynamic next reminder (t12u) and "every path that changes today's total recomputes" (t13u).</summary>
public sealed class DynamicReminderTests
{
    private static readonly DateTime Day = new(2026, 11, 3); // Tuesday

    // user's current settings: 2220 / 370, 08:30–18:00, lunch 12:00–13:30, no drinking 17:30–18:00
    private static UserSettings Mine() => new()
    {
        DailyGoalMl = 2220, CupCapacityMl = 370, WorkCupCapacityMl = 370, HomeCupCapacityMl = 800,
        WorkStart = new(8, 30, 0), WorkEnd = new(18, 0, 0),
        LunchStart = new(12, 0, 0), LunchEnd = new(13, 30, 0), // pinned: these tests predate the 11:50 default
        ActiveDays = [true, true, true, true, true, false, false],
    };

    private static DateTime T(int h, int m) => Day.AddHours(h).AddMinutes(m);
    private static WaterEvent Ev(int h, int m, int ml, string src = "cup") => new() { At = T(h, m), AmountMl = ml, Source = src };
    private static DailyRecord Rec(params WaterEvent[] e) => new() { Date = "2026-11-03", Events = e.ToList() };
    private static ReminderPlan P(DailyRecord r, DateTime now, DateTime? reminded = null) => WaterCalculator.CalculateDynamic(Mine(), r, now, reminded);

    // ---------- algorithm ----------

    [Fact]
    public void OnPace_UserCurrentState()
    {
        // 370 at 09:50 -> 1050 ml still paced at work (1850 left, 800 reserved) over 370 min -> 11:36
        var p = P(Rec(Ev(9, 50, 370)), T(10, 0));
        Assert.Equal(T(11, 36), p.NextReminder);
        Assert.Equal(0, p.SuggestedMl);
    }

    [Fact]
    public void Ahead_PushesNextLater()
    {
        var onPace = P(Rec(Ev(10, 0, 370)), T(10, 5)).NextReminder;
        var ahead = P(Rec(Ev(10, 0, 1110)), T(10, 5)).NextReminder;   // 310 ml left at work -> one cup, skips lunch
        Assert.Equal(T(11, 43), onPace);
        Assert.Equal(T(15, 30), ahead);
        Assert.True(ahead > onPace);
    }

    [Fact]
    public void Behind_IsDueNow_ThenShorterInterval()
    {
        var p = P(Rec(), T(11, 0));                       // nothing drunk, first one was due 10:10
        Assert.Equal(370, p.SuggestedMl);
        Assert.True(p.NextReminder <= T(11, 0));
        var after = P(Rec(), T(11, 1), reminded: T(11, 0)); // reminder fired at 11:00, ignored
        Assert.Equal(T(13, 37), after.NextReminder);        // 4 work cups in 300 min, interval crosses lunch
        Assert.Equal(0, after.SuggestedMl);
    }

    [Fact]
    public void Behind_KeepsMinimumGap()
    {
        var p = P(Rec(), T(16, 31), reminded: T(16, 30)); // 6 cups in 60 min would be 9 min apart
        Assert.Equal(T(16, 50), p.NextReminder);           // clamped to 20 min
    }

    [Fact]
    public void AcrossLunch_SkipsLunch()
    {
        var p = P(Rec(Ev(11, 50, 1110)), T(11, 51));      // 310 ml left at work, skips lunch
        Assert.Equal(T(16, 7), p.NextReminder);
    }

    [Fact]
    public void OverdueDuringLunch_WaitsForLunchEnd()
    {
        var p = P(Rec(Ev(9, 0, 370)), T(12, 30));
        Assert.Equal(T(13, 30), p.NextReminder);
        Assert.Equal(0, p.SuggestedMl);
        Assert.Equal("午休中 · 下次提醒 13:30", WaterCalculator.StatusLine(Mine(), Rec(Ev(9, 0, 370)), p, T(12, 30), false));
    }

    [Fact]
    public void GoalReached_NoMoreReminders()
    {
        var r = Rec(Ev(9, 0, 2220));
        var p = P(r, T(10, 0));
        Assert.Null(p.NextReminder);
        Assert.Equal(0, p.SuggestedMl);
        Assert.Equal("今日目标已完成", WaterCalculator.StatusLine(Mine(), r, p, T(10, 0), true));
    }

    [Fact]
    public void NearCutoff_LastOneSqueezedInOrDropped()
    {
        var r = Rec(Ev(16, 0, 1050)); // 1170 left: 370 still paced at work, 800 reserved for evening
        Assert.Equal(370, P(r, T(17, 10)).SuggestedMl);                       // due now
        Assert.Equal(T(17, 20), P(r, T(17, 1), reminded: T(17, 0)).NextReminder); // 20 min gap still fits
        Assert.Null(P(r, T(17, 11), reminded: T(17, 10)).NextReminder);       // 17:30 cutoff -> no room
        Assert.Null(P(r, T(17, 35)).NextReminder);
        Assert.Equal("下班前不再提醒", WaterCalculator.StatusLine(Mine(), r, P(r, T(17, 35)), T(17, 35), false));
    }

    [Fact]
    public void BeforeWork_FirstReminderFromWorkStart()
        => Assert.Equal(T(10, 10), P(Rec(), T(8, 0)).NextReminder);

    [Fact]
    public void NeverInsideLunchOrAfterCutoff()
    {
        for (var consumed = 0; consumed < 2220; consumed += 185)
            for (var min = 8 * 60; min < 17 * 60 + 30; min += 7)
            {
                var now = Day.AddMinutes(min);
                var p = P(Rec(new WaterEvent { At = now.AddMinutes(-1), AmountMl = Math.Max(1, consumed) }), now);
                if (p.NextReminder is not { } n) continue;
                Assert.False(n >= T(12, 0) && n < T(13, 30), $"{now:HH:mm}/{consumed}: {n:HH:mm} in lunch");
                Assert.True(n < T(17, 30), $"{now:HH:mm}/{consumed}: {n:HH:mm} after cutoff");
                Assert.True(n >= now.AddMinutes(19), $"{now:HH:mm}/{consumed}: {n:HH:mm} too soon after drink");
            }
    }

    [Fact]
    public void WeekendStatusUnchanged()
    {
        var sat = new DateTime(2026, 11, 7, 10, 0, 0);
        var p = WaterCalculator.CalculateDynamic(Mine(), new DailyRecord(), sat);
        Assert.Equal("今天休息，不提醒", WaterCalculator.StatusLine(Mine(), new DailyRecord(), p, sat, false));
    }

    [Fact]
    public void DefaultWorkday_ReservesHomeCup_PlansSixWorkCupsNotNine()
    {
        // Product defaults: 3000 ml, work cup 370, home cup 800, 08:30–17:30, lunch 11:50–13:30, cutoff 17:00.
        // Drinking time is 410 minutes. Six work cups land about every 63 minutes, not nine cups every ~43.
        var s = new UserSettings();
        var morning = new DateTime(2026, 11, 3, 8, 0, 0);
        var plan = WaterCalculator.CalculateDynamic(s, new DailyRecord(), morning);

        Assert.Equal(3000, s.DailyGoalMl);
        Assert.Equal(370, WaterCalculator.WorkCupMl(s));
        Assert.Equal(800, WaterCalculator.HomeCupMl(s));
        Assert.Equal(20, s.MinReminderGapMinutes);
        Assert.Equal(6, plan.TotalReminders);
        Assert.NotEqual(9, plan.TotalReminders);
        Assert.Equal(63, Math.Round(plan.Interval.TotalMinutes));
        Assert.True(plan.Interval.TotalMinutes > s.MinReminderGapMinutes);
        Assert.Equal(new DateTime(2026, 11, 3, 9, 34, 0), plan.NextReminder);
        Assert.Equal(0, plan.SuggestedMl);

        var during = new DateTime(2026, 11, 3, 9, 0, 0);
        Assert.Equal("下次 09:34 · 370 ml", WaterCalculator.StatusLine(s, new DailyRecord(), plan, during, compact: true));
        Assert.Equal("下次提醒 09:34 · 建议 370 ml", WaterCalculator.StatusLine(s, new DailyRecord(), plan, during, compact: false));
    }

    [Fact]
    public void AfterSixWorkCups_NoMoreWorkReminder_EveningStillPlansOneHomeCup()
    {
        var s = new UserSettings();
        var day = new DateTime(2026, 11, 3);
        var six = new DailyRecord
        {
            Events = Enumerable.Range(0, 6).Select(i => new WaterEvent
            {
                At = day.AddHours(9).AddMinutes(i * 50),
                AmountMl = 370,
                Source = "cup",
            }).ToList(),
        };
        Assert.Equal(2220, six.ConsumedMl);

        var during = WaterCalculator.CalculateDynamic(s, six, day.AddHours(16));
        Assert.Equal(0, during.TotalReminders);
        Assert.Null(during.NextReminder);
        Assert.Equal(0, during.SuggestedMl);
        Assert.Equal("下班后再提醒", WaterCalculator.StatusLine(s, six, during, day.AddHours(16), false));

        six.Events.Add(new WaterEvent { At = day.AddHours(16).AddMinutes(10), AmountMl = 20, Source = "custom" });
        Assert.True(six.ConsumedMl > 2220);
        Assert.Null(WaterCalculator.CalculateDynamic(s, six, day.AddHours(16).AddMinutes(20)).NextReminder);

        var five = new DailyRecord { Events = [new WaterEvent { At = day.AddHours(9), AmountMl = 1850, Source = "cup" }] };
        var stillWorking = WaterCalculator.CalculateDynamic(s, five, day.AddHours(10));
        Assert.Equal(1, stillWorking.TotalReminders);
        Assert.NotNull(stillWorking.NextReminder);

        var eveningRecord = new DailyRecord { Events = [new WaterEvent { At = day.AddHours(9), AmountMl = 2220, Source = "cup" }] };
        var evening = WaterCalculator.CalculateDynamic(s, eveningRecord, day.AddHours(17).AddMinutes(30));
        Assert.Equal(1, evening.TotalReminders);
        Assert.Equal(day.AddHours(21).AddMinutes(10), evening.NextReminder);
        Assert.Equal(0, evening.SuggestedMl);
    }

    [Fact]
    public void NoEveningWindow_PacesTheWholeGoalDuringWork()
    {
        var s = new UserSettings { QuietStart = new TimeSpan(17, 30, 0) };
        var plan = WaterCalculator.CalculateDynamic(s, new DailyRecord(), new DateTime(2026, 11, 3, 8, 0, 0));
        Assert.Equal(9, plan.TotalReminders);
    }

    // ---------- every path that changes today's total recomputes immediately ----------

    private sealed class Harness
    {
        public DateTime Now;
        public DailyRecord Disk;
        public int Changes;
        public ProgressTracker Tracker;
        public Harness(DateTime now, params WaterEvent[] events)
        {
            Now = now;
            Disk = Rec(events);
            Tracker = new ProgressTracker(Mine(), _ => Clone(Disk), r => Disk = Clone(r), () => Now);
            Tracker.Changed += () => Changes++;
        }
        public DateTime? Next => Tracker.Plan().NextReminder;
        private static DailyRecord Clone(DailyRecord r) => System.Text.Json.JsonSerializer.Deserialize<DailyRecord>(System.Text.Json.JsonSerializer.Serialize(r))!;
    }

    [Theory]
    [InlineData("cup")]
    [InlineData("keyboard")]
    [InlineData("custom")]
    public void DrinkRecomputesFromDrinkTime(string source)
    {
        var h = new Harness(T(11, 0));
        Assert.True(h.Tracker.Plan().SuggestedMl > 0);   // behind, due now
        Assert.Equal(370, h.Tracker.Drink(370, source));
        Assert.Equal(1, h.Changes);
        Assert.Equal(T(13, 56), h.Next);                // 3 work cups over 300 min from 11:00, skips lunch
        Assert.Equal(0, h.Tracker.Plan().SuggestedMl);
        Assert.Equal(370, h.Disk.ConsumedMl);           // saved
    }

    [Fact]
    public void CorrectionUpRecomputes()
    {
        var h = new Harness(T(11, 0));
        Assert.Equal(1110, h.Tracker.Correct(1110));
        Assert.Equal(1, h.Changes);
        Assert.Equal(T(15, 50), h.Next);                // 310 ml left at work, skips lunch
    }

    [Fact]
    public void CorrectionDownRecomputes()
    {
        var h = new Harness(T(10, 30), Ev(10, 0, 1110));
        Assert.Equal(T(15, 30), h.Next);
        Assert.Equal(-740, h.Tracker.Correct(370));
        Assert.Equal(1, h.Changes);
        Assert.Equal(T(11, 43), h.Next);                // more left at work -> sooner, timer still from last real drink
    }

    [Fact]
    public void EditRecordRecomputes()
    {
        var ev = Ev(10, 0, 370);
        var h = new Harness(T(10, 30), ev);
        var before = h.Next;
        Assert.Equal(T(11, 43), before);
        Assert.True(h.Tracker.EditEvent(ev.Id, 1110));
        Assert.Equal(1, h.Changes);
        Assert.Equal(T(15, 30), h.Next);
        Assert.True(h.Next > before);
    }

    [Fact]
    public void DeleteRecordRecomputes()
    {
        var ev = Ev(10, 0, 370);
        var h = new Harness(T(10, 30), ev);
        Assert.True(h.Tracker.DeleteEvent(ev.Id));
        Assert.Equal(1, h.Changes);
        Assert.Equal(370, h.Tracker.Plan().SuggestedMl); // nothing drunk -> 10:10 was due
    }

    [Fact]
    public void ClearTodayRecomputes()
    {
        var h = new Harness(T(10, 30), Ev(10, 0, 370), Ev(10, 10, 370));
        Assert.True(h.Tracker.ClearToday());
        Assert.Equal(1, h.Changes);
        Assert.Equal(0, h.Disk.ConsumedMl);
        Assert.Equal(370, h.Tracker.Plan().SuggestedMl);
    }

    [Fact]
    public void SettingsChangeRecomputes()
    {
        var h = new Harness(T(10, 30), Ev(10, 0, 370));
        var before = h.Next;
        h.Tracker.Settings.DailyGoalMl = 1900;
        h.Tracker.SettingsChanged();
        Assert.Equal(1, h.Changes);
        Assert.Equal(T(13, 54), h.Next);
        Assert.True(h.Next > before);
    }

    [Fact]
    public void FiredReminderRestartsCountdown()
    {
        var h = new Harness(T(11, 0));
        h.Tracker.MarkReminded(T(11, 0));
        Assert.Equal(1, h.Changes);
        Assert.Equal(T(13, 37), h.Next);
    }

    [Fact]
    public void ExternalChangeOnDiskIsPickedUpOnReload()
    {
        var h = new Harness(T(10, 30));
        h.Tracker.Reload();
        Assert.Equal(0, h.Changes);                     // nothing changed -> no event
        h.Disk.Events.Add(Ev(10, 20, 370));             // e.g. another window saved
        h.Tracker.Reload();
        Assert.Equal(1, h.Changes);
        Assert.Equal(370, h.Tracker.Today.ConsumedMl);
    }

    [Fact]
    public void NoOpChangesDoNotRaise()
    {
        var h = new Harness(T(10, 30), Ev(10, 0, 370));
        Assert.Equal(0, h.Tracker.Correct(370));
        Assert.Equal(0, h.Tracker.Drink(0, "cup"));
        Assert.Equal(0, h.Changes);
    }
}
