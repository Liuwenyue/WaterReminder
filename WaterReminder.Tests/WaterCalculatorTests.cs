using WaterReminder.Services;
using WaterReminder;
using Xunit;

namespace WaterReminder.Tests;

public sealed class WaterCalculatorTests
{
    [Fact]
    public void CalculatesCeilingReminderCount()
    {
        var settings = new UserSettings { DailyGoalMl = 2000, CupCapacityMl = 300, WorkStart = new(9, 0, 0), WorkEnd = new(18, 0, 0) };
        var slots = WaterCalculator.Schedule(settings, new DateTime(2026, 11, 2, 12, 0, 0));
        Assert.Equal(7, slots.Count);
    }

    [Fact]
    public void DoesNotExceedGoalInSuggestion()
    {
        var now = new DateTime(2026, 11, 2, 17, 0, 0);
        var settings = new UserSettings { DailyGoalMl = 500, CupCapacityMl = 300, WorkCupCapacityMl = 300, WorkStart = new(9, 0, 0), WorkEnd = new(18, 0, 0) };
        var record = new DailyRecord { Events = [new WaterEvent { AmountMl = 400, At = now.AddMinutes(-30) }] };
        var plan = WaterCalculator.CalculateDynamic(settings, record, now);
        Assert.InRange(plan.SuggestedMl, 0, 100);
    }

    [Fact]
    public void ActiveDaysUseMondayBasedIndex()
    {
        var settings = new UserSettings { ActiveDays = [true, false, false, false, false, false, false] };
        Assert.True(WaterCalculator.IsActiveDay(settings, new DateTime(2026, 11, 2))); // Monday
        Assert.False(WaterCalculator.IsActiveDay(settings, new DateTime(2026, 11, 3))); // Tuesday
    }

    private static UserSettings Default() => new() { DailyGoalMl = 2000, CupCapacityMl = 300, WorkStart = new(9, 0, 0), WorkEnd = new(18, 0, 0) };

    [Fact]
    public void GoalSmallerThanCupGivesSingleReminder()
    {
        var settings = new UserSettings { DailyGoalMl = 200, CupCapacityMl = 300, WorkStart = new(9, 0, 0), WorkEnd = new(18, 0, 0) };
        var slots = WaterCalculator.Schedule(settings, new DateTime(2026, 11, 2, 10, 0, 0));
        Assert.Single(slots);
        Assert.Equal(200, slots[0].DueMl);
    }

    [Fact]
    public void BeforeWorkStartNothingIsDue()
    {
        var now = new DateTime(2026, 11, 2, 8, 0, 0);
        var plan = WaterCalculator.CalculateDynamic(Default(), new DailyRecord(), now);
        Assert.Equal(0, plan.DueMl);
        Assert.Equal(0, plan.SuggestedMl);
        Assert.True(plan.NextReminder > now);
    }

    [Fact]
    public void MissedReminders_SuggestOneCupNow()
    {
        // Dynamic planning never merges missed cups: it asks for one cup at a time.
        var plan = WaterCalculator.CalculateDynamic(Default(), new DailyRecord(), new DateTime(2026, 11, 2, 14, 30, 0));
        Assert.Equal(300, plan.SuggestedMl);
        Assert.True(plan.NextReminder <= new DateTime(2026, 11, 2, 14, 30, 0));
    }

    [Fact]
    public void GoalReachedStopsSuggestions()
    {
        var now = new DateTime(2026, 11, 2, 17, 0, 0);
        var record = new DailyRecord { Events = [new WaterEvent { AmountMl = 2000, At = now.AddHours(-1) }] };
        var plan = WaterCalculator.CalculateDynamic(Default(), record, now);
        Assert.Equal(0, plan.SuggestedMl);
    }

    [Fact]
    public void StaticScheduleEndsBeforeWorkEnd()
    {
        var s = Default();
        var slots = WaterCalculator.Schedule(s, new DateTime(2026, 11, 2));
        Assert.NotEmpty(slots);
        Assert.All(slots, x => Assert.True(x.At.TimeOfDay < s.WorkEnd));
    }

    [Fact]
    public void InvalidWorkHoursProduceEmptyPlan()
    {
        var settings = new UserSettings { WorkStart = new(18, 0, 0), WorkEnd = new(9, 0, 0) };
        var now = new DateTime(2026, 11, 2, 8, 0, 0); // before the (invalid) WorkEnd: no daytime plan
        Assert.Empty(WaterCalculator.Schedule(settings, now));
        var plan = WaterCalculator.CalculateDynamic(settings, new DailyRecord(), now);
        Assert.Null(plan.NextReminder);
    }

    [Fact]
    public void WeekendIsInactiveByDefault()
    {
        var settings = new UserSettings();
        Assert.False(WaterCalculator.IsActiveDay(settings, new DateTime(2026, 11, 7))); // Saturday
        Assert.False(WaterCalculator.IsActiveDay(settings, new DateTime(2026, 11, 8))); // Sunday
        Assert.True(WaterCalculator.IsActiveDay(settings, new DateTime(2026, 11, 6)));  // Friday
    }

    [Fact]
    public void ConsumedNeverNegative()
    {
        var record = new DailyRecord { Events = [new WaterEvent { AmountMl = 300 }, new WaterEvent { AmountMl = -500 }] };
        Assert.Equal(0, record.ConsumedMl);
    }

    [Theory]
    [InlineData(new[] { 370 }, 1000, 630)]
    [InlineData(new[] { 370, 300 }, 500, -170)]
    [InlineData(new[] { 370 }, 370, 0)]
    [InlineData(new int[0], 800, 800)]
    [InlineData(new[] { 300, -50 }, 0, -250)]
    public void AdjustmentMakesTotalEqualTarget(int[] amounts, int target, int expected)
    {
        var record = new DailyRecord { Events = amounts.Select(a => new WaterEvent { AmountMl = a }).ToList() };
        var delta = WaterCalculator.AdjustmentFor(record, target);
        Assert.Equal(expected, delta);
        record.Events.Add(new WaterEvent { AmountMl = delta, Source = "adjust" });
        Assert.Equal(target, record.ConsumedMl);
    }

    [Fact]
    public void UndoingAdjustmentRestoresOriginalTotal()
    {
        var record = new DailyRecord { Events = [new WaterEvent { AmountMl = 370 }] };
        var adj = new WaterEvent { AmountMl = WaterCalculator.AdjustmentFor(record, 1200), Source = "adjust" };
        record.Events.Add(adj);
        Assert.Equal(1200, record.ConsumedMl);
        record.Events.RemoveAll(x => x.Id == adj.Id);
        Assert.Equal(370, record.ConsumedMl);
    }

    [Theory]
    [InlineData("1200", 1200)]
    [InlineData(" 1,200 ", 1200)]
    [InlineData("800ml", 800)]
    [InlineData("0", 0)]
    [InlineData("20000", 20000)]
    public void ParsesValidTotals(string text, int expected)
    {
        Assert.True(WaterCalculator.TryParseTotal(text, out var total, out var error));
        Assert.Equal(expected, total);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("12.5")]
    [InlineData("20001")]
    public void RejectsInvalidTotals(string text)
    {
        Assert.False(WaterCalculator.TryParseTotal(text, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    private static UserSettings Mine() => new()
    {
        DailyGoalMl = 2220, CupCapacityMl = 370, WorkCupCapacityMl = 370, HomeCupCapacityMl = 800,
        WorkStart = new(8, 30, 0), WorkEnd = new(18, 0, 0),
        LunchStart = new(12, 0, 0), LunchEnd = new(13, 30, 0), // pinned: these tests predate the 11:50 default
        ActiveDays = [true, true, true, true, true, false, false],
    };

    private static string[] Times(UserSettings s) => WaterCalculator.Schedule(s, new DateTime(2026, 11, 3)).Select(x => x.At.ToString("HH:mm")).ToArray();

    [Fact]
    public void UserSettingsSchedule()
    {
        Assert.Equal(["09:40", "10:50", "11:55", "14:35", "15:45", "16:55"], Times(Mine()));
        var slots = WaterCalculator.Schedule(Mine(), new DateTime(2026, 11, 3));
        Assert.Equal([370, 740, 1110, 1480, 1850, 2220], slots.Select(x => x.DueMl).ToArray());
    }

    [Fact]
    public void NewSettingsHaveLunchAndNoDrinkDefaults()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<UserSettings>("{\"DailyGoalMl\":2220,\"CupCapacityMl\":370}")!;
        Assert.True(s.LunchEnabled);
        Assert.Equal(new TimeSpan(11, 50, 0), s.LunchStart);
        Assert.Equal(new TimeSpan(13, 30, 0), s.LunchEnd);
        Assert.Equal(30, s.NoDrinkBeforeEndMinutes);
        Assert.Equal(2220, s.DailyGoalMl);
        Assert.Equal(new TimeSpan(23, 0, 0), s.QuietStart);
        Assert.Equal(new TimeSpan(8, 30, 0), s.WorkStart);
    }

    [Theory]
    [InlineData(2220, 370)]
    [InlineData(2000, 300)]
    [InlineData(1500, 500)]   // odd count 3
    [InlineData(2500, 500)]   // odd count 5
    [InlineData(3000, 200)]   // 15 reminders
    [InlineData(200, 300)]    // single
    [InlineData(10000, 50)]   // 200 slots -> merged
    public void ScheduleAvoidsLunchAndCutoffAndReachesGoal(int goal, int cup)
    {
        var s = Mine(); s.DailyGoalMl = goal; s.CupCapacityMl = cup;
        var slots = WaterCalculator.Schedule(s, new DateTime(2026, 11, 3));
        Assert.NotEmpty(slots);
        Assert.Equal(goal, slots[^1].DueMl);
        foreach (var x in slots)
        {
            var t = x.At.TimeOfDay;
            Assert.Equal(0, t.Minutes % 5);
            Assert.True(t > s.WorkStart);
            Assert.True(t < new TimeSpan(17, 30, 0), $"{t} after cutoff");
            Assert.False(t >= new TimeSpan(12, 0, 0) && t < new TimeSpan(13, 30, 0), $"{t} in lunch");
        }
        for (var i = 1; i < slots.Count; i++)
        {
            Assert.True(slots[i].At > slots[i - 1].At);
            Assert.True(slots[i].DueMl >= slots[i - 1].DueMl);
        }
    }

    [Fact]
    public void LastReminderStaysWellBeforeCutoff()
    {
        var last = WaterCalculator.Schedule(Mine(), new DateTime(2026, 11, 3))[^1].At.TimeOfDay;
        Assert.True(new TimeSpan(17, 30, 0) - last >= TimeSpan.FromMinutes(30));
    }

    private static UserSettings One(int sh, int sm, int ls, int lsm, int le, int lem, int eh, int em, int noDrink)
        => new() { DailyGoalMl = 300, CupCapacityMl = 300, WorkStart = new(sh, sm, 0), LunchStart = new(ls, lsm, 0), LunchEnd = new(le, lem, 0), WorkEnd = new(eh, em, 0), NoDrinkBeforeEndMinutes = noDrink };

    [Fact]
    public void ReminderAtLunchStartMovesToLunchEnd()
    {
        // available 150-60 = 90 min, single reminder at offset 60 = 10:00 = lunch start -> 11:00
        Assert.Equal(["11:00"], Times(One(9, 0, 10, 0, 11, 0, 11, 30, 0)));
    }

    [Theory]
    [InlineData(10, 0, 10, 40, 12, 0, 14, 30, 30, "13:05")] // offset after lunch start skips lunch
    [InlineData(10, 0, 10, 40, 12, 0, 13, 30, 30, "12:25")]
    [InlineData(10, 0, 10, 55, 11, 20, 11, 30, 0, "10:45")] // before lunch, stays before
    [InlineData(10, 0, 10, 45, 11, 10, 11, 30, 0, "10:40")] // 10:43 would round into lunch start -> floor
    public void LunchBoundaries(int sh, int sm, int ls, int lsm, int le, int lem, int eh, int em, int nd, string expected)
        => Assert.Equal([expected], Times(One(sh, sm, ls, lsm, le, lem, eh, em, nd)));

    [Fact]
    public void ReminderNeverAfterCutoff()
    {
        var s = Mine(); s.NoDrinkBeforeEndMinutes = 120; // cutoff 16:00
        Assert.All(Times(s), t => Assert.True(string.CompareOrdinal(t, "16:00") < 0, t));
    }

    [Fact]
    public void LunchDisabledUsesWholeDay()
    {
        var s = Mine(); s.LunchEnabled = false;
        Assert.Contains(Times(s), t => string.CompareOrdinal(t, "12:00") >= 0 && string.CompareOrdinal(t, "13:30") < 0);
    }

    [Fact]
    public void PhasesAndStatusAroundLunchAndCutoff()
    {
        var s = Mine(); var d = new DateTime(2026, 11, 3);
        Assert.Equal(WaterCalculator.DayPhase.BeforeWork, WaterCalculator.PhaseAt(s, d.AddHours(8)));
        Assert.Equal(WaterCalculator.DayPhase.Working, WaterCalculator.PhaseAt(s, d.AddHours(11.99)));
        Assert.Equal(WaterCalculator.DayPhase.Lunch, WaterCalculator.PhaseAt(s, d.AddHours(12)));
        Assert.Equal(WaterCalculator.DayPhase.Working, WaterCalculator.PhaseAt(s, d.AddHours(13.5)));
        Assert.Equal(WaterCalculator.DayPhase.NoDrinkWindow, WaterCalculator.PhaseAt(s, d.AddHours(17.5)));
        Assert.Equal(WaterCalculator.DayPhase.AfterWork, WaterCalculator.PhaseAt(s, d.AddHours(18)));

        var lunch = WaterCalculator.CalculateDynamic(s, new DailyRecord(), d.AddHours(12.5));
        Assert.Equal(new DateTime(2026, 11, 3, 13, 30, 0), lunch.NextReminder);
        Assert.Equal(0, lunch.SuggestedMl);

        var cutoff = WaterCalculator.CalculateDynamic(s, new DailyRecord(), d.AddHours(17.6));
        Assert.Null(cutoff.NextReminder);
        Assert.Equal(2220, cutoff.DueMl);

        var afterLast = WaterCalculator.CalculateDynamic(
            s, new DailyRecord { Events = [new WaterEvent { AmountMl = 370, At = d.AddHours(16) }] }, d.AddHours(17));
        Assert.Equal(new DateTime(2026, 11, 3, 16, 20, 0), afterLast.NextReminder);
        Assert.Equal(370, afterLast.SuggestedMl);
        Assert.Equal(new DateTime(2026, 11, 3, 16, 55, 0), WaterCalculator.CurrentSlot(s, d.AddHours(17)));
    }

    [Fact]
    public void NoDrinkWindowAsLongAsWorkGivesNoReminders()
    {
        var s = Mine(); s.NoDrinkBeforeEndMinutes = 570;
        Assert.Empty(Times(s));
        var plan = WaterCalculator.CalculateDynamic(s, new DailyRecord(), new DateTime(2026, 11, 3, 10, 0, 0));
        Assert.Null(plan.NextReminder);
    }
}
