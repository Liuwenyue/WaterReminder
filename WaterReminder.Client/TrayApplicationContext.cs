using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using WaterReminder;
using WaterReminder.Services;

namespace WaterReminder.Client;

/// <summary>
/// Tray icon and desktop toasts. Scheduling and phone push stay on the server.
/// Toasts are shown only while <see cref="DesktopToastPolicy"/> allows them (work hours).
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ReminderApiClient _api;
    private readonly ClientConfig _config;
    private readonly SshTunnelManager? _tunnel;
    private readonly EventWaitHandle _showEvent;
    private readonly RegisteredWaitHandle _showWait;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Icon _icon;
    private readonly ToolStripMenuItem _pause30;
    private readonly ToolStripMenuItem _pauseToday;
    private readonly ToolStripMenuItem _resume;
    private MainWindow? _window;
    private string? _lastAlertId;
    private bool _clickLogsDrink;
    private DateTime _lastDrinkUtc = DateTime.MinValue;
    private bool _paused;

    public TrayApplicationContext(ClientConfig config, SshTunnelManager? tunnel, EventWaitHandle showEvent)
    {
        _config = config;
        _tunnel = tunnel;
        _showEvent = showEvent;
        _api = new ReminderApiClient(config.ServerUrl, config.ApiToken);
        _icon = LoadIcon();
        _tray = new NotifyIcon
        {
            Icon = _icon,
            Visible = true,
            Text = "喝水提醒",
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开悬浮窗", null, (_, _) => ShowFloating());
        menu.Items.Add("今天状态", null, async (_, _) => await RefreshAsync(announceStatus: true));
        menu.Items.Add("记录一杯", null, async (_, _) => await LogDrinkAsync());
        menu.Items.Add(new ToolStripSeparator());
        _pause30 = new ToolStripMenuItem("暂停 30 分钟", null, async (_, _) => await PauseAsync(TimeSpan.FromMinutes(30)));
        _pauseToday = new ToolStripMenuItem("今日暂停", null, async (_, _) => await PauseAsync(null));
        _resume = new ToolStripMenuItem("恢复提醒", null, async (_, _) => await ResumeAsync()) { Enabled = false };
        menu.Items.Add(_pause30);
        menu.Items.Add(_pauseToday);
        menu.Items.Add(_resume);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("设置", null, (_, _) =>
        {
            var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _ = OpenSettingsAsync(ui);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitThread());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowFloating();
        };
        _tray.BalloonTipClicked += async (_, _) =>
        {
            if (!_clickLogsDrink) return;
            _clickLogsDrink = false;
            await LogDrinkAsync();
        };

        _timer = new System.Windows.Forms.Timer { Interval = 20_000 };
        _timer.Tick += async (_, _) => await RefreshAsync(announceStatus: false);
        _timer.Start();
        var startup = new System.Windows.Forms.Timer { Interval = 200 };
        startup.Tick += (_, _) =>
        {
            startup.Stop();
            startup.Dispose();
            if (_config.FloatingWindow) ShowFloating();
        };
        startup.Start();

        var sync = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _showWait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => sync.Post(__ => ShowFloating(), null),
            null,
            Timeout.Infinite,
            false);

        if (_tunnel?.Connecting == true)
            SetTip("连接服务器中…");
        _ = RefreshAsync(announceStatus: false);
    }

    private async Task OpenSettingsAsync(SynchronizationContext ui)
    {
        ClientSettingsResponse? current = null;
        var now = DateTime.Now;
        var consumed = 0;
        string? error = null;
        try
        {
            var settings = await _api.GetSettingsAsync().ConfigureAwait(false);
            if (!settings.Ok || settings.Value is null)
                error = settings.Error ?? "无法读取设置";
            else
            {
                current = settings.Value;
                var status = await _api.GetStatusAsync().ConfigureAwait(false);
                if (status.Ok && status.Value is not null
                    && DateTime.TryParse(status.Value.Now, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    now = parsed;
                    consumed = status.Value.ConsumedMl;
                }
            }
        }
        catch
        {
            error = "无法读取设置";
        }

        ui.Send(_ =>
        {
            if (error is not null || current is null)
            {
                ShowBalloon(error ?? "无法读取设置", "喝水提醒", logsDrink: false);
                return;
            }

            var window = new SettingsWindow(
                current,
                _config.FloatingWindow,
                ClientStartup.IsEnabled(),
                update => SaveSettingsAsync(update, ui),
                now,
                consumed);
            if (window.ShowDialog() == true)
            {
                _config.FloatingWindow = window.FloatingWindow;
                _config.AutoStart = window.AutoStart;
                ClientStartup.SetEnabled(window.AutoStart);
                _config.Save();
                if (_config.FloatingWindow) ShowFloating();
                else HideFloating();
            }
        }, null);
    }

    private async Task<string?> SaveSettingsAsync(SettingsUpdate update, SynchronizationContext ui)
    {
        var saved = await _api.SaveSettingsAsync(update).ConfigureAwait(false);
        if (!saved.Ok)
            return string.IsNullOrWhiteSpace(saved.Error) ? "保存失败" : saved.Error;
        ui.Post(_ => _ = RefreshAsync(announceStatus: false), null);
        return null;
    }

    private async Task PauseAsync(TimeSpan? forDuration)
    {
        var settings = await _api.GetSettingsAsync();
        if (!settings.Ok || settings.Value is null)
        {
            ShowBalloon(settings.Error ?? "无法读取设置", "喝水提醒", logsDrink: false);
            return;
        }

        var now = DateTime.Now;
        var status = await _api.GetStatusAsync();
        if (status.Ok && status.Value is not null
            && DateTime.TryParse(status.Value.Now, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            now = parsed;

        var update = SettingsEditor.FromResponse(settings.Value);
        update.PauseOnly = true;
        update.RemindersPaused = true;
        update.PauseUntil = forDuration is { } span
            ? now.Add(span).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
            : now.Date.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        var saved = await _api.SaveSettingsAsync(update);
        if (!saved.Ok)
        {
            ShowBalloon(saved.Error ?? "没能暂停提醒", "喝水提醒", logsDrink: false);
            return;
        }
        await RefreshAsync(announceStatus: false);
    }

    private async Task ResumeAsync()
    {
        var settings = await _api.GetSettingsAsync();
        if (!settings.Ok || settings.Value is null)
        {
            ShowBalloon(settings.Error ?? "无法读取设置", "喝水提醒", logsDrink: false);
            return;
        }
        var update = SettingsEditor.FromResponse(settings.Value);
        update.PauseOnly = true;
        update.RemindersPaused = false;
        update.PauseUntil = null;
        var saved = await _api.SaveSettingsAsync(update);
        if (!saved.Ok)
        {
            ShowBalloon(saved.Error ?? "没能恢复提醒", "喝水提醒", logsDrink: false);
            return;
        }
        await RefreshAsync(announceStatus: false);
    }

    private void UpdatePauseMenu(bool paused)
    {
        _paused = paused;
        _pause30.Enabled = !paused;
        _pauseToday.Enabled = !paused;
        _resume.Enabled = paused;
    }

    private void ShowFloating()
    {
        if (_window is null)
        {
            _window = new MainWindow(_api, _config, () =>
            {
                var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
                _ = OpenSettingsAsync(ui);
            });
        }
        if (!_window.IsVisible) _window.Show();
        _window.Activate();
    }

    private void HideFloating()
    {
        if (_window is { IsVisible: true }) _window.Hide();
    }

    private async Task RefreshAsync(bool announceStatus)
    {
        try
        {
            if (_tunnel?.Connecting == true && !await SshTunnelManager.PortOpenAsync(LocalPort()).ConfigureAwait(false))
            {
                SetTip("连接服务器中…");
                return;
            }

            var result = await _api.GetStatusAsync();
            if (!result.Ok || result.Value is null)
            {
                if (result.StatusCode == 429)
                {
                    _timer.Interval = 60_000;
                    SetTip("请求太频繁，稍后再试");
                }
                else if (_tunnel?.Enabled == true)
                    SetTip("连接服务器中…");
                else
                    SetTip(result.StatusCode == 401 ? "服务器拒绝了访问" : "无法连接服务器");
                return;
            }

            _timer.Interval = 20_000;

            var status = result.Value;
            UpdatePauseMenu(status.RemindersPaused);
            var tip = !string.IsNullOrWhiteSpace(status.CompactStatusText)
                ? status.CompactStatusText
                : status.StatusText;
            SetTip(string.IsNullOrWhiteSpace(tip) ? "喝水提醒" : tip);

            var alertId = status.DesktopAlert?.Id;
            if (!DesktopToastPolicy.ShouldToast(status.ShowDesktop, status.Phase, alertId, _lastAlertId))
            {
                if (announceStatus && !string.IsNullOrWhiteSpace(status.StatusText))
                    ShowBalloon(status.StatusText, "喝水提醒", logsDrink: false);
                return;
            }

            var alert = status.DesktopAlert!;
            _lastAlertId = alert.Id;
            ShowBalloon(
                string.IsNullOrWhiteSpace(alert.Body) ? status.StatusText : alert.Body,
                string.IsNullOrWhiteSpace(alert.Title) ? "该喝水了" : alert.Title,
                logsDrink: true);
            await _api.AckDesktopAsync(alert.Id);
        }
        catch
        {
            SetTip(_tunnel?.Enabled == true ? "连接服务器中…" : "无法连接服务器");
        }
    }

    private int LocalPort()
        => _config.Tunnel?.LocalPort is > 0 and < 65536 ? _config.Tunnel.LocalPort : 8787;

    private async Task LogDrinkAsync()
    {
        if (DateTime.UtcNow - _lastDrinkUtc < TimeSpan.FromSeconds(2)) return;
        _lastDrinkUtc = DateTime.UtcNow;
        try
        {
            var result = await _api.LogDesktopDrinkAsync();
            if (!result.Ok || result.Value is null)
            {
                ShowBalloon("没能记下这一杯", "喝水提醒", logsDrink: false);
                return;
            }

            ShowBalloon(
                string.IsNullOrWhiteSpace(result.Value.Message) ? $"已记 {result.Value.RecordedMl} ml" : result.Value.Message,
                string.IsNullOrWhiteSpace(result.Value.Title) ? "已记录" : result.Value.Title,
                logsDrink: false);
            await RefreshAsync(announceStatus: false);
        }
        catch
        {
            ShowBalloon("没能记下这一杯", "喝水提醒", logsDrink: false);
        }
    }

    private void ShowBalloon(string text, string title, bool logsDrink)
    {
        _clickLogsDrink = logsDrink;
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(logsDrink ? 15_000 : 5_000);
    }

    private void SetTip(string text)
    {
        _tray.Text = text.Length <= 63 ? text : text[..63];
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _timer.Dispose();
        _showWait.Unregister(null);
        _window?.ForceClose();
        _tray.Visible = false;
        _tray.Dispose();
        _icon.Dispose();
        _api.Dispose();
        _tunnel?.Dispose();
        base.ExitThreadCore();
    }

    /// <summary>
    /// Original tray icon: embedded Assets/app.ico at the small icon size.
    /// The tray is created before any window, so the WPF application has to exist
    /// before the pack URI can be read. Falls back to the system information icon.
    /// </summary>
    private static Icon LoadIcon()
    {
        try
        {
            ClientTheme.Ensure();
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (info is not null)
            {
                using var stream = info.Stream;
                return new Icon(stream, SystemInformation.SmallIconSize);
            }
        }
        catch { }
        return SystemIcons.Information;
    }
}
