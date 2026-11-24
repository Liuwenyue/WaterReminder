using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WaterReminder;
using WaterReminder.Services;

namespace WaterReminder.Server;

internal sealed class FamilyAccounts : IDisposable
{
    private sealed record Entry(Guid Id, string Name, string CodeHash, bool Archived = false, bool SharedWithOwner = true)
    {
        public FamilyMember View => new(Id, Name, Archived);
    }

    private readonly ServerOptions _options;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<Guid, ReminderEngine> _engines = [];
    private List<Entry> _entries;
    public ReminderEngine Owner { get; }

    public FamilyAccounts(ServerOptions options, ReminderEngine owner)
    {
        _options = options;
        Owner = owner;
        _path = Path.Combine(options.DataDirectory, "family.json");
        _entries = File.Exists(_path) ? ReadEntries(File.ReadAllText(_path)) : [];
    }

    public FamilyOverview Overview(Guid caller)
    {
        lock (_gate)
        {
            return caller == Guid.Empty
                ? new(true, [new(Guid.Empty, "我"), .. _entries.Where(entry => entry.SharedWithOwner).Select(entry => entry.View)])
                : new(false, [_entries.Single(entry => entry.Id == caller).View]);
        }
    }

    // Owner management endpoints must not reveal accounts created for independent use.
    public bool OwnerCanAccess(Guid id)
    {
        lock (_gate) return _entries.Any(entry => entry.Id == id && entry.SharedWithOwner);
    }

    public Guid? Authenticate(HttpRequest request)
    {
        if (ServerHost.Authorized(request, _options.ApiToken)) return Guid.Empty;
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer wr_", StringComparison.OrdinalIgnoreCase) || header.Length != 74)
            return null;

        var hash = Hash(header[7..]);
        lock (_gate)
        {
            foreach (var entry in _entries.Where(item => !item.Archived))
            {
                if (CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(entry.CodeHash)))
                    return entry.Id;
            }
            return null;
        }
    }

    public ReminderEngine? Find(Guid id, bool includeArchived = false)
    {
        if (id == Guid.Empty) return Owner;
        lock (_gate)
        {
            var entry = _entries.Find(item => item.Id == id);
            return entry is not null && (includeArchived || !entry.Archived) ? Engine(entry) : null;
        }
    }

    public IReadOnlyList<ReminderEngine> ActiveEngines()
    {
        lock (_gate)
            return [Owner, .. _entries.Where(entry => !entry.Archived).Select(Engine)];
    }

    public FamilyInvitation? Add(AddFamilyMember request, out string? error)
    {
        var name = request.Name?.Trim();
        var push = request.PushWardToken?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name) || name.Length > 32 || name.Any(char.IsControl))
        {
            error = "请输入 1–32 个字符的成员名称。";
            return null;
        }
        if (!ValidPushToken(push))
        {
            error = "手机推送密钥格式无效。";
            return null;
        }

        lock (_gate)
        {
            if (_entries.Count >= 50 || _entries.Any(entry => !entry.Archived && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                error = "成员名称已存在，或已达到 50 人上限。";
                return null;
            }
            if (PushTokenInUse(push))
            {
                error = "每位成员须使用自己手机的 PushWard 密钥。";
                return null;
            }

            var code = NewAccessCode();
            var entry = new Entry(Guid.NewGuid(), name, Convert.ToHexString(Hash(code)));
            var settings = new UserSettings { PushWardApiToken = push };
            new StorageService(MemberDirectory(entry.Id)).SaveSettings(settings);
            Save([.. _entries, entry]);
            error = null;
            return new FamilyInvitation(entry.View, code);
        }
    }

    /// <summary>
    /// Owner-only: rotates a member's desktop access code. The old code stops working
    /// immediately; the member's ledger and settings are untouched. Returns null for
    /// a missing, archived, or independently used member.
    /// </summary>
    public FamilyInvitation? ResetAccessCode(Guid id)
    {
        lock (_gate)
        {
            var entry = _entries.Find(item => item.Id == id && !item.Archived && item.SharedWithOwner);
            if (entry is null) return null;
            var code = NewAccessCode();
            Save(_entries.Select(item => item.Id == id ? item with { CodeHash = Convert.ToHexString(Hash(code)) } : item).ToList());
            return new FamilyInvitation(entry.View, code);
        }
    }

    private static string NewAccessCode()
        => "wr_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public FamilyProgressOverview Progress()
    {
        lock (_gate)
        {
            var today = DateOnly.ParseExact(Owner.Status().Date, "yyyy-MM-dd");
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            var members = Overview(Guid.Empty).Members.Where(member => !member.Archived).Select(member =>
            {
                var engine = Find(member.Id)!;
                var status = engine.Status();
                var days = engine.History(7).Days.Where(day => string.CompareOrdinal(day.Date, monday.ToString("yyyy-MM-dd")) >= 0).ToList();
                var last = engine.History(365).Days.Where(day => day.EventCount > 0).OrderByDescending(day => day.Date).FirstOrDefault();
                var lastAt = last is null ? null : engine.RecordsFor(last.Date)?.Events.OrderByDescending(item => item.At).FirstOrDefault()?.At;
                return new FamilyProgress(member, status.ConsumedMl, status.GoalMl, lastAt,
                    days.Count(day => day.GoalMl > 0 && day.ConsumedMl >= day.GoalMl), days.Count(day => day.EventCount > 0), engine.SafePushFeedback());
            }).ToList();
            return new(monday.ToString("yyyy-MM-dd"), members);
        }
    }

    public ClientSettingsResponse? MemberSettings(Guid id)
    {
        lock (_gate)
        {
            var entry = _entries.Find(item => item.Id == id && !item.Archived && item.SharedWithOwner);
            return entry is null ? null : Engine(entry).ClientSettings();
        }
    }

    public ClientSettingsResponse? UpdateMemberSettings(Guid id, FamilySettingsUpdate request, out string? error)
    {
        lock (_gate)
        {
            error = null;
            var entry = _entries.Find(item => item.Id == id && !item.Archived && item.SharedWithOwner);
            if (entry is null) return null;
            var push = request.PushWardToken?.Trim() ?? "";
            if (!ValidPushToken(push))
            {
                error = "手机推送密钥格式无效。";
                return null;
            }
            if (PushTokenInUse(push, id))
            {
                error = "每位成员须使用自己手机的 PushWard 密钥。";
                return null;
            }
            var engine = Engine(entry);
            return engine.TryUpdateSettings(request.Settings, out error, push) ? engine.ClientSettings() : null;
        }
    }

    private static bool ValidPushToken(string token)
        => token.Length <= 512 && !token.Any(char.IsWhiteSpace) && !token.Any(char.IsControl);

    // Call under _gate so concurrent adds/updates cannot assign the same token.
    private bool PushTokenInUse(string token, Guid? except = null)
        => token.Length > 0 && (token == Owner.PhonePushToken ||
            _entries.Any(entry => !entry.Archived && entry.Id != except &&
                token == new StorageService(MemberDirectory(entry.Id)).LoadSettings().PushWardApiToken));

    public bool Archive(Guid id)
    {
        lock (_gate)
        {
            var entry = _entries.Find(item => item.Id == id && item.SharedWithOwner);
            if (entry is null) return false;
            if (entry.Archived) return true;
            Save(_entries.Select(item => item.Id == id ? item with { Archived = true } : item).ToList());
            if (_engines.TryGetValue(id, out var engine)) engine.Retire();
            return true;
        }
    }

    private ReminderEngine Engine(Entry entry)
    {
        if (_engines.TryGetValue(entry.Id, out var existing)) return existing;
        var engine = new ReminderEngine(new ReminderEngineOptions
        {
            DataDirectory = MemberDirectory(entry.Id),
            PollAnswers = _options.RunScheduler && !entry.Archived,
        });
        if (entry.Archived) engine.Retire();
        _engines.Add(entry.Id, engine);
        return engine;
    }

    private string MemberDirectory(Guid id) => Path.Combine(_options.DataDirectory, "members", id.ToString("N"));

    private void Save(List<Entry> entries)
    {
        Directory.CreateDirectory(_options.DataDirectory);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries));
        File.Move(temp, _path, overwrite: true);
        _entries = entries;
    }

    private sealed class EntryDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string CodeHash { get; set; } = "";
        public bool Archived { get; set; }
        public bool SharedWithOwner { get; set; } = true;
    }

    private static List<Entry> ReadEntries(string json)
        => JsonSerializer.Deserialize<List<EntryDto>>(json)?.Select(item => new Entry(
            item.Id, item.Name, item.CodeHash, item.Archived, item.SharedWithOwner)).ToList()
            ?? throw new InvalidDataException("Invalid family registry");

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    public void Dispose()
    {
        lock (_gate)
            foreach (var engine in _engines.Values) engine.Dispose();
    }
}
