using System.IO;
using WaterReminder;
using WaterReminder.Services;
using Xunit;

namespace WaterReminder.Tests;

/// <summary>
/// Persisted reminder time, phone-push delivery/backoff policy, bounded replay cache,
/// record labels and the dynamic settings-page summary.
/// </summary>
public sealed class PersistenceAndPolicyTests
{
    private static readonly DateTime Day = new(2026, 11, 3); // Tuesday

    private static UserSettings Mine() => new()
    {
        DailyGoalMl = 2220, CupCapacityMl = 370, WorkCupCapacityMl = 370, HomeCupCapacityMl = 800,
        WorkStart = new(8, 30, 0), WorkEnd = new(18, 0, 0),
        LunchStart = new(12, 0, 0), LunchEnd = new(13, 30, 0),
        QuietStart = new(23, 0, 0),
        ActiveDays = [true, true, true, true, true, false, false],
        MobilePushEnabled = true,
        PushWardApiToken = "hlk_test_token",
    };

    private static DateTime T(int h, int m) => Day.AddHours(h).AddMinutes(m);

    private static string TempDir()
        => Path.Combine(Path.GetTempPath(), "WaterReminderTests", Guid.NewGuid().ToString("N"));

    private static void WithStorage(Action<StorageService> action)
    {
        var dir = TempDir();
        try { action(new StorageService(dir)); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------- LastReminderAt persistence ----------

    [Fact]
    public void MarkReminded_SavesLastReminderAt_AndRestartRestoresIt()
    {
        WithStorage(storage =>
        {
            var now = T(10, 0);
            var tracker = new ProgressTracker(Mine(), storage.LoadDailyRecord, storage.SaveDailyRecord, () => now);

            tracker.MarkReminded(now);

            var saved = storage.LoadDailyRecord(DateOnly.FromDateTime(now));
            Assert.Equal(now, saved.LastReminderAt); // serializable DailyRecord field

            // Simulate a restart two minutes later: a brand-new tracker loads the same file.
            var restarted = new ProgressTracker(Mine(), storage.LoadDailyRecord, storage.SaveDailyRecord, () => now.AddMinutes(2));
            Assert.Equal(now, restarted.LastReminderAt);
            Assert.Equal(now, restarted.Today.LastReminderAt);

            var expected = WaterCalculator.CalculateDynamic(Mine(), restarted.Today, now.AddMinutes(2), now);
            Assert.Equal(expected.NextReminder, restarted.Plan().NextReminder);
            Assert.True(restarted.Plan().NextReminder >= now + WaterCalculator.MinReminderGap,
                "a restart must not allow an immediate repeat reminder");
        });
    }

    [Fact]
    public void CrossDay_ClearsLastReminderAt()
    {
        var disk = new Dictionary<string, DailyRecord>();
        string Key(DateOnly d) => d.ToString("yyyy-MM-dd");
        DailyRecord Load(DateOnly d) => disk.TryGetValue(Key(d), out var r)
            ? System.Text.Json.JsonSerializer.Deserialize<DailyRecord>(System.Text.Json.JsonSerializer.Serialize(r))!
            : new DailyRecord { Date = Key(d) };
        void Save(DailyRecord r) => disk[r.Date] = System.Text.Json.JsonSerializer.Deserialize<DailyRecord>(
            System.Text.Json.JsonSerializer.Serialize(r))!;

        var now = T(22, 0);
        var tracker = new ProgressTracker(Mine(), Load, Save, () => now);
        tracker.MarkReminded(now);
        Assert.Equal(now, tracker.LastReminderAt);

        now = Day.AddDays(1).AddHours(8).AddMinutes(30); // next morning
        tracker.Reload();
        Assert.Null(tracker.LastReminderAt);
        Assert.Null(tracker.Today.LastReminderAt);
    }

    // ---------- evening phone push: short failure backoff, mark only on success ----------

    [Fact]
    public void MobilePushFailureDoesNotMark_AndBacksOffForOneToTwoMinutes()
    {
        Assert.InRange(MobilePushDeliveryPolicy.FailureBackoff, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));

        var at = T(19, 0);
        var outcome = MobilePushDeliveryPolicy.AfterPublish(published: false, at);
        Assert.False(outcome.MarkReminded);                      // failed push must not burn the 20-minute gap
        Assert.Equal(at + MobilePushDeliveryPolicy.FailureBackoff, outcome.NextAttempt);
        Assert.False(MobilePushDeliveryPolicy.ShouldAttempt(at.AddSeconds(30), outcome.NextAttempt, inFlight: false));
        Assert.True(MobilePushDeliveryPolicy.ShouldAttempt(outcome.NextAttempt, outcome.NextAttempt, inFlight: false));
        Assert.False(MobilePushDeliveryPolicy.ShouldAttempt(outcome.NextAttempt, outcome.NextAttempt, inFlight: true));
    }

    [Fact]
    public void MobilePushSuccessMarksReminder_AndDoesNotBackOff()
    {
        var outcome = MobilePushDeliveryPolicy.AfterPublish(published: true, T(19, 0));
        Assert.True(outcome.MarkReminded);
        Assert.Equal(DateTime.MinValue, outcome.NextAttempt);
    }

    // ---------- bounded replay cache ----------

    [Fact]
    public void SeenIds_EvictsOnlyTheOldest()
    {
        var cache = new SeenIdCache(capacity: 3);
        Assert.True(cache.Add("a"));
        Assert.True(cache.Add("b"));
        Assert.True(cache.Add("c"));
        Assert.True(cache.Add("d")); // evicts "a" only; the rest of the window still deduplicates

        Assert.Equal(3, cache.Count);
        Assert.False(cache.Contains("a"));
        Assert.True(cache.Contains("b"));
        Assert.True(cache.Contains("c"));
        Assert.True(cache.Contains("d"));
        Assert.False(cache.Add("d")); // duplicate replay

        Assert.True(cache.Add("a")); // an old id may be seen again after eviction
        Assert.Equal(3, cache.Count);
    }

    // ---------- record labels ----------

    [Theory]
    [InlineData("pushward", 800, "手机 Pushward")]
    [InlineData("cup", 370, "喝一杯")]
    [InlineData("custom", -100, "手动修正")]
    [InlineData("adjust", 50, "修正")]
    [InlineData(null, 100, "手动记录")]
    public void RecordLabels_DescribeSources(string? source, int amount, string expected)
        => Assert.Equal(expected, RecordLabels.Describe(source, amount));

    // ---------- reminder routing is PushWard-only ----------

    [Theory]
    [InlineData(true, true, true)]   // enabled + token -> publish through PushWard
    [InlineData(true, false, false)] // no token -> do not publish
    [InlineData(false, true, false)] // push disabled -> do not publish
    public void ReminderService_RoutesPhonePushToPushWardWhenEnabledAndConfigured(
        bool enabled, bool configured, bool expected)
        => Assert.Equal(expected,
            ReminderRouting.ShouldPublishPushWard(new UserSettings { MobilePushEnabled = enabled }, configured));

    // ---------- settings page summary follows CalculateDynamic ----------

    [Fact]
    public void PlanSummary_AfterWork_DescribesPhoneReminderInsteadOfTimetable()
    {
        var text = PlanSummaryText.Describe(Mine(), new DailyRecord { Date = "2026-11-03" }, T(18, 0));
        Assert.Contains("手机 Pushward", text);
        Assert.Contains("家庭杯 800 ml", text);
        Assert.Contains("19:26", text);                 // dynamic next reminder
        Assert.DoesNotContain("每天工作时段提醒", text);
    }

    [Fact]
    public void PlanSummary_DuringWork_ShowsDynamicGapAndCup()
    {
        var text = PlanSummaryText.Describe(Mine(), new DailyRecord { Date = "2026-11-03" }, T(10, 0));
        Assert.Contains("工作杯 370 ml", text);
        Assert.Contains("预留家庭杯 800 ml", text);
        Assert.Contains("间隔约", text);
        Assert.Contains("工作时段桌面和手机会同时提醒", text);
        Assert.DoesNotContain("09:40", text);           // no fixed timetable
    }

    [Fact]
    public void PlanSummary_RestDayAndNoDrinkWindow()
    {
        Assert.Contains("今天休息", PlanSummaryText.Describe(Mine(), new DailyRecord(), new DateTime(2026, 11, 7, 10, 0, 0)));
        Assert.Contains("下班前不喝水", PlanSummaryText.Describe(Mine(), new DailyRecord(), T(17, 45)));
    }

    [Fact]
    public void PlanSummary_GoalReached()
    {
        var record = new DailyRecord { Date = "2026-11-03", Events = [new WaterEvent { AmountMl = 2220, At = T(9, 0) }] };
        Assert.Contains("今日目标已完成", PlanSummaryText.Describe(Mine(), record, T(10, 0)));
    }
}
