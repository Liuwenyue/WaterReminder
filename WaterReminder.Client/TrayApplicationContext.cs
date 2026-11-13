using System.Drawing;
using System.Globalization;
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
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Icon _icon;
    private MainWindow? _window;
    private string? _lastAlertId;
    private bool _clickLogsDrink;
    private DateTime _lastDrinkUtc = DateTime.MinValue;

    public TrayApplicationContext(ClientConfig config)
    {
        _config = config;
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

            var window = new SettingsWindow(current, _config.FloatingWindow, update => SaveSettingsAsync(update, ui), now, consumed);
            if (window.ShowDialog() == true)
            {
                _config.FloatingWindow = window.FloatingWindow;
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
            var result = await _api.GetStatusAsync();
            if (!result.Ok || result.Value is null)
            {
                SetTip(result.StatusCode == 401 ? "服务器拒绝了访问" : "无法连接服务器");
                return;
            }

            var status = result.Value;
            SetTip(string.IsNullOrWhiteSpace(status.StatusText) ? "喝水提醒" : status.StatusText);

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
            SetTip("无法连接服务器");
        }
    }

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
        _window?.ForceClose();
        _tray.Visible = false;
        _tray.Dispose();
        _icon.Dispose();
        _api.Dispose();
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
