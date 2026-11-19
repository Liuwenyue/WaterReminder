using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WaterReminder;
using WaterReminder.Services;

namespace WaterReminder.Server;

internal sealed class FamilyAccounts : IDisposable
{
    private sealed record Entry(Guid Id, string Name, string CodeHash, bool Archived = false, string? InviteHash = null, DateTimeOffset? InviteExpiresAt = null, string? InviteServerUrl = null, string? WeChatIdentityHash = null, bool SharedWithOwner = true, List<string>? WeChatSessionHashes = null, string? UsedShareHash = null)
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

    private sealed record ShareInvite(string Hash, DateTimeOffset ExpiresAt);
    internal sealed record WeChatLoginResult(string AccessCode, Guid MemberId, string MemberName, bool SharedWithOwner);

    private string SharePath => Path.Combine(_options.DataDirectory, "wechat-shares.json");

    public string CreateWeChatShare()
    {
        lock (_gate)
        {
            var invitations = ReadShares().Where(i => i.ExpiresAt > DateTimeOffset.UtcNow).TakeLast(99).ToList();
            var token = "ws_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            invitations.Add(new(Convert.ToHexString(Hash(token)), DateTimeOffset.UtcNow.AddDays(1)));
            Directory.CreateDirectory(_options.DataDirectory);
            File.WriteAllText(SharePath + ".tmp", JsonSerializer.Serialize(invitations));
            File.Move(SharePath + ".tmp", SharePath, overwrite: true);
            return token;
        }
    }

    private List<ShareInvite> ReadShares()
        => File.Exists(SharePath)
            ? JsonSerializer.Deserialize<List<ShareInvite>>(File.ReadAllText(SharePath)) ?? []
            : [];

    public WeChatLoginResult? LogInWeChat(string openId, string? shareToken, Guid? expectedMemberId, out string? error)
        => LogInIdentity(openId, shareToken, true, expectedMemberId, out error);

    public WeChatLoginResult? LogInDevice(string deviceId, Guid? expectedMemberId, out string? error)
    {
        var id = deviceId.Trim().ToLowerInvariant();
        if (id.Length != 64 || id.Any(c => !Uri.IsHexDigit(c)))
        { error = "设备标识无效。"; return null; }
        return LogInIdentity("device:" + id, null, false, expectedMemberId, out error);
    }

    private WeChatLoginResult? LogInIdentity(string openId, string? shareToken, bool bindWeChat, Guid? expectedMemberId, out string? error)
    {
        var identity = Convert.ToHexString(Hash(openId));
        lock (_gate)
        {
            var entry = _entries.Find(item => item.WeChatIdentityHash == identity);
            if (expectedMemberId is { } expected && entry?.Id != expected)
            { error = "当前登录身份与原账号不匹配，请使用原登录身份重试。"; return null; }
            if (entry?.Archived == true) { error = "账号已停用。"; return null; }
            string? shareHash = null;
            if (!string.IsNullOrEmpty(shareToken))
            {
                if (shareToken.Length != 51 || !shareToken.StartsWith("ws_", StringComparison.Ordinal))
                { error = "邀请无效或已过期。"; return null; }
                shareHash = Convert.ToHexString(Hash(shareToken));
                if (!ReadShares().Any(i => i.Hash == shareHash && i.ExpiresAt > DateTimeOffset.UtcNow) ||
                    _entries.Any(i => i.UsedShareHash == shareHash && i.WeChatIdentityHash != identity))
                { error = "邀请无效、已过期或已使用。"; return null; }
                if (entry is not null && !entry.SharedWithOwner)
                { error = "已有独立账号，不能自动共享历史记录。"; return null; }
            }
            var session = "wr_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var sessionHash = Convert.ToHexString(Hash(session));
            if (entry is null)
            {
                if (_entries.Count >= 50) { error = "当前账号数量已达上限。"; return null; }
                var id = Guid.NewGuid();
                entry = new Entry(id, "成员" + id.ToString("N")[..6], sessionHash,
                    WeChatIdentityHash: identity, SharedWithOwner: shareHash is not null,
                    WeChatSessionHashes: [], UsedShareHash: shareHash);
                Save([.. _entries, entry]);
            }
            else
            {
                var sessions = (entry.WeChatSessionHashes ?? []).TakeLast(9).Append(sessionHash).ToList();
                entry = entry with { WeChatSessionHashes = sessions, UsedShareHash = entry.UsedShareHash ?? shareHash };
                Save(_entries.Select(item => item.Id == entry.Id ? entry : item).ToList());
            }
            if (bindWeChat) Engine(entry).BindVerifiedWeChat(openId);
            error = null;
            return new(session, entry.Id, entry.Name, entry.SharedWithOwner);
        }
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
                if (CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(entry.CodeHash)) ||
                    (entry.WeChatSessionHashes?.Any(s => CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(s))) ?? false))
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

            var code = "wr_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var entry = new Entry(Guid.NewGuid(), name, Convert.ToHexString(Hash(code)));
            var settings = new UserSettings { PushWardApiToken = push };
            new StorageService(MemberDirectory(entry.Id)).SaveSettings(settings);
            Save([.. _entries, entry]);
            error = null;
            return new FamilyInvitation(entry.View, code);
        }
    }

    public MemberInvite? CreateInvitation(CreateMemberInvite request, out string? error)
    {
        var serverUrl = request.ServerUrl?.Trim();
        if (string.IsNullOrWhiteSpace(serverUrl)) serverUrl = Environment.GetEnvironmentVariable("WATERREMINDER_PUBLIC_URL")?.Trim();
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.IsLoopback || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "请输入手机可访问的 HTTPS 服务地址，不能使用 localhost 或桌面 SSH 地址。";
            return null;
        }
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Contains("/api", StringComparison.OrdinalIgnoreCase))
        {
            error = "请填写服务根地址，不要包含 /api。";
            return null;
        }
        lock (_gate)
        {
            Entry? entry;
            if (request.MemberId is { } id)
            {
                entry = _entries.Find(item => item.Id == id && !item.Archived && item.SharedWithOwner);
                if (entry is null) { error = "成员不存在或已停用。"; return null; }
            }
            else
            {
                var created = Add(new(request.Name ?? "", request.PushWardToken), out error);
                if (created is null) return null;
                entry = _entries.Single(item => item.Id == created.Member.Id);
            }
            var token = "wi_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var expires = DateTimeOffset.UtcNow.AddMinutes(15);
            var publicUrl = uri.GetLeftPart(UriPartial.Authority) + path;
            Save(_entries.Select(item => item.Id == entry.Id ? item with
            {
                InviteHash = Convert.ToHexString(Hash(token)), InviteExpiresAt = expires, InviteServerUrl = publicUrl
            } : item).ToList());
            error = null;
            return new(entry.View, token, publicUrl, expires);
        }
    }

    private Entry? Invited(string? token)
    {
        if (token is null || token.Length != 67 || !token.StartsWith("wi_", StringComparison.Ordinal)) return null;
        var hash = Hash(token);
        return _entries.Find(entry => !entry.Archived && entry.InviteExpiresAt > DateTimeOffset.UtcNow &&
            entry.InviteHash is not null && CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(entry.InviteHash)));
    }

    public InvitationPreview? PreviewInvitation(string? token)
    {
        lock (_gate)
        {
            var entry = Invited(token);
            return entry is null ? null : new(entry.View, entry.InviteExpiresAt!.Value, entry.InviteServerUrl!);
        }
    }

    public RedeemedInvitation? RedeemInvitation(string? token)
    {
        lock (_gate)
        {
            var entry = Invited(token);
            if (entry is null) return null;
            var code = "wr_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            Save(_entries.Select(item => item.Id == entry.Id ? item with
            {
                CodeHash = Convert.ToHexString(Hash(code)), InviteHash = null, InviteExpiresAt = null, InviteServerUrl = null
            } : item).ToList());
            return new(entry.View, code, entry.InviteServerUrl!);
        }
    }

    public bool RevokeInvitation(Guid id)
    {
        lock (_gate)
        {
            if (!_entries.Any(entry => entry.Id == id)) return false;
            Save(_entries.Select(item => item.Id == id ? item with
            { InviteHash = null, InviteExpiresAt = null, InviteServerUrl = null } : item).ToList());
            return true;
        }
    }

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

    // Call under _gate so concurrent invitations/updates cannot assign the same token.
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
            WeChat = _options.WeChat,
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
        public string? InviteHash { get; set; }
        public DateTimeOffset? InviteExpiresAt { get; set; }
        public string? InviteServerUrl { get; set; }
        public string? WeChatIdentityHash { get; set; }
        public bool SharedWithOwner { get; set; } = true;
        public List<string>? WeChatSessionHashes { get; set; }
        public string? UsedShareHash { get; set; }
    }

    private static List<Entry> ReadEntries(string json)
        => JsonSerializer.Deserialize<List<EntryDto>>(json)?.Select(item => new Entry(
            item.Id, item.Name, item.CodeHash, item.Archived, item.InviteHash, item.InviteExpiresAt, item.InviteServerUrl, item.WeChatIdentityHash, item.SharedWithOwner, item.WeChatSessionHashes, item.UsedShareHash)).ToList()
            ?? throw new InvalidDataException("Invalid family registry");

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    public void Dispose()
    {
        lock (_gate)
            foreach (var engine in _engines.Values) engine.Dispose();
    }
}
