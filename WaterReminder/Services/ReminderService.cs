using System.Media;
using System.Windows.Threading;

namespace WaterReminder.Services;

public sealed class ReminderService
{
    private readonly AppServices _services;
    private readonly Action _showMainWindow;
    private readonly Action<string> _notify;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };

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
        if (WaterCalculator.PhaseAt(settings, now) != WaterCalculator.DayPhase.Working) return; // no reminders in lunch / last minutes before work end

        var plan = _services.Progress.Plan();
        if (plan.SuggestedMl <= 0 || plan.NextReminder is null || plan.NextReminder > now) return;
        _services.Progress.MarkReminded(now); // restarts the countdown; next one at least 20 min later
        _showMainWindow();
        _notify($"当前建议补充 {plan.SuggestedMl} ml，点击程序可记录一杯。");
        if (settings.SoundEnabled) SystemSounds.Asterisk.Play();
    }
}
