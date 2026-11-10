using System.Media;
using System.Windows.Threading;

namespace WaterReminder.Services;

public sealed partial class ReminderService
{
    private readonly AppServices _services;
    private readonly Action _showMainWindow;
    private readonly Action<string> _notify;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private DateTime _pushNextAttempt = DateTime.MinValue;
    private bool _pushInFlight;

    public ReminderService(AppServices services, Action showMainWindow, Action<string> notify)
    {
        _services = services;
        _showMainWindow = showMainWindow;
        _notify = notify;
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Tick()
    {
        var now = DateTime.Now;
        _services.RefreshToday();
        var settings = _services.Settings;
        if (settings.PauseUntil is not null && settings.PauseUntil <= now)
        {
            settings.RemindersPaused = false;
            settings.PauseUntil = null;
            _services.Storage.SaveSettings(settings);
            _services.Progress.SettingsChanged();
        }
        if (!WaterCalculator.IsActiveDay(settings, now) || settings.RemindersPaused) return;

        var plan = _services.Progress.Plan();
        if (plan.SuggestedMl <= 0 || plan.NextReminder is null || plan.NextReminder > now) return;

        if (WaterCalculator.ShouldShowDesktop(settings, now))
        {
            // work hours only: popup + tray balloon + optional sound
            _services.Progress.MarkReminded(now); // restarts the countdown; next one at least 20 min later
            _showMainWindow();
            _notify($"当前建议补充 {plan.SuggestedMl} ml，点击程序可记录一杯。");
            if (settings.SoundEnabled) SystemSounds.Asterisk.Play();
            return;
        }

        if (WaterCalculator.ShouldSendMobilePush(settings, now))
        {
            // Evening: PushWard only; no window, no sound, no tray balloon.
            // Unconfigured credentials mean "do not push" rather than retrying forever.
            if (!ShouldPublishPushWard(settings, _services.PushWard.IsConfigured)) return;

            // Only mark the reminder once PushWard actually accepted it, otherwise a failed
            // push would still burn the 20-minute gap. "Accepted" means the API returned 2xx —
            // not that the user already tapped. A short private backoff stops 30-second retries.
            if (!MobilePushDeliveryPolicy.ShouldAttempt(now, _pushNextAttempt, _pushInFlight)) return;
            _pushInFlight = true;
            _ = PublishEveningAsync(plan.SuggestedMl, now);
        }
    }

    private async Task PublishEveningAsync(int suggestedMl, DateTime at)
    {
        try
        {
            // No ConfigureAwait(false) here: MarkReminded raises Changed and must resume on the UI thread.
            var published = await _services.PushWard.PublishReminderAsync(suggestedMl, _services.Today.ConsumedMl);
            var outcome = MobilePushDeliveryPolicy.AfterPublish(published, at);
            if (outcome.MarkReminded) _services.Progress.MarkReminded(at);
            _pushNextAttempt = outcome.NextAttempt;
        }
        catch
        {
            _pushNextAttempt = MobilePushDeliveryPolicy.AfterPublish(false, at).NextAttempt;
        }
        finally
        {
            _pushInFlight = false;
        }
    }
}
