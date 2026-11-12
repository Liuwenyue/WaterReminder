using WaterReminder;
using WaterReminder.Services;
using Xunit;

namespace WaterReminder.Tests;

/// <summary>After-work phone-push planning, split cups, reminder decisions and PushWard settings compatibility.</summary>
public sealed class EveningPushTests
{
    private static readonly DateTime Day = new(2026, 11, 3); // Tuesday
    private static readonly DateTime Saturday = new(2026, 11, 7);
    private static readonly DateTime Friday = new(2026, 11, 6);
    private static readonly DateTime Monday = new(2026, 11, 9);

    private static UserSettings Settings() => new()
    {
        DailyGoalMl = 2220,
        CupCapacityMl = 370,
        WorkCupCapacityMl = 370,
        HomeCupCapacityMl = 800,
        WorkStart = new(8, 30, 0),
        WorkEnd = new(18, 0, 0),
        LunchStart = new(12, 0, 0),
        LunchEnd = new(13, 30, 0),
        QuietStart = new(23, 0, 0),
        ActiveDays = [true, true, true, true, true, false, false],
        MobilePushEnabled = true,
        PushWardApiToken = "hlk_test_token",
    };

    private static DateTime T(int h, int m) => Day.AddHours(h).AddMinutes(m);
    private static WaterEvent Ev(int h, int m, int ml, string src = "cup") => new() { At = T(h, m), AmountMl = ml, Source = src };
    private static DailyRecord Rec(params WaterEvent[] e) => new() { Date = "2026-11-03", Events = e.ToList() };
    private static ReminderPlan P(DailyRecord r, DateTime now, DateTime? reminded = null)
        => WaterCalculator.CalculateDynamic(Settings(), r, now, reminded);

    // ---------- evening plan ----------

    [Fact]
    public void Evening_Remaining1850_SpreadsThreeHomeCupsBeforeQuiet()
    {
        var rec = Rec(Ev(9, 0, 370)); // 1850 ml left
        var now = T(18, 0);
        var times = new List<DateTime>();

        for (var i = 0; i < 3; i++)
        {
            var p = P(rec, now);
            Assert.NotNull(p.NextReminder);
            Assert.Equal(3 - i, p.TotalReminders);
            Assert.True(p.NextReminder < Day.AddHours(23), $"{p.NextReminder:HH:mm} at/after quiet");
            Assert.True(p.NextReminder >= now, $"{p.NextReminder:HH:mm} before {now:HH:mm}");
            times.Add(p.NextReminder!.Value);

            rec.Events.Add(new WaterEvent { At = p.NextReminder.Value, AmountMl = 800, Source = "pushward" });
            now = p.NextReminder.Value;
        }

        Assert.Equal(new[] { T(19, 26), T(20, 52), T(22, 18) }, times.ToArray());
    }

    [Fact]
    public void Evening_AfterDrinkRespectsMinimumGap()
    {
        var p = P(Rec(Ev(9, 0, 370), Ev(19, 26, 800)), T(19, 26));
        Assert.NotNull(p.NextReminder);
        Assert.True(p.NextReminder - T(19, 26) >= WaterCalculator.MinReminderGap);
        Assert.True(p.NextReminder < Day.AddHours(23));
    }

    [Fact]
    public void Evening_StatusShowsNextPushTime()
    {
        var rec = Rec(Ev(9, 0, 370));
        var now = T(18, 0);
        var p = P(rec, now);
        Assert.Equal("下次提醒 19:26 · 建议 800 ml", WaterCalculator.StatusLine(Settings(), rec, p, now, false));
        Assert.Equal("下次提醒 19:26 · 建议 800 ml", WaterCalculator.StatusLine(Settings(), rec, p, now, true));
    }

    [Fact]
    public void Evening_NearQuiet_DropsInsteadOfSchedulingAtQuietStart()
    {
        // 450 ml left, last drink at 22:45: the 20-minute gap cannot fit before 23:00.
        var rec = Rec(Ev(9, 0, 370), Ev(19, 0, 600), Ev(20, 0, 600), Ev(22, 45, 200));
        Assert.Null(P(rec, T(22, 45)).NextReminder);
    }

    [Theory]
    [InlineData(17, 31)]
    [InlineData(17, 35)]
    [InlineData(17, 59)]
    public void NoDrinkWindow_StillHasNoNextAndKeepsStatus(int h, int m)
    {
        var rec = Rec(Ev(16, 0, 1850));
        var now = T(h, m);
        Assert.Null(P(rec, now).NextReminder);
        Assert.Equal("下班前不再提醒", WaterCalculator.StatusLine(Settings(), rec, P(rec, now), now, false));
    }

    [Fact]
    public void AfterQuietStart_NoNextEvenWithRemaining()
    {
        var rec = Rec(Ev(9, 0, 370));
        var now = T(23, 1);
        var p = P(rec, now);
        Assert.Null(p.NextReminder);
        Assert.Equal(0, p.SuggestedMl);
        Assert.Equal("今日提醒已结束", WaterCalculator.StatusLine(Settings(), rec, p, now, false));
    }

    // ---------- reminder decisions ----------

    [Fact]
    public void Saturday_RestDay_NoPushNoDesktop()
    {
        var s = Settings();
        var now = Saturday.AddHours(19);
        Assert.False(WaterCalculator.ShouldSendMobilePush(s, now));
        Assert.False(WaterCalculator.ShouldShowDesktop(s, now));
        var p = WaterCalculator.CalculateDynamic(s, new DailyRecord(), now);
        Assert.Equal("今天休息，不提醒", WaterCalculator.StatusLine(s, new DailyRecord(), p, now, false));
    }

    [Fact]
    public void FridayQuietStart_ThenMondayBeforeWork_NoFire()
    {
        var s = Settings();
        var fridayQuiet = Friday.AddHours(23);
        Assert.False(WaterCalculator.ShouldSendMobilePush(s, fridayQuiet));
        Assert.Null(P(Rec(Ev(9, 0, 370)), fridayQuiet).NextReminder); // date/time of Day only matters for the clock

        var mondayMorning = Monday.AddHours(8);
        Assert.Equal(WaterCalculator.DayPhase.BeforeWork, WaterCalculator.PhaseAt(s, mondayMorning));
        Assert.False(WaterCalculator.ShouldSendMobilePush(s, mondayMorning));
        Assert.False(WaterCalculator.ShouldShowDesktop(s, mondayMorning));

        var fridayLate = Friday.AddHours(22).AddMinutes(59);
        Assert.True(WaterCalculator.ShouldSendMobilePush(s, fridayLate));
    }

    [Fact]
    public void DesktopOnlyDuringWork_PushOnEveryReminderWindow()
    {
        var s = Settings();
        Assert.True(WaterCalculator.ShouldShowDesktop(s, T(10, 0)));
        Assert.False(WaterCalculator.ShouldShowDesktop(s, T(12, 30)));  // lunch
        Assert.False(WaterCalculator.ShouldShowDesktop(s, T(17, 45)));  // no-drink window
        Assert.False(WaterCalculator.ShouldShowDesktop(s, T(18, 0)));   // after work: phone only
        Assert.True(WaterCalculator.ShouldSendMobilePush(s, T(10, 0))); // work window, phone as well
        Assert.False(WaterCalculator.ShouldSendMobilePush(s, T(12, 30)));
        Assert.False(WaterCalculator.ShouldSendMobilePush(s, T(17, 45)));
        Assert.True(WaterCalculator.ShouldSendMobilePush(s, T(18, 0)));
        Assert.False(WaterCalculator.ShouldSendMobilePush(s, T(23, 0)));      // quiet start

        s.RemindersPaused = true;
        Assert.False(WaterCalculator.ShouldSendMobilePush(s, T(19, 0)));
        Assert.False(WaterCalculator.ShouldShowDesktop(s, T(10, 0)));

        s.RemindersPaused = false;
        s.MobilePushEnabled = false;
        Assert.False(WaterCalculator.ShouldSendMobilePush(s, T(19, 0)));
    }

    [Fact]
    public void CurrentCup_SwitchesByTimeAndDay()
    {
        var s = Settings();
        Assert.Equal(370, WaterCalculator.CurrentCupMl(s, T(10, 0)));
        Assert.Equal(800, WaterCalculator.CurrentCupMl(s, T(19, 0)));
        Assert.Equal(800, WaterCalculator.CurrentCupMl(s, T(8, 0)));
        Assert.Equal(800, WaterCalculator.CurrentCupMl(s, Saturday.AddHours(10)));
    }

    // ---------- a phone tap recomputes the next reminder ----------

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
            Tracker = new ProgressTracker(Settings(), _ => Clone(Disk), r => Disk = Clone(r), () => Now);
            Tracker.Changed += () => Changes++;
        }
        public DateTime? Next => Tracker.Plan().NextReminder;
        private static DailyRecord Clone(DailyRecord r) => System.Text.Json.JsonSerializer.Deserialize<DailyRecord>(System.Text.Json.JsonSerializer.Serialize(r))!;
    }

    [Fact]
    public void PushWardDrinkRecalculatesNextReminder()
    {
        var h = new Harness(T(18, 0), Ev(9, 0, 370));
        Assert.Equal(T(19, 26), h.Next);

        Assert.Equal(800, h.Tracker.Drink(800, PushWardMessageBuilder.RecordSource));
        Assert.Equal(1, h.Changes);
        Assert.Equal(T(20, 0), h.Next); // anchor moves to 18:00, 2 home cups over 5 h
        Assert.Equal("pushward", h.Disk.Events[^1].Source);
        Assert.Equal(1170, h.Disk.ConsumedMl);
    }

    // ---------- settings compatibility ----------

    [Fact]
    public void NewSettingsUseAfterWorkDefaults()
    {
        var s = new UserSettings();
        Assert.Equal(3000, s.DailyGoalMl);
        Assert.Equal(370, s.WorkCupCapacityMl);
        Assert.Equal(800, s.HomeCupCapacityMl);
        Assert.Equal(new TimeSpan(8, 30, 0), s.WorkStart);
        Assert.Equal(new TimeSpan(17, 30, 0), s.WorkEnd);
        Assert.Equal(new TimeSpan(11, 50, 0), s.LunchStart);
        Assert.Equal(new TimeSpan(13, 30, 0), s.LunchEnd);
        Assert.Equal(new TimeSpan(23, 0, 0), s.QuietStart);
        Assert.Equal(TimeSpan.FromMinutes(20), WaterCalculator.MinReminderGap);
        Assert.True(s.MobilePushEnabled);
        Assert.Equal("", s.PushWardApiToken);
    }

    [Fact]
    public void LegacyJson_RemovedChannelKeys_AreIgnoredGracefully()
    {
        WithStorage((storage, dir) =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                """{"DailyGoalMl":2000,"CupCapacityMl":500,"MobilePushChannel":"ntfy","NtfyTopic":"old-topic","NtfyServer":"https://old.example","NtfyActionTopic":"old-action","NtfyToken":"old-token","PushoverUserKey":"old-user","PushoverApiToken":"old-api","PushWardApiToken":" hlk_old "}""");

            var s = storage.LoadSettings();
            Assert.Equal(2000, s.DailyGoalMl);
            Assert.Equal("hlk_old", s.PushWardApiToken);
            Assert.True(s.MobilePushEnabled);

            // Saving drops the removed keys instead of writing them back.
            storage.SaveSettings(s);
            var saved = File.ReadAllText(Path.Combine(dir, "settings.json"));
            Assert.DoesNotContain("Ntfy", saved);
            Assert.DoesNotContain("Pushover", saved);
            Assert.DoesNotContain("MobilePushChannel", saved);
            Assert.Contains("hlk_old", saved);
        });
    }

    [Fact]
    public void RecordLabels_DescribePushWardPhoneDrink()
    {
        Assert.Equal("手机 Pushward", RecordLabels.Describe("pushward", 800));
    }

    [Theory]
    [InlineData(10, 0, true)]  // work window: phone as well as desktop
    [InlineData(12, 30, false)] // lunch is not a reminder window
    [InlineData(17, 59, false)] // no-drink window before WorkEnd
    [InlineData(18, 0, true)]  // after work, phone only
    [InlineData(22, 59, true)]
    [InlineData(23, 0, false)] // quiet hours
    public void ShouldSendMobilePush_OnWorkAndEveningWindows(int hour, int minute, bool expected)
        => Assert.Equal(expected, WaterCalculator.ShouldSendMobilePush(Settings(), T(hour, minute)));

    [Fact]
    public void ShouldSendMobilePush_RequiresEnabledDayAndNotPaused()
    {
        var s = Settings();
        Assert.False(WaterCalculator.ShouldSendMobilePush(s, Saturday.AddHours(19)));
        s.RemindersPaused = true;
        Assert.False(WaterCalculator.ShouldSendMobilePush(s, T(19, 0)));
        s.RemindersPaused = false;
        s.MobilePushEnabled = false;
        Assert.False(WaterCalculator.ShouldSendMobilePush(s, T(19, 0)));
    }

    private static string TempDir()
        => Path.Combine(Path.GetTempPath(), "WaterReminderTests", Guid.NewGuid().ToString("N"));

    private static void WithStorage(Action<StorageService, string> action)
    {
        var dir = TempDir();
        try { action(new StorageService(dir), dir); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void LegacyJson_MissingSplitCups_UsesCupCapacityForWork()
    {
        WithStorage((storage, dir) =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                """{"DailyGoalMl":2000,"CupCapacityMl":500,"WorkStart":"09:00:00","WorkEnd":"18:00:00"}""");

            var s = storage.LoadSettings();
            Assert.Equal(500, s.WorkCupCapacityMl);
            Assert.Equal(500, s.CupCapacityMl); // legacy field kept in sync
            Assert.Equal(800, s.HomeCupCapacityMl);
            Assert.Equal(new TimeSpan(23, 0, 0), s.QuietStart);
        });
    }

    [Fact]
    public void SplitCupsAndTokenAreNormalizedOnSave()
    {
        WithStorage((storage, _) =>
        {
            var s = new UserSettings
            {
                CupCapacityMl = 999, WorkCupCapacityMl = 370, HomeCupCapacityMl = 800,
                PushWardApiToken = " hlk_test ",
            };
            storage.SaveSettings(s);
            var loaded = storage.LoadSettings();
            Assert.Equal(370, loaded.CupCapacityMl);
            Assert.Equal(370, loaded.WorkCupCapacityMl);
            Assert.Equal(800, loaded.HomeCupCapacityMl);
            Assert.Equal("hlk_test", loaded.PushWardApiToken);
        });
    }

    [Theory]
    [InlineData(0, 0, 600, 600, 800)]     // zero -> fall back
    [InlineData(5000, 10, 600, 3000, 50)] // clamped 50..3000
    public void SplitCupsAreClamped(int work, int home, int legacy, int expectedWork, int expectedHome)
    {
        WithStorage((storage, dir) =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                $$"""{"CupCapacityMl":{{legacy}},"WorkCupCapacityMl":{{work}},"HomeCupCapacityMl":{{home}}}""");
            var s = storage.LoadSettings();
            Assert.Equal(expectedWork, s.WorkCupCapacityMl);
            Assert.Equal(expectedHome, s.HomeCupCapacityMl);
            Assert.Equal(expectedWork, s.CupCapacityMl);
        });
    }
}
