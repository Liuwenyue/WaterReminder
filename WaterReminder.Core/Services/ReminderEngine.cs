using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WaterReminder.Services;

public sealed class ReminderEngineOptions
{
    public required string DataDirectory { get; init; }
    /// <summary>Defaults to <see cref="ShanghaiClock.Now"/>.</summary>
    public Func<DateTime>? Clock { get; init; }
    /// <summary>Env or pushward.token overlay. When it returns a value it wins over settings.json.</summary>
    public Func<string?>? PushWardToken { get; init; }
    public HttpClient? Http { get; init; }
    /// <summary>When false, ticks publish reminders but do not start the answers loop (tests).</summary>
    public bool PollAnswers { get; init; } = true;
    /// <summary>Shared WeChat client. Null or unconfigured keeps the PushWard phone channel.</summary>
    public WeChatClient? WeChat { get; init; }
}

/// <summary>
/// Headless reminder loop: Shanghai schedule, PushWard in every reminder window,
/// desktop alerts for the Windows client, and one-tap phone recording.
/// </summary>
public sealed partial class ReminderEngine : IDisposable, IPushWardReplies
{
    public static readonly TimeSpan DesktopAlertLifetime = TimeSpan.FromMinutes(15);

    private readonly ReminderEngineOptions _options;
    private readonly Func<DateTime> _clock;
    private readonly Func<string?>? _pushToken;
    private readonly StorageService _storage;
    private readonly UserSettings _settings;
    private readonly ProgressTracker _progress;
    private readonly PushWardService _push;
    private readonly WeChatClient? _wechat;
    private readonly object _gate = new();
    private DateTime _pushNextAttempt = DateTime.MinValue;
    private bool _pushInFlight;
    private bool _retired;
    private readonly CancellationTokenSource _retirement = new();
    private PhoneReplyState? _phoneReplies;
    private DesktopAlert? _pendingDesktop;

    private sealed class DesktopAlert
    {
        public string Id { get; init; } = "";
        public DateTime At { get; init; }
        public int SuggestedMl { get; init; }
        public string Title { get; init; } = "";
        public string Body { get; init; } = "";
    }

    private readonly record struct OutboundPush(PushWardMessage? Push, WeChatReminder? WeChat, PushWardMessage? WeChatFallback, DateTime At);

    public ReminderEngine(ReminderEngineOptions options)
    {
        _options = options;
        _clock = options.Clock ?? ShanghaiClock.Now;
        _pushToken = options.PushWardToken;
        _storage = new StorageService(options.DataDirectory);
        _settings = _storage.LoadSettings();
        ApplyOverlay(_settings);
        _progress = new ProgressTracker(_settings, _storage.LoadDailyRecord, _storage.SaveDailyRecord, () => _clock());
        _push = new PushWardService(_settings, options.Http, replies: this);
        _wechat = options.WeChat;
    }

    public async Task TickAsync(CancellationToken ct = default)
    {
        OutboundPush? outbound = null;
        lock (_gate)
        {
            if (_retired) return;
            var now = RefreshLocked();
            var plan = _progress.Plan();
            var due = plan.SuggestedMl > 0 && plan.NextReminder is { } next && next <= now;
            if (!due) return;

            var desktop = WaterCalculator.ShouldShowDesktop(_settings, now);
            if (desktop) StageDesktopLocked(plan, now);

            var pushWindow = WaterCalculator.ShouldSendMobilePush(_settings, now);
            var user = pushWindow && _settings.PhoneMode != PhoneDeliveryMode.PushWard && _wechat?.IsConfigured == true
                ? WeChatUsers.Load(_options.DataDirectory) : null;
            var channels = ReminderRouting.Choose(_settings.MobilePushEnabled, _push.IsConfigured,
                user is { Credits: > 0 } && !string.IsNullOrWhiteSpace(user.OpenId), _settings.PhoneMode);
            if (pushWindow && channels != PhoneChannel.None)
            {
                if (!MobilePushDeliveryPolicy.ShouldAttempt(now, _pushNextAttempt, _pushInFlight))
                    return;
                _pushInFlight = true;
                var push = channels.HasFlag(PhoneChannel.PushWard) ? BuildPushLocked(plan, now) : null;
                WeChatReminder? wechat = channels.HasFlag(PhoneChannel.WeChat) ? new WeChatReminder(user!.OpenId,
                    plan.SuggestedMl, _progress.Today.ConsumedMl, _settings.DailyGoalMl, now) : null;
                var wechatFallback = wechat is not null && push is null && _settings.PhoneMode != PhoneDeliveryMode.WeChat
                    && _settings.MobilePushEnabled && _push.IsConfigured
                    ? BuildPushLocked(plan, now) : null;
                outbound = new OutboundPush(push, wechat, wechatFallback, now);
            }
            else if (desktop)
            {
                _progress.MarkReminded(now);
            }
        }

        if (outbound is null) return;

        var wechatResult = new WeChatSendResult();
        using var send = CancellationTokenSource.CreateLinkedTokenSource(ct, _retirement.Token);
        if (outbound.Value.WeChat is { } reminder)
        {
            try { wechatResult = await _wechat!.SendAsync(reminder, send.Token).ConfigureAwait(false); }
            catch { wechatResult = default; }
        }
        var published = wechatResult.Sent;
        if (!published && outbound.Value.WeChatFallback is { } fallbackPush)
            published |= await _push.PublishTrackedAsync(fallbackPush, outbound.Value.At, send.Token).ConfigureAwait(false);
        if (outbound.Value.Push is { } message)
            published |= await _push.PublishTrackedAsync(message, outbound.Value.At, send.Token).ConfigureAwait(false);

        lock (_gate)
        {
            if (_retired) return;
            _pushInFlight = false;
            if (outbound.Value.WeChat is { } delivered)
                ApplyWeChatResultLocked(delivered.OpenId, wechatResult.Sent, wechatResult.QuotaExhausted);
            _progress.Reload();
            if (DateOnly.FromDateTime(_clock()) != DateOnly.FromDateTime(outbound.Value.At)) return;
            if (wechatResult.QuotaExhausted && !published)
            {
                _pushNextAttempt = DateTime.MinValue;
                return;
            }
            var outcome = MobilePushDeliveryPolicy.AfterPublish(published, outbound.Value.At);
            if (outcome.MarkReminded) _progress.MarkReminded(outbound.Value.At);
            _pushNextAttempt = outcome.NextAttempt;
        }
    }

    public StatusResponse Status()
    {
        lock (_gate)
        {
            var now = RefreshLocked();
            return BuildStatusLocked(now);
        }
    }

    public ClientSettingsResponse ClientSettings()
    {
        lock (_gate)
        {
            RefreshLocked();
            return CurrentSettingsLocked();
        }
    }

    private ClientSettingsResponse CurrentSettingsLocked()
    {
        var response = new ClientSettingsResponse
        {
            DailyGoalMl = _settings.DailyGoalMl,
            WorkCupMl = WaterCalculator.WorkCupMl(_settings),
            HomeCupMl = WaterCalculator.HomeCupMl(_settings),
            WorkStart = Clock(_settings.WorkStart),
            WorkEnd = Clock(_settings.WorkEnd),
            LunchEnabled = _settings.LunchEnabled,
            LunchStart = Clock(_settings.LunchStart),
            LunchEnd = Clock(_settings.LunchEnd),
            NoDrinkBeforeEndMinutes = _settings.NoDrinkBeforeEndMinutes,
            QuietStart = Clock(_settings.QuietStart),
            MinReminderGapMinutes = _settings.MinReminderGapMinutes,
            MobilePushEnabled = _settings.MobilePushEnabled,
            PushWardConfigured = _push.IsConfigured,
            PhoneMode = _settings.PhoneMode,
            ActiveDays = (bool[])_settings.ActiveDays.Clone(),
        };
        response.Revision = Convert.ToHexString(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response)));
        response.RemindersPaused = _settings.RemindersPaused;
        response.PauseUntil = _settings.PauseUntil?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        return response;
    }

    /// <summary>Desktop tray action. Amount omitted means one work cup.</summary>
    public int LogDrink(int? amountMl, string? source)
    {
        lock (_gate)
        {
            EnsureActiveLocked();
            var now = RefreshLocked();
            var src = string.IsNullOrWhiteSpace(source) ? "desktop" : source.Trim();
            int amount;
            if (amountMl is > 0)
                amount = amountMl.Value;
            else if (string.Equals(src, "desktop", StringComparison.OrdinalIgnoreCase))
                amount = WaterCalculator.WorkCupMl(_settings);
            else
                amount = WaterCalculator.CurrentCupMl(_settings, now);
            return _progress.Drink(amount, src);
        }
    }

    public TodayRecordsResponse TodayRecords()
    {
        lock (_gate)
        {
            RefreshLocked();
            return MapRecords(_progress.Today, editable: true);
        }
    }

    /// <summary>One calendar day from storage. Today is editable; past days are read-only.</summary>
    public TodayRecordsResponse? RecordsFor(string? dateText)
    {
        if (string.IsNullOrWhiteSpace(dateText)
            || !DateOnly.TryParseExact(dateText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return null;
        lock (_gate)
        {
            RefreshLocked();
            var today = DateOnly.FromDateTime(_clock());
            var record = date == today ? _progress.Today : _storage.LoadDailyRecord(date);
            return MapRecords(record, editable: date == today);
        }
    }

    /// <summary>Recent days that have a saved record, newest first. Empty days are omitted.</summary>
    public HistoryResponse History(int days)
    {
        var take = Math.Clamp(days <= 0 ? 30 : days, 1, 366);
        lock (_gate)
        {
            RefreshLocked();
            var today = DateOnly.FromDateTime(_clock());
            var from = today.AddDays(-(take - 1));
            var daysList = _storage.ListDailyRecords()
                .Where(r => DateOnly.TryParseExact(r.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                            && d >= from && d <= today)
                .Select(r =>
                {
                    var consumed = r.Date == _progress.Today.Date ? _progress.Today.ConsumedMl : r.ConsumedMl;
                    var events = r.Date == _progress.Today.Date ? _progress.Today.Events : r.Events;
                    return new HistoryDayResponse
                    {
                        Date = r.Date,
                        ConsumedMl = consumed,
                        GoalMl = (r.Date == _progress.Today.Date ? _progress.Today : r).GoalSnapshotMl ?? 0,
                        GoalKnown = (r.Date == _progress.Today.Date ? _progress.Today : r).GoalSnapshotMl.HasValue,
                        EventCount = events.Count,
                    };
                })
                .Where(x => x.EventCount > 0)
                .OrderByDescending(x => x.Date)
                .ToList();
            return new HistoryResponse { Days = daysList };
        }
    }

    /// <summary>Makes today's total equal <paramref name="totalMl"/> by appending an <c>adjust</c> event. Same total is a no-op.</summary>
    public bool TrySetTodayTotal(int totalMl, out SetTotalResponse? result, out string? error)
    {
        result = null;
        if (!WaterCalculator.TryParseTotal(totalMl.ToString(CultureInfo.InvariantCulture), out var total, out error))
            return false;
        lock (_gate)
        {
            EnsureActiveLocked();
            RefreshLocked();
            var delta = _progress.Correct(total);
            error = null;
            result = new SetTotalResponse
            {
                DeltaMl = delta,
                ConsumedMl = _progress.Today.ConsumedMl,
                GoalMl = _settings.DailyGoalMl,
                Changed = delta != 0,
            };
            return true;
        }
    }

    public bool TryEditRecord(Guid id, int amountMl, out string? error)
    {
        if (amountMl == 0 || amountMl is < -3000 or > 3000)
        {
            error = "请输入 1–3000 之间的整数。";
            return false;
        }
        lock (_gate)
        {
            EnsureActiveLocked();
            RefreshLocked();
            if (_progress.Today.Events.All(x => x.Id != id))
            {
                error = "找不到这条记录。";
                return false;
            }
            _progress.EditEvent(id, amountMl);
            error = null;
            return true;
        }
    }

    public bool TryDeleteRecord(Guid id)
    {
        lock (_gate)
        {
            EnsureActiveLocked();
            RefreshLocked();
            return _progress.DeleteEvent(id);
        }
    }

    public bool TryClearToday()
    {
        lock (_gate)
        {
            EnsureActiveLocked();
            RefreshLocked();
            return _progress.ClearToday();
        }
    }

    public TodayRecordsResponse? ClearToday(DateOnly expectedDate)
    {
        lock (_gate)
        {
            EnsureActiveLocked();
            var now = RefreshLocked();
            if (DateOnly.FromDateTime(now) != expectedDate) return null;
            _progress.ClearLoadedDay();
            return MapRecords(_progress.Today, editable: true);
        }
    }

    private TodayRecordsResponse MapRecords(DailyRecord record, bool editable)
        => new()
        {
            Date = record.Date,
            LastReminderAt = record.LastReminderAt is { } last
                ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Unspecified), TimeSpan.FromHours(8)) : null,
            ConsumedMl = record.ConsumedMl,
            GoalMl = record.GoalSnapshotMl ?? 0,
            GoalKnown = record.GoalSnapshotMl.HasValue,
            RemindersPaused = editable && _settings.RemindersPaused,
            Editable = editable,
            Events = record.Events
                .OrderBy(x => x.At)
                .Select(x => new RecordEventResponse
                {
                    Id = x.Id,
                    At = x.At.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                    AmountMl = x.AmountMl,
                    Source = x.Source,
                })
                .ToList(),
        };

    /// <summary>One phone tap. Work cup during work hours, home cup after work.</summary>
    public int RecordPhoneDrink()
    {
        lock (_gate)
        {
            EnsureActiveLocked();
            var now = RefreshLocked();
            var amount = WaterCalculator.CurrentCupMl(_settings, now);
            return _progress.Drink(amount, PushWardMessageBuilder.RecordSource);
        }
    }

    string? IPushWardReplies.LoadCursor(string token)
    {
        lock (_gate) return ReplyStateLocked(token).Cursor;
    }

    /// <summary>Send one test to this member without advancing the scheduled reminder.</summary>
    public async Task<bool> SendTestPushAsync(CancellationToken ct = default)
    {
        PushWardMessage message;
        DateTime now;
        lock (_gate)
        {
            EnsureActiveLocked();
            now = RefreshLocked();
            if (_pushInFlight || !ReminderRouting.ShouldPublishPushWard(_settings, _push.IsConfigured)) return false;
            message = PushWardMessageBuilder.BuildTest(_settings, _progress.Today.ConsumedMl, now);
            _pushInFlight = true;
        }
        try
        {
            using var send = CancellationTokenSource.CreateLinkedTokenSource(ct, _retirement.Token);
            return await _push.PublishTrackedAsync(message, now, send.Token).ConfigureAwait(false);
        }
        finally { lock (_gate) _pushInFlight = false; }
    }

    void IPushWardReplies.Track(string token, string notificationId, string? cursor, DateTime date, string? drinkActionId)
    {
        lock (_gate)
        {
            if (_retired || !string.Equals(token, _settings.PushWardApiToken, StringComparison.Ordinal)) return;
            var state = ReplyStateLocked(token);
            var day = DateOnly.FromDateTime(date);
            var cutoff = day.AddDays(-30);
            var sent = state.Sent.Where(entry => entry.Value >= cutoff).ToDictionary();
            sent[notificationId] = day;
            var actions = state.DrinkActions.Where(entry => sent.ContainsKey(entry.Key)).ToDictionary();
            if (drinkActionId is not null) actions[notificationId] = drinkActionId;
            SaveRepliesLocked(new PhoneReplyState { TokenHash = state.TokenHash, Cursor = state.Cursor ?? cursor, Sent = sent, DrinkActions = actions });
        }
    }

    bool IPushWardReplies.Accept(string token, string? previousCursor, PushWardAnswerBatch batch)
    {
        lock (_gate)
        {
            // Wait until the outstanding POST's notification ID has been saved before advancing.
            if (_retired || _pushInFlight || !_settings.MobilePushEnabled ||
                !string.Equals(token, _settings.PushWardApiToken, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(batch.Cursor)) return false;
            var state = ReplyStateLocked(token);
            if (state.Cursor != previousCursor) return false;
            if (state.Cursor == batch.Cursor && batch.Items.Count == 0) return true;

            var now = RefreshLocked();
            var sent = new Dictionary<string, DateOnly>(state.Sent);
            var actions = new Dictionary<string, string>(state.DrinkActions);
            foreach (var answer in batch.Items)
            {
                if (!sent.Remove(answer.NotificationId, out var day)) continue;
                actions.Remove(answer.NotificationId, out var expectedAction);
                var message = ApplyPhoneReplyLocked(now, day, state.TokenHash, answer, expectedAction);
                if (message is not null)
                {
                    var confirmation = PushWardMessageBuilder.BuildConfirmation(token, 0);
                    _ = _push.PublishAsync(confirmation with { Payload = confirmation.Payload with
                    {
                        Body = message,
                        Title = answer.ActionId.StartsWith("test-ack-", StringComparison.Ordinal) ? "连接测试" : answer.ActionId.StartsWith("drink", StringComparison.Ordinal) ? "喝水记录" : "饮水总量"
                    } }, _retirement.Token);
                }
            }
            SaveRepliesLocked(new PhoneReplyState { TokenHash = state.TokenHash, Cursor = batch.Cursor, Sent = sent, DrinkActions = actions });
            return true;
        }
    }

    private string? ApplyPhoneReplyLocked(DateTime now, DateOnly day, string tokenHash, PushWardAnswerItem answer, string? expectedAction)
    {
        if (answer.ActionId.StartsWith("test-ack-", StringComparison.Ordinal))
            return ApplySafeTestReply(now, answer, expectedAction);
        var exact = expectedAction is not null && expectedAction.StartsWith("drink-", StringComparison.Ordinal)
            && string.Equals(answer.ActionId, expectedAction, StringComparison.Ordinal);
        var drink = exact || (expectedAction is null or PushWardMessageBuilder.DrinkActionId
            && string.Equals(answer.ActionId, PushWardMessageBuilder.DrinkActionId, StringComparison.Ordinal));
        var correction = string.Equals(answer.ActionId, PushWardMessageBuilder.BuildTotalAction(day.ToDateTime(TimeOnly.MinValue)).Id,
            StringComparison.Ordinal);
        if (!drink && !correction) return null;
        if (day != DateOnly.FromDateTime(now) || _progress.Today.Date != day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            return "这条提醒已过期，请使用今天的提醒。";

        int? total = null;
        if (correction)
        {
            if (!WaterCalculator.TryParseTotal(answer.Text, out var corrected, out var error))
                return $"未修改。{error}";
            total = corrected;
        }
        var cup = WaterCalculator.CurrentCupMl(_settings, now);
        if (exact && (!int.TryParse(expectedAction!.Split('-')[^1], NumberStyles.None, CultureInfo.InvariantCulture, out cup) || cup < 0))
            return null;
        var receipt = $"{tokenHash}:{answer.NotificationId}";
        var delta = _progress.ApplyPhoneReply(now, receipt, total, cup);
        if (delta is null) return null;
        if (total is { } amount) return $"今日已喝总量为 {amount} ml";
        return delta > 0 ? $"已记 {delta} ml" : "今日已达到饮水目标。";
    }

    private PhoneReplyState ReplyStateLocked(string token)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        _phoneReplies ??= _storage.LoadPhoneReplies();
        return _phoneReplies?.TokenHash == hash ? _phoneReplies : new PhoneReplyState { TokenHash = hash };
    }

    private void SaveRepliesLocked(PhoneReplyState state)
    {
        _storage.SavePhoneReplies(state);
        _phoneReplies = state;
    }

    /// <summary>Persists reminder fields and applies them immediately. A blank phone token keeps the existing credential.</summary>
    public bool TryUpdateSettings(SettingsUpdate update, out string? error, string? pushWardToken = null)
    {
        lock (_gate)
        {
            EnsureActiveLocked();
            RefreshLocked();
            var fresh = _storage.LoadSettings();
            var fileToken = string.IsNullOrWhiteSpace(pushWardToken) ? fresh.PushWardApiToken : pushWardToken.Trim();
            if (update.PauseOnly)
            {
                var pause = SettingsEditor.FromResponse(CurrentSettingsLocked());
                pause.RemindersPaused = update.RemindersPaused;
                pause.PauseUntil = update.PauseUntil;
                update = pause;
            }
            else if (update.ExpectedRevision is { } revision && revision != CurrentSettingsLocked().Revision)
            {
                error = SettingsEditor.StaleSettings;
                return false;
            }
            if (!SettingsEditor.TryApply(fresh, update, out error)) return false;
            fresh.PushWardApiToken = string.IsNullOrWhiteSpace(_pushToken?.Invoke()) ? fileToken : "";
            _storage.SaveSettings(fresh);
            ApplyOverlay(fresh);
            var tokenChanged = fresh.PushWardApiToken != _settings.PushWardApiToken;
            Copy(fresh, _settings);
            if (!update.PreservePause && _progress.Today.RemindersPausedOverride.HasValue)
            {
                _progress.Today.RemindersPausedOverride = null;
                _storage.SaveDailyRecord(_progress.Today);
            }
            _progress.SaveGoalSnapshot(_settings.DailyGoalMl);
            _progress.SettingsChanged();
            if (tokenChanged) _push.Stop();
            EnsureAnswerLoopLocked();
            return true;
        }
    }

    public bool AcknowledgeDesktop(string? id)
    {
        lock (_gate)
        {
            EnsureActiveLocked();
            if (_pendingDesktop is null || string.IsNullOrWhiteSpace(id)) return false;
            if (!string.Equals(_pendingDesktop.Id, id.Trim(), StringComparison.Ordinal)) return false;
            _pendingDesktop = null;
            return true;
        }
    }

    public string Describe()
    {
        var status = Status();
        return string.Join('\n',
        [
            $"timezone: {status.TimeZone}",
            $"now: {status.Now}",
            $"phase: {status.Phase}",
            $"showDesktop: {status.ShowDesktop.ToString().ToLowerInvariant()}",
            $"pushWindow: {status.PushWindow.ToString().ToLowerInvariant()}",
            $"consumedMl: {status.ConsumedMl}",
            $"goalMl: {status.GoalMl}",
            $"suggestedMl: {status.SuggestedMl}",
            $"nextReminder: {status.NextReminder ?? ""}",
            $"workCupMl: {status.WorkCupMl}",
            $"homeCupMl: {status.HomeCupMl}",
            $"workStart: {status.WorkStart}",
            $"workEnd: {status.WorkEnd}",
            $"quietStart: {status.QuietStart}",
            $"desktopAlert: {status.DesktopAlert?.Id ?? ""}",
        ]);
    }

    public string PhonePushToken
    {
        get
        {
            lock (_gate)
            {
                ReloadSettingsLocked();
                return _settings.PushWardApiToken;
            }
        }
    }

    public WeChatStatusResponse WeChatStatus()
    {
        lock (_gate)
        {
            var user = WeChatUsers.Load(_options.DataDirectory);
            var configured = _wechat?.IsConfigured == true;
            return new WeChatStatusResponse
            {
                Enabled = configured,
                TemplateId = configured ? _wechat!.TemplateId : "",
                Bound = !string.IsNullOrWhiteSpace(user.OpenId),
                Credits = Math.Max(0, user.Credits),
            };
        }
    }

    public async Task<(bool Ok, WeChatStatusResponse Status, string? Error)> BindWeChatAsync(string? code, CancellationToken cancellationToken = default)
    {
        if (_wechat is not { IsConfigured: true })
            return (false, WeChatStatus(), "服务器还没有配置微信提醒。");
        var trimmed = code?.Trim() ?? "";
        if (trimmed.Length is < 8 or > 128 || trimmed.Any(char.IsWhiteSpace) || trimmed.Any(char.IsControl))
            return (false, WeChatStatus(), "微信登录码不正确。");
        string? openId;
        try
        {
            openId = await _wechat.ExchangeOpenIdAsync(trimmed, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            return (false, WeChatStatus(), "暂时连不上微信。");
        }
        if (string.IsNullOrEmpty(openId))
            return (false, WeChatStatus(), "微信没有返回用户标识。");
        BindVerifiedWeChat(openId);
        return (true, WeChatStatus(), null);
    }

    public void BindVerifiedWeChat(string openId)
    {
        if (string.IsNullOrWhiteSpace(openId)) throw new ArgumentException("Missing verified WeChat identity.", nameof(openId));
        lock (_gate)
        {
            EnsureActiveLocked();
            var user = WeChatUsers.Load(_options.DataDirectory);
            if (user.OpenId != openId)
            {
                user.Credits = 0;
                user.Seen.Clear();
            }
            user.OpenId = openId;
            WeChatUsers.Save(_options.DataDirectory, user);
        }
    }

    public (bool Ok, WeChatStatusResponse Status, string? Error) AcceptWeChatSubscription(string? id, string? status)
    {
        if (_wechat is not { IsConfigured: true })
            return (false, WeChatStatus(), "服务器还没有配置微信提醒。");
        var operation = id?.Trim() ?? "";
        if (operation.Length is < 8 or > 64 || operation.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            return (false, WeChatStatus(), "订阅编号不正确。");
        var decision = status?.Trim() ?? "";
        if (decision is not ("accept" or "reject" or "ban" or "filter"))
            return (false, WeChatStatus(), "订阅结果不正确。");
        lock (_gate)
        {
            EnsureActiveLocked();
            var user = WeChatUsers.Load(_options.DataDirectory);
            if (!user.Seen.Contains(operation, StringComparer.Ordinal))
            {
                user.Seen.Add(operation);
                if (decision == "accept" && user.Credits < 100) user.Credits++;
                WeChatUsers.Save(_options.DataDirectory, user);
            }
        }
        return (true, WeChatStatus(), null);
    }

    private void ApplyWeChatResultLocked(string openId, bool published, bool quotaExhausted)
    {
        var user = WeChatUsers.Load(_options.DataDirectory);
        if (user.OpenId != openId) return;
        if (published) user.Credits = Math.Max(0, user.Credits - 1);
        else if (quotaExhausted) user.Credits = 0;
        WeChatUsers.Save(_options.DataDirectory, user);
    }

    public void Retire()
    {
        lock (_gate)
        {
            if (_retired) return;
            _retired = true;
            _pendingDesktop = null;
            _retirement.Cancel();
            _push.Dispose();
            _retirement.Dispose();
        }
    }

    public void Dispose() => Retire();

    private void EnsureActiveLocked()
    {
        if (_retired) throw new ObjectDisposedException(nameof(ReminderEngine));
    }

    private DateTime RefreshLocked()
    {
        ReloadSettingsLocked();
        var now = _clock();
        if (!_retired) ExpirePauseLocked(now);
        _progress.Reload(now);
        if (!_retired) _progress.SaveGoalSnapshot(_settings.DailyGoalMl);
        if (_progress.Today.RemindersPausedOverride is { } paused)
        {
            _settings.RemindersPaused = paused;
            _settings.PauseUntil = paused ? now.Date.AddDays(1) : null;
        }
        EnsureAnswerLoopLocked();
        var show = WaterCalculator.ShouldShowDesktop(_settings, now);
        if (!show || (_pendingDesktop is { } alert && now - alert.At > DesktopAlertLifetime))
            _pendingDesktop = null;
        return now;
    }

    private void ReloadSettingsLocked()
    {
        var fresh = _storage.LoadSettings();
        ApplyOverlay(fresh);
        Copy(fresh, _settings);
    }

    private void ApplyOverlay(UserSettings target)
    {
        var overlay = _pushToken?.Invoke();
        if (!string.IsNullOrWhiteSpace(overlay))
            target.PushWardApiToken = overlay.Trim();
    }

    private void ExpirePauseLocked(DateTime now)
    {
        if (_settings.PauseUntil is null || _settings.PauseUntil > now) return;
        _settings.RemindersPaused = false;
        _settings.PauseUntil = null;
        var fresh = _storage.LoadSettings();
        fresh.RemindersPaused = false;
        fresh.PauseUntil = null;
        if (!string.IsNullOrWhiteSpace(_pushToken?.Invoke()))
            fresh.PushWardApiToken = "";
        _storage.SaveSettings(fresh);
        _progress.SettingsChanged();
    }

    private void EnsureAnswerLoopLocked()
    {
        if (_retired || !_options.PollAnswers) return;
        var should = ReminderRouting.ShouldPublishPushWard(_settings, _push.IsConfigured);
        if (should && !_push.IsRunning) _push.Start();
        else if (!should && _push.IsRunning) _push.Stop();
    }

    private PushWardMessage BuildPushLocked(ReminderPlan plan, DateTime now)
        => PushWardMessageBuilder.BuildReminder(_settings.PushWardApiToken,
            plan.SuggestedMl, _progress.Today.ConsumedMl, _settings.DailyGoalMl, now,
            WaterCalculator.CurrentCupMl(_settings, now));

    private void StageDesktopLocked(ReminderPlan plan, DateTime now)
    {
        var id = plan.NextReminder!.Value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        if (_pendingDesktop?.Id == id) return;
        var consumed = _progress.Today.ConsumedMl;
        _pendingDesktop = new DesktopAlert
        {
            Id = id,
            At = now,
            SuggestedMl = plan.SuggestedMl,
            Title = PushWardMessageBuilder.ReminderTitle,
            Body = PushWardMessageBuilder.FormatReminderBody(consumed, _settings.DailyGoalMl, plan.SuggestedMl),
        };
    }

    private StatusResponse BuildStatusLocked(DateTime now)
    {
        var plan = _progress.Plan();
        var phase = WaterCalculator.PhaseAt(_settings, now);
        var show = WaterCalculator.ShouldShowDesktop(_settings, now);
        return new StatusResponse
        {
            Date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeZone = ShanghaiClock.Id,
            Now = now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            ConsumedMl = _progress.Today.ConsumedMl,
            GoalMl = _settings.DailyGoalMl,
            SuggestedMl = plan.SuggestedMl,
            NextReminder = plan.NextReminder?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            Phase = phase.ToString(),
            ShowDesktop = show,
            PushWindow = WaterCalculator.ShouldSendMobilePush(_settings, now),
            PauseUntil = _settings.PauseUntil is { } until
                ? new DateTimeOffset(DateTime.SpecifyKind(until, DateTimeKind.Unspecified), TimeSpan.FromHours(8)) : null,
            RemindersPaused = _settings.RemindersPaused,
            StatusText = WaterCalculator.StatusLine(_settings, _progress.Today, plan, now, compact: false),
            CompactStatusText = WaterCalculator.StatusLine(_settings, _progress.Today, plan, now, compact: true),
            WorkCupMl = WaterCalculator.WorkCupMl(_settings),
            HomeCupMl = WaterCalculator.HomeCupMl(_settings),
            WorkStart = Clock(_settings.WorkStart),
            WorkEnd = Clock(_settings.WorkEnd),
            LunchStart = Clock(_settings.LunchStart),
            LunchEnd = Clock(_settings.LunchEnd),
            QuietStart = Clock(_settings.QuietStart),
            MinReminderGapMinutes = _settings.MinReminderGapMinutes,
            DesktopAlert = show && _pendingDesktop is { } alert
                ? new DesktopAlertResponse
                {
                    Id = alert.Id,
                    Title = alert.Title,
                    Body = alert.Body,
                    SuggestedMl = alert.SuggestedMl,
                }
                : null,
        };
    }

    private static string Clock(TimeSpan value) => value.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    private static void Copy(UserSettings from, UserSettings to)
    {
        to.DailyGoalMl = from.DailyGoalMl;
        to.CupCapacityMl = from.CupCapacityMl;
        to.WorkCupCapacityMl = from.WorkCupCapacityMl;
        to.HomeCupCapacityMl = from.HomeCupCapacityMl;
        to.WorkStart = from.WorkStart;
        to.WorkEnd = from.WorkEnd;
        to.LunchEnabled = from.LunchEnabled;
        to.LunchStart = from.LunchStart;
        to.LunchEnd = from.LunchEnd;
        to.NoDrinkBeforeEndMinutes = from.NoDrinkBeforeEndMinutes;
        to.MinReminderGapMinutes = from.MinReminderGapMinutes;
        to.QuietStart = from.QuietStart;
        to.ActiveDays = from.ActiveDays is { Length: 7 } days ? (bool[])days.Clone() : [true, true, true, true, true, false, false];
        to.MobilePushEnabled = from.MobilePushEnabled;
        to.PhoneMode = from.PhoneMode;
        to.PushWardApiToken = from.PushWardApiToken ?? "";
        to.SoundEnabled = from.SoundEnabled;
        to.AutoStart = from.AutoStart;
        to.FloatingWindow = from.FloatingWindow;
        to.FirstRun = from.FirstRun;
        to.RemindersPaused = from.RemindersPaused;
        to.PauseUntil = from.PauseUntil;
        to.CompactMode = from.CompactMode;
        to.WindowLeft = from.WindowLeft;
        to.WindowTop = from.WindowTop;
        to.CompactLeft = from.CompactLeft;
        to.CompactTop = from.CompactTop;
        to.ExpandedLeft = from.ExpandedLeft;
        to.ExpandedTop = from.ExpandedTop;
    }
}
