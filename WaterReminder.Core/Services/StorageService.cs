using System.Globalization;
using System.IO;
using System.Text.Json;

namespace WaterReminder.Services;

internal sealed class PhoneReplyState
{
    public string TokenHash { get; init; } = "";
    public string? Cursor { get; set; }
    public Dictionary<string, DateOnly> Sent { get; init; } = [];
    public Dictionary<string, string> DrinkActions { get; init; } = [];
}

public sealed class StorageService
{
    /// <summary>
    /// How many days of per-day record files are kept. Covers the 366-day history window
    /// with headroom; older files are pruned on save. The migrated legacy backup is never pruned.
    /// </summary>
    public const int MaxRecordDays = 400;

    private readonly string _directory;
    private readonly object _migrationLock = new();
    private bool _migrated;

    public StorageService() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaterReminder")) { }
    public StorageService(string directory) => _directory = directory;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };
    private string SettingsPath => Path.Combine(_directory, "settings.json");
    private string RecordsDirectory => Path.Combine(_directory, "records");
    /// <summary>Pre-split ledger: one JSON array with every day. Migrated on first access, never deleted.</summary>
    private string LegacyRecordsPath => Path.Combine(_directory, "daily-records.json");
    private string MigratedRecordsPath => LegacyRecordsPath + ".migrated";
    private string PhoneRepliesPath => Path.Combine(_directory, "phone-replies.json");
    private string DayPath(string date) => Path.Combine(RecordsDirectory, date + ".json");

    internal PhoneReplyState? LoadPhoneReplies()
    {
        if (!File.Exists(PhoneRepliesPath)) return null;
        try { return JsonSerializer.Deserialize<PhoneReplyState>(File.ReadAllText(PhoneRepliesPath)); }
        catch (JsonException) when (File.Exists(PhoneRepliesPath + ".bak"))
        {
            return JsonSerializer.Deserialize<PhoneReplyState>(File.ReadAllText(PhoneRepliesPath + ".bak"));
        }
    }

    internal void SavePhoneReplies(PhoneReplyState state)
        => AtomicWrite(PhoneRepliesPath, JsonSerializer.Serialize(state, _options));

    public UserSettings LoadSettings()
        => Normalize(ReadWithBackup<UserSettings>(SettingsPath));

    public DailyRecord LoadDailyRecord(DateOnly date)
    {
        EnsureMigrated();
        var key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = DayPath(key);
        if (File.Exists(path))
        {
            var record = ReadWithBackup<DailyRecord>(path);
            record.Date = key; // the file name is the authority for which day this is
            return record;
        }
        // Safety net: the day's file is missing (deleted, or restored from a legacy ledger),
        // but the legacy copy may still hold it.
        return ReadLegacyDay(key) ?? new DailyRecord { Date = key };
    }

    /// <summary>Every saved day, oldest first. Missing days are omitted.</summary>
    public IReadOnlyList<DailyRecord> ListDailyRecords()
    {
        EnsureMigrated();
        var records = new Dictionary<string, DailyRecord>(StringComparer.Ordinal);
        if (Directory.Exists(RecordsDirectory))
            foreach (var path in Directory.EnumerateFiles(RecordsDirectory, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!IsDayName(name)) continue;
                var record = ReadWithBackup<DailyRecord>(path);
                record.Date = name;
                records[name] = record;
            }
        // A legacy file that appeared after migration (e.g. restored while running):
        // its days are visible until they are saved per-day. The migrated backup is
        // deliberately not scanned here: parsing it on every list would reintroduce
        // the whole-ledger read this layout removes; LoadDailyRecord still falls back to it.
        foreach (var day in ReadLegacyDays(LegacyRecordsPath))
            records.TryAdd(day.Date, day);
        return records.Values.OrderBy(x => x.Date).ToList();
    }

    public void SaveSettings(UserSettings settings) => AtomicWrite(SettingsPath, JsonSerializer.Serialize(Normalize(settings), _options));

    public void SaveDailyRecord(DailyRecord record)
        => SaveDailyRecords([record]);

    internal void SaveDailyRecords(IEnumerable<DailyRecord> changes)
    {
        EnsureMigrated();
        foreach (var day in changes)
        {
            if (!IsDayName(day.Date))
                throw new ArgumentException($"DailyRecord.Date must be yyyy-MM-dd, got '{day.Date}'.", nameof(changes));
            AtomicWrite(DayPath(day.Date), JsonSerializer.Serialize(day, _options));
        }
        PruneOldRecords();
    }

    private static bool IsDayName(string name)
        => DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>
    /// One-time split of the legacy single-file ledger into per-day files. The legacy file
    /// is renamed to daily-records.json.migrated, never deleted, so the original data stays
    /// on disk as a backup. A corrupt legacy file still throws, as before: the caller sees
    /// the same JsonException the old whole-file load produced.
    /// </summary>
    private void EnsureMigrated()
    {
        lock (_migrationLock)
        {
            if (_migrated) return;
            if (!File.Exists(LegacyRecordsPath))
            {
                _migrated = true;
                return;
            }
            // ReadWithBackup recovers from daily-records.json.bak, and throws JsonException
            // when both copies are unreadable, exactly like the old whole-file load.
            var legacy = ReadWithBackup<List<DailyRecord>>(LegacyRecordsPath);
            foreach (var day in legacy)
            {
                if (!IsDayName(day.Date)) continue; // stays in the .migrated backup
                var path = DayPath(day.Date);
                if (File.Exists(path)) continue; // a per-day file is newer than the legacy copy
                AtomicWrite(path, JsonSerializer.Serialize(day, _options));
            }
            var migrated = MigratedRecordsPath;
            if (File.Exists(migrated))
                migrated = LegacyRecordsPath + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".migrated";
            try
            {
                File.Move(LegacyRecordsPath, migrated);
                if (File.Exists(LegacyRecordsPath + ".bak"))
                    File.Move(LegacyRecordsPath + ".bak", migrated + ".bak", true);
            }
            catch (FileNotFoundException)
            {
                // Another StorageService on the same directory migrated first.
            }
            _migrated = true;
        }
    }

    /// <summary>Safety net for a day whose per-day file is missing. Never throws: a damaged
    /// backup must not break reads of healthy per-day files.</summary>
    private DailyRecord? ReadLegacyDay(string key)
        => ReadLegacyDays(LegacyRecordsPath).FirstOrDefault(day => day.Date == key)
            ?? ReadLegacyDays(MigratedRecordsPath).FirstOrDefault(day => day.Date == key);

    private List<DailyRecord> ReadLegacyDays(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<DailyRecord>>(File.ReadAllText(path), _options) ?? [];
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Deletes per-day files (and their backups) older than the retention window.
    /// Runs on save only, never on read, so listing history never scans the disk twice.</summary>
    private void PruneOldRecords()
    {
        if (!Directory.Exists(RecordsDirectory)) return;
        // Keep the most recent MaxRecordDays days, today included.
        var oldestKept = DateOnly.FromDateTime(DateTime.Now).AddDays(-(MaxRecordDays - 1));
        foreach (var path in Directory.EnumerateFiles(RecordsDirectory, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || date >= oldestKept) continue;
            try
            {
                File.Delete(path);
                var backup = path + ".bak";
                if (File.Exists(backup)) File.Delete(backup);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Best effort: a locked old file must not fail the save that just succeeded.
            }
        }
    }

    private T ReadWithBackup<T>(string path) where T : class, new()
    {
        T Parse(string text) => JsonSerializer.Deserialize<T>(text, _options)
            ?? throw new JsonException($"Invalid data in {Path.GetFileName(path)}.");
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            if (!File.Exists(path + ".bak"))
            {
                if (error is JsonException) throw;
                return new T();
            }
            var backup = File.ReadAllText(path + ".bak");
            var recovered = Parse(backup);
            File.WriteAllText(path + ".recover", backup);
            File.Move(path + ".recover", path, true);
            return recovered;
        }
    }

    private void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        File.Move(temp, path, true);
    }

    private static UserSettings Normalize(UserSettings s)
    {
        s.DailyGoalMl = Math.Clamp(s.DailyGoalMl, 100, 10000);
        // Old settings.json has no WorkCupCapacityMl: keep using CupCapacityMl for work hours.
        if (!s.WorkCupCapacityWasSet || s.WorkCupCapacityMl <= 0)
            s.WorkCupCapacityMl = s.CupCapacityMl > 0 ? s.CupCapacityMl : 370;
        s.WorkCupCapacityMl = Math.Clamp(s.WorkCupCapacityMl, 50, 3000);
        if (s.HomeCupCapacityMl <= 0) s.HomeCupCapacityMl = 800;
        s.HomeCupCapacityMl = Math.Clamp(s.HomeCupCapacityMl, 50, 3000);
        // Keep the legacy field in sync so old code/JSON still sees the work cup.
        s.CupCapacityMl = s.WorkCupCapacityMl;

        if (s.MinReminderGapMinutes <= 0) s.MinReminderGapMinutes = (int)WaterCalculator.MinReminderGap.TotalMinutes;
        s.MinReminderGapMinutes = Math.Clamp(s.MinReminderGapMinutes, 5, 180);
        if (s.ActiveDays is null || s.ActiveDays.Length != 7) s.ActiveDays = [true, true, true, true, true, false, false];
        // Old settings.json files store PhoneMode as a number; the retired values 1
        // and 3 no longer exist, so they fall back to Auto instead of staying undefined.
        if (!Enum.IsDefined(s.PhoneMode)) s.PhoneMode = PhoneDeliveryMode.Auto;
        s.PushWardApiToken = (s.PushWardApiToken ?? "").Trim();
        // Removed channel/ntfy/Pushover keys in an old settings.json are simply ignored by
        // System.Text.Json and disappear the next time settings are saved.
        return s;
    }
}
