using System.Drawing;
using Forms = System.Windows.Forms;

namespace WaterReminder.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly AppServices _services;
    private readonly Action _show;
    private readonly Action _openSettings;
    private readonly Action _exit;
    private readonly Forms.Timer _statusTimer = new() { Interval = 10000 };

    public TrayIconService(AppServices services, Action show, Action openSettings, Action exit)
    {
        _services = services;
        _show = show;
        _openSettings = openSettings;
        _exit = exit;
        _icon = new Forms.NotifyIcon { Icon = LoadIcon(), Visible = true, Text = "喝水提醒" };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _show(); };
        _icon.BalloonTipClicked += (_, _) => _show();
        _icon.ContextMenuStrip = BuildMenu();
        _statusTimer.Tick += (_, _) => UpdateText();
        _statusTimer.Start();
        UpdateText();
    }

    public static Icon LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (info is not null)
            {
                using var stream = info.Stream;
                var size = Forms.SystemInformation.SmallIconSize;
                return new Icon(stream, size);
            }
        }
        catch { }
        return SystemIcons.Information;
    }

    public void UpdateText()
    {
        try
        {
            var settings = _services.Settings;
            var now = DateTime.Now;
            var text = $"喝水 {_services.Today.ConsumedMl:N0}/{settings.DailyGoalMl:N0} ml";
            text += " · " + WaterCalculator.StatusLine(settings, _services.Today, _services.Progress.Plan(), now, compact: true);
            _icon.Text = text.Length > 63 ? text[..63] : text;
        }
        catch { }
    }

    public void ShowReminder(string text)
    {
        _icon.BalloonTipTitle = "该喝水啦";
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = Forms.ToolTipIcon.None;
        _icon.ShowBalloonTip(5000);
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new FluentMenuRenderer(),
            Font = new Font("Microsoft YaHei UI", 9.5f),
            Padding = new Forms.Padding(4),
            ShowImageMargin = false,
        };
        Forms.ToolStripMenuItem Item(string text, EventHandler onClick)
        {
            var item = new Forms.ToolStripMenuItem(text, null, onClick) { Padding = new Forms.Padding(8, 6, 24, 6) };
            menu.Items.Add(item);
            return item;
        }
        var header = new Forms.ToolStripLabel("喝水提醒") { ForeColor = Color.FromArgb(0x89, 0x94, 0xA3), Padding = new Forms.Padding(8, 4, 8, 2) };
        menu.Items.Add(header);
        Item("打开悬浮窗", (_, _) => _show());
        menu.Items.Add(new Forms.ToolStripSeparator());
        var pause30 = Item("暂停 30 分钟", (_, _) => SetPause(DateTime.Now.AddMinutes(30)));
        var pauseToday = Item("今日暂停提醒", (_, _) => SetPause(DateTime.Today.AddDays(1)));
        var resume = Item("恢复提醒", (_, _) =>
        {
            _services.Settings.RemindersPaused = false;
            _services.Settings.PauseUntil = null;
            _services.Storage.SaveSettings(_services.Settings);
            UpdateText();
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        Item("设置…", (_, _) => _openSettings());
        Item("退出", (_, _) => _exit());
        menu.Opening += (_, _) =>
        {
            var paused = _services.Settings.RemindersPaused;
            pause30.Visible = !paused;
            pauseToday.Visible = !paused;
            resume.Visible = paused;
            header.Text = $"今日 {_services.Today.ConsumedMl:N0} / {_services.Settings.DailyGoalMl:N0} ml" + (paused ? " · 已暂停" : "");
        };
        return menu;
    }

    private void SetPause(DateTime until)
    {
        _services.Settings.RemindersPaused = true;
        _services.Settings.PauseUntil = until;
        _services.Storage.SaveSettings(_services.Settings);
        UpdateText();
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        _statusTimer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
    }

    /// <summary>Light, rounded-highlight context menu instead of the default gray WinForms look.</summary>
    private sealed class FluentMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        private static readonly Color Bg = Color.FromArgb(0xFC, 0xFD, 0xFE);
        private static readonly Color Hover = Color.FromArgb(0xE9, 0xF2, 0xFD);
        private static readonly Color Border = Color.FromArgb(0xE2, 0xE8, 0xF0);
        private static readonly Color Text = Color.FromArgb(0x17, 0x21, 0x2F);

        public FluentMenuRenderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(Bg);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            using var p = new Pen(Border);
            var r = e.AffectedBounds; r.Width -= 1; r.Height -= 1;
            e.Graphics.DrawRectangle(p, r);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = Rounded(r, 4);
            using var b = new SolidBrush(Hover);
            e.Graphics.FillPath(b, path);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item is not Forms.ToolStripLabel) e.TextColor = e.Item.Enabled ? Text : Color.FromArgb(0xA0, 0xA8, 0xB4);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var p = new Pen(Border);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
        }

        private static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int radius)
        {
            var d = radius * 2;
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private sealed class Colors : Forms.ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Bg;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Color.Transparent;
            public override Color MenuItemSelected => Hover;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
        }
    }
}
