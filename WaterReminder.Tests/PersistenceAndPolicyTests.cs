using System.Globalization;
using System.IO;
using System.Text.Json;
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

    private static void WithTempDir(Action<string> action)
    {
        var dir = TempDir();
        try { Directory.CreateDirectory(dir); action(dir); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static string DayKey(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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

    // ---------- per-day record files ----------

    [Fact]
    public void LegacyLedger_MigratesToPerDayFiles_AndKeepsLegacyAsBackup()
    {
        WithTempDir(dir =>
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var first = today.AddDays(-2);
            var second = today.AddDays(-1);
            var legacy = new List<DailyRecord>
            {
                new() { Date = DayKey(first), Events = [new WaterEvent { AmountMl = 240 }] },
                new() { Date = DayKey(second), Events = [new WaterEvent { AmountMl = 500 }] },
            };
            File.WriteAllText(Path.Combine(dir, "daily-records.json"), JsonSerializer.Serialize(legacy));

            var storage = new StorageService(dir);
            Assert.Equal(240, storage.LoadDailyRecord(first).ConsumedMl);
            Assert.Equal(500, storage.LoadDailyRecord(second).ConsumedMl);
            Assert.Equal(new[] { DayKey(first), DayKey(second) }, storage.ListDailyRecords().Select(day => day.Date).ToArray());

            Assert.True(File.Exists(Path.Combine(dir, "records", DayKey(first) + ".json")));
            Assert.True(File.Exists(Path.Combine(dir, "records", DayKey(second) + ".json")));
            Assert.False(File.Exists(Path.Combine(dir, "daily-records.json")));
            Assert.True(File.Exists(Path.Combine(dir, "daily-records.json.migrated")));

            // A second instance on the same directory sees the migrated files, not the legacy one.
            var reopened = new StorageService(dir);
            Assert.Equal(240, reopened.LoadDailyRecord(first).ConsumedMl);
            Assert.Equal(2, reopened.ListDailyRecords().Count);
        });
    }

    [Fact]
    public void LegacyLedger_MissingDayFile_FallsBackToMigratedBackup()
    {
        WithTempDir(dir =>
        {
            var day = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);
            var legacy = new List<DailyRecord>
            {
                new() { Date = DayKey(day), Events = [new WaterEvent { AmountMl = 240 }] },
            };
            File.WriteAllText(Path.Combine(dir, "daily-records.json"), JsonSerializer.Serialize(legacy));
            var storage = new StorageService(dir);
            Assert.Equal(240, storage.LoadDailyRecord(day).ConsumedMl); // migrates

            File.Delete(Path.Combine(dir, "records", DayKey(day) + ".json"));

            // After a restart the day is still readable from the migrated backup.
            var reopened = new StorageService(dir);
            Assert.Equal(240, reopened.LoadDailyRecord(day).ConsumedMl);
        });
    }

    [Fact]
    public void LegacyMigration_KeepsExistingPerDayFile_OverLegacyCopy()
    {
        WithTempDir(dir =>
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var day = today.AddDays(-1);
            var other = today.AddDays(-2);
            // A per-day file written by an interrupted earlier migration wins over the legacy copy.
            Directory.CreateDirectory(Path.Combine(dir, "records"));
            File.WriteAllText(Path.Combine(dir, "records", DayKey(day) + ".json"),
                JsonSerializer.Serialize(new DailyRecord { Date = DayKey(day), Events = [new WaterEvent { AmountMl = 300 }] }));
            var legacy = new List<DailyRecord>
            {
                new() { Date = DayKey(day), Events = [new WaterEvent { AmountMl = 240 }] },
                new() { Date = DayKey(other), Events = [new WaterEvent { AmountMl = 500 }] },
            };
            File.WriteAllText(Path.Combine(dir, "daily-records.json"), JsonSerializer.Serialize(legacy));

            var storage = new StorageService(dir);
            Assert.Equal(300, storage.LoadDailyRecord(day).ConsumedMl);
            Assert.Equal(500, storage.LoadDailyRecord(other).ConsumedMl);
            Assert.True(File.Exists(Path.Combine(dir, "daily-records.json.migrated")));
        });
    }

    [Fact]
    public void SaveDailyRecord_LeavesOtherDaysUntouched()
    {
        WithTempDir(dir =>
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var other = today.AddDays(-1);
            var storage = new StorageService(dir);
            storage.SaveDailyRecord(new DailyRecord { Date = DayKey(other), Events = [new WaterEvent { AmountMl = 240 }] });
            storage.SaveDailyRecord(new DailyRecord { Date = DayKey(today), Events = [new WaterEvent { AmountMl = 500 }] });
            var otherPath = Path.Combine(dir, "records", DayKey(other) + ".json");
            var before = File.ReadAllBytes(otherPath);
            var beforeModified = File.GetLastWriteTimeUtc(otherPath);

            storage.SaveDailyRecord(new DailyRecord { Date = DayKey(today), Events = [new WaterEvent { AmountMl = 700 }] });

            Assert.Equal(before, File.ReadAllBytes(otherPath));
            Assert.Equal(beforeModified, File.GetLastWriteTimeUtc(otherPath));
            Assert.Equal(700, storage.LoadDailyRecord(today).ConsumedMl);
        });
    }

    [Fact]
    public void SaveDailyRecord_PrunesFilesOlderThanRetentionWindow()
    {
        WithTempDir(dir =>
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var keep = today.AddDays(-(StorageService.MaxRecordDays - 1)); // oldest kept day
            var drop = today.AddDays(-StorageService.MaxRecordDays);      // one day too old
            var ancient = today.AddDays(-500);
            // Seed files directly: saving through StorageService would prune them immediately.
            var recordsDir = Path.Combine(dir, "records");
            Directory.CreateDirectory(recordsDir);
            foreach (var date in new[] { keep, drop, ancient })
                File.WriteAllText(Path.Combine(recordsDir, DayKey(date) + ".json"),
                    JsonSerializer.Serialize(new DailyRecord { Date = DayKey(date), Events = [new WaterEvent { AmountMl = 100 }] }));
            // A stale backup of a pruned day is removed with it.
            File.WriteAllText(Path.Combine(recordsDir, DayKey(ancient) + ".json.bak"), "{}");

            var storage = new StorageService(dir);
            Assert.Equal(0, storage.LoadDailyRecord(today).ConsumedMl);
            Assert.Equal(3, storage.ListDailyRecords().Count);
            Assert.True(File.Exists(Path.Combine(recordsDir, DayKey(ancient) + ".json"))); // reads never prune

            storage.SaveDailyRecord(new DailyRecord { Date = DayKey(today) });

            Assert.True(File.Exists(Path.Combine(recordsDir, DayKey(keep) + ".json")));
            Assert.False(File.Exists(Path.Combine(recordsDir, DayKey(drop) + ".json")));
            Assert.False(File.Exists(Path.Combine(recordsDir, DayKey(ancient) + ".json")));
            Assert.False(File.Exists(Path.Combine(recordsDir, DayKey(ancient) + ".json.bak")));
            Assert.Equal(100, storage.LoadDailyRecord(keep).ConsumedMl);
            Assert.Equal(2, storage.ListDailyRecords().Count);
        });
    }

    // ---------- corrupt JSON: keep the last good file, log an error ----------

    private static string ReadLog(string logDir)
    {
        var path = Path.Combine(logDir, $"waterreminder-{DateTime.Now:yyyy-MM-dd}.log");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    [Fact]
    public void CorruptSettings_RecoversFromBackup_AndLogsError()
    {
        WithTempDir(dir =>
        {
            var logDir = Path.Combine(dir, "logs");
            var storage = new StorageService(dir, new FileLogger(logDir));
            var first = Mine(); first.DailyGoalMl = 1111;
            var second = Mine(); second.DailyGoalMl = 2222;
            storage.SaveSettings(first);
            storage.SaveSettings(second); // writes settings.json.bak with the first version
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{ this is not json");

            var recovered = storage.LoadSettings();

            Assert.Equal(1111, recovered.DailyGoalMl); // last good file wins
            Assert.True(File.Exists(Path.Combine(dir, "settings.json.bak")));
            Assert.DoesNotContain("not json", File.ReadAllText(Path.Combine(dir, "settings.json")));
            var log = ReadLog(logDir);
            Assert.Contains("[ERROR]", log);
            Assert.Contains("settings.json", log);
            Assert.Contains("restored from backup", log);
            Assert.DoesNotContain("hlk_test_token", log); // the fake PushWard token must never reach the log
        });
    }

    [Fact]
    public void CorruptSettingsWithoutBackup_Throws_KeepsFile_AndLogsError()
    {
        WithTempDir(dir =>
        {
            var logDir = Path.Combine(dir, "logs");
            const string corrupt = "{ broken settings";
            File.WriteAllText(Path.Combine(dir, "settings.json"), corrupt);

            var storage = new StorageService(dir, new FileLogger(logDir));
            Assert.Throws<JsonException>(() => storage.LoadSettings());

            Assert.Equal(corrupt, File.ReadAllText(Path.Combine(dir, "settings.json"))); // kept for inspection
            var log = ReadLog(logDir);
            Assert.Contains("[ERROR]", log);
            Assert.Contains("settings.json", log);
            Assert.Contains("no backup", log);
        });
    }

    [Fact]
    public void CorruptSettingsAndBackup_Throws_AndKeepsBothFiles()
    {
        WithTempDir(dir =>
        {
            var logDir = Path.Combine(dir, "logs");
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{ broken");
            File.WriteAllText(Path.Combine(dir, "settings.json.bak"), "{ also broken");
            var storage = new StorageService(dir, new FileLogger(logDir));

            Assert.Throws<JsonException>(() => storage.LoadSettings());

            Assert.Equal("{ broken", File.ReadAllText(Path.Combine(dir, "settings.json")));
            Assert.Equal("{ also broken", File.ReadAllText(Path.Combine(dir, "settings.json.bak")));
            Assert.Contains("both unreadable", ReadLog(logDir));
        });
    }

    [Fact]
    public void CorruptDailyRecord_RecoversFromBackup_AndLogsError()
    {
        WithTempDir(dir =>
        {
            var logDir = Path.Combine(dir, "logs");
            var storage = new StorageService(dir, new FileLogger(logDir));
            var today = DateOnly.FromDateTime(DateTime.Now);
            storage.SaveDailyRecord(new DailyRecord { Date = DayKey(today), Events = [new WaterEvent { AmountMl = 240 }] });
            storage.SaveDailyRecord(new DailyRecord { Date = DayKey(today), Events = [new WaterEvent { AmountMl = 500 }] });
            var path = Path.Combine(dir, "records", DayKey(today) + ".json");
            File.WriteAllText(path, "not json at all");

            var recovered = storage.LoadDailyRecord(today);

            Assert.Equal(240, recovered.ConsumedMl);
            var log = ReadLog(logDir);
            Assert.Contains("[ERROR]", log);
            Assert.Contains(DayKey(today) + ".json", log);
            Assert.Contains("restored from backup", log);
        });
    }

    [Fact]
    public void CorruptLegacyLedger_LogsError_AndKeepsFile()
    {
        WithTempDir(dir =>
        {
            var logDir = Path.Combine(dir, "logs");
            File.WriteAllText(Path.Combine(dir, "daily-records.json"), "[ broken ledger");
            var storage = new StorageService(dir, new FileLogger(logDir));

            Assert.Throws<JsonException>(() => storage.LoadDailyRecord(DateOnly.FromDateTime(DateTime.Now)));

            Assert.Equal("[ broken ledger", File.ReadAllText(Path.Combine(dir, "daily-records.json")));
            var log = ReadLog(logDir);
            Assert.Contains("[ERROR]", log);
            Assert.Contains("daily-records.json", log);
        });
    }
}
