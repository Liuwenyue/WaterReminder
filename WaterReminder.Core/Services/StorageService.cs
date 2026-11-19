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
    private readonly string _directory;

    public StorageService() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaterReminder")) { }
    public StorageService(string directory) => _directory = directory;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };
    private string SettingsPath => Path.Combine(_directory, "settings.json");
    private string RecordsPath => Path.Combine(_directory, "daily-records.json");
    private string PhoneRepliesPath => Path.Combine(_directory, "phone-replies.json");

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
        var key = date.ToString("yyyy-MM-dd");
        return LoadAllRecords().FirstOrDefault(x => x.Date == key) ?? new DailyRecord { Date = key };
    }

    /// <summary>Every saved day, oldest first. Missing days are omitted.</summary>
    public IReadOnlyList<DailyRecord> ListDailyRecords()
        => LoadAllRecords().OrderBy(x => x.Date).ToList();

    public void SaveSettings(UserSettings settings) => AtomicWrite(SettingsPath, JsonSerializer.Serialize(Normalize(settings), _options));

    public void SaveDailyRecord(DailyRecord record)
        => SaveDailyRecords([record]);

    internal void SaveDailyRecords(IEnumerable<DailyRecord> changes)
    {
        var records = LoadAllRecords().ToDictionary(day => day.Date);
        foreach (var day in changes) records[day.Date] = day;
        AtomicWrite(RecordsPath, JsonSerializer.Serialize(records.Values.OrderBy(day => day.Date), _options));
    }

    private List<DailyRecord> LoadAllRecords()
        => ReadWithBackup<List<DailyRecord>>(RecordsPath);

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

        if (s.MinReminderGapMinutes <= 0) s.MinReminderGapMinutes = (int)WaterCalculator.MinReminderGap.TotalMinutes;
        s.MinReminderGapMinutes = Math.Clamp(s.MinReminderGapMinutes, 5, 180);
        if (s.ActiveDays is null || s.ActiveDays.Length != 7) s.ActiveDays = [true, true, true, true, true, false, false];
        s.PushWardApiToken = (s.PushWardApiToken ?? "").Trim();
        // Removed channel/ntfy/Pushover keys in an old settings.json are simply ignored by
        // System.Text.Json and disappear the next time settings are saved.
        return s;
    }
}
