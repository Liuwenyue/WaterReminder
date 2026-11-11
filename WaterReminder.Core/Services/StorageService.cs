using System.IO;
using System.Text.Json;

namespace WaterReminder.Services;

public sealed class StorageService
{
    private readonly string _directory;

    public StorageService() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaterReminder")) { }
    public StorageService(string directory) => _directory = directory;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };
    private string SettingsPath => Path.Combine(_directory, "settings.json");
    private string RecordsPath => Path.Combine(_directory, "daily-records.json");
    private string BackupPath => RecordsPath + ".bak";

    public UserSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var value = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath), _options);
                if (value is not null) return Normalize(value);
            }
        }
        catch { }
        return new UserSettings();
    }

    public DailyRecord LoadDailyRecord(DateOnly date)
    {
        var key = date.ToString("yyyy-MM-dd");
        return LoadAllRecords().FirstOrDefault(x => x.Date == key) ?? new DailyRecord { Date = key };
    }

    public void SaveSettings(UserSettings settings) => AtomicWrite(SettingsPath, JsonSerializer.Serialize(Normalize(settings), _options));

    public void SaveDailyRecord(DailyRecord record)
    {
        var records = LoadAllRecords();
        var index = records.FindIndex(x => x.Date == record.Date);
        if (index >= 0) records[index] = record; else records.Add(record);
        AtomicWrite(RecordsPath, JsonSerializer.Serialize(records.OrderBy(x => x.Date).ToList(), _options));
    }

    private List<DailyRecord> LoadAllRecords()
    {
        try
        {
            if (File.Exists(RecordsPath)) return JsonSerializer.Deserialize<List<DailyRecord>>(File.ReadAllText(RecordsPath), _options) ?? [];
        }
        catch
        {
            try { if (File.Exists(BackupPath)) return JsonSerializer.Deserialize<List<DailyRecord>>(File.ReadAllText(BackupPath), _options) ?? []; } catch { }
        }
        return [];
    }

    private void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(_directory);
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

        if (s.ActiveDays is null || s.ActiveDays.Length != 7) s.ActiveDays = [true, true, true, true, true, false, false];
        s.PushWardApiToken = (s.PushWardApiToken ?? "").Trim();
        // Removed channel/ntfy/Pushover keys in an old settings.json are simply ignored by
        // System.Text.Json and disappear the next time settings are saved.
        return s;
    }
}
