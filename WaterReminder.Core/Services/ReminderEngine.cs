using System.Globalization;

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
}

/// <summary>
/// Headless reminder loop: Shanghai schedule, PushWard in every reminder window,
/// desktop alerts for the Windows client, and one-tap phone recording.
/// </summary>
public sealed class ReminderEngine : IDisposable
{
    public static readonly TimeSpan DesktopAlertLifetime = TimeSpan.FromMinutes(15);

    private readonly ReminderEngineOptions _options;
    private readonly Func<DateTime> _clock;
    private readonly Func<string?>? _pushToken;
    private readonly StorageService _storage;
    private readonly UserSettings _settings;
    private readonly ProgressTracker _progress;
    private readonly PushWardService _push;
    private readonly object _gate = new();
    private DateTime _pushNextAttempt = DateTime.MinValue;
    private bool _pushInFlight;
    private DesktopAlert? _pendingDesktop;

    private sealed class DesktopAlert
    {
        public string Id { get; init; } = "";
        public DateTime At { get; init; }
        public int SuggestedMl { get; init; }
        public string Title { get; init; } = "";
        public string Body { get; init; } = "";
    }

    private readonly record struct OutboundPush(int Suggested, int Consumed, DateTime At);

    public ReminderEngine(ReminderEngineOptions options)
    {
        _options = options;
        _clock = options.Clock ?? ShanghaiClock.Now;
        _pushToken = options.PushWardToken;
        _storage = new StorageService(options.DataDirectory);
        _settings = _storage.LoadSettings();
        ApplyOverlay(_settings);
        _progress = new ProgressTracker(_settings, _storage.LoadDailyRecord, _storage.SaveDailyRecord, () => _clock());
        _push = new PushWardService(_settings, options.Http, RecordPhoneDrink, static action => action());
    }

    public async Task TickAsync(CancellationToken ct = default)
    {
        OutboundPush? outbound = null;
        lock (_gate)
        {
            var now = RefreshLocked();
            var plan = _progress.Plan();
            var due = plan.SuggestedMl > 0 && plan.NextReminder is { } next && next <= now;
            if (!due) return;

            var desktop = WaterCalculator.ShouldShowDesktop(_settings, now);
            if (desktop) StageDesktopLocked(plan, now);

            var pushWindow = WaterCalculator.ShouldSendMobilePush(_settings, now);
            var canPush = ReminderRouting.ShouldPublishPushWard(_settings, _push.IsConfigured);
            if (pushWindow && canPush)
            {
                if (!MobilePushDeliveryPolicy.ShouldAttempt(now, _pushNextAttempt, _pushInFlight))
                    return;
                _pushInFlight = true;
                outbound = new OutboundPush(plan.SuggestedMl, _progress.Today.ConsumedMl, now);
            }
            else if (desktop)
            {
                // Push is off or not configured: the desktop slot still has to advance.
                _progress.MarkReminded(now);
            }
        }

        if (outbound is null) return;

        var published = false;
        try
        {
            published = await _push.PublishReminderAsync(outbound.Value.Suggested, outbound.Value.Consumed, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            published = false;
        }

        lock (_gate)
        {
            var outcome = MobilePushDeliveryPolicy.AfterPublish(published, outbound.Value.At);
            if (outcome.MarkReminded) _progress.MarkReminded(outbound.Value.At);
            _pushNextAttempt = outcome.NextAttempt;
            _pushInFlight = false;
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
            return new ClientSettingsResponse
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
                ActiveDays = (bool[])_settings.ActiveDays.Clone(),
            };
        }
    }

    /// <summary>Desktop tray action. Amount omitted means one work cup.</summary>
    public int LogDrink(int? amountMl, string? source)
    {
        lock (_gate)
        {
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

    /// <summary>One phone tap. Work cup during work hours, home cup after work.</summary>
    public int RecordPhoneDrink()
    {
        lock (_gate)
        {
            var now = RefreshLocked();
            var amount = WaterCalculator.CurrentCupMl(_settings, now);
            return _progress.Drink(amount, PushWardMessageBuilder.RecordSource);
        }
    }

    /// <summary>Persists reminder fields and applies them to the running schedule. The phone token is kept as it was.</summary>
    public bool TryUpdateSettings(SettingsUpdate update, out string? error)
    {
        lock (_gate)
        {
            var fresh = _storage.LoadSettings();
            var fileToken = fresh.PushWardApiToken;
            if (!SettingsEditor.TryApply(fresh, update, out error)) return false;
            fresh.PushWardApiToken = string.IsNullOrWhiteSpace(_pushToken?.Invoke()) ? fileToken : "";
            _storage.SaveSettings(fresh);
            ApplyOverlay(fresh);
            Copy(fresh, _settings);
            _progress.SettingsChanged();
            return true;
        }
    }

    public bool AcknowledgeDesktop(string? id)
    {
        lock (_gate)
        {
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

    public void Dispose() => _push.Dispose();

    private DateTime RefreshLocked()
    {
        ReloadSettingsLocked();
        var now = _clock();
        ExpirePauseLocked(now);
        _progress.Reload();
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
        if (!_options.PollAnswers) return;
        var should = _settings.MobilePushEnabled && _push.IsConfigured;
        if (should && !_push.IsRunning) _push.Start();
        else if (!should && _push.IsRunning) _push.Stop();
    }

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
            RemindersPaused = _settings.RemindersPaused,
            StatusText = WaterCalculator.StatusLine(_settings, _progress.Today, plan, now, compact: false),
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
