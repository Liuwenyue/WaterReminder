using System.IO;
using System.Windows;
using WaterReminder.Services;

namespace WaterReminder;

/// <summary>
/// "--ui-demo" mode: opens every window side by side with sample data stored in %TEMP%,
/// so the UI can be reviewed/screenshotted without touching real user data.
/// </summary>
internal static class UiDemo
{
    private static void Snap(Window w, string file)
    {
        const double scale = 1.25;
        w.UpdateLayout();
        var width = (int)Math.Ceiling(w.ActualWidth * scale);
        var height = (int)Math.Ceiling(w.ActualHeight * scale);
        var dark = Services.ThemeManager.IsDark;
        var bg = new System.Windows.Media.SolidColorBrush(dark ? System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20) : System.Windows.Media.Color.FromRgb(0xE9, 0xEE, 0xF4));
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawRectangle(bg, null, new Rect(0, 0, w.ActualWidth, w.ActualHeight));
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Render(w);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    public static void Run(string[] args)
    {
        var dir = Path.Combine(Path.GetTempPath(), "WaterReminderDemo");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var storage = new StorageService(dir);
        var done = args.Contains("--done");
        var empty = args.Contains("--empty");
        var settings = new UserSettings { DailyGoalMl = 2000, CupCapacityMl = 300, FirstRun = false, AutoStart = true };
        storage.SaveSettings(settings);
        var today = new DailyRecord();
        if (!empty)
        {
            var d = DateTime.Today;
            today.Events.Add(new WaterEvent { At = d.AddHours(9).AddMinutes(12), AmountMl = 300, Source = "cup" });
            today.Events.Add(new WaterEvent { At = d.AddHours(10).AddMinutes(35), AmountMl = 300, Source = "cup" });
            today.Events.Add(new WaterEvent { At = d.AddHours(11).AddMinutes(50), AmountMl = 250, Source = "custom" });
            today.Events.Add(new WaterEvent { At = d.AddHours(13).AddMinutes(20), AmountMl = 300, Source = "keyboard" });
            if (done) today.Events.Add(new WaterEvent { At = d.AddHours(15), AmountMl = 850, Source = "custom" });
        }
        storage.SaveDailyRecord(today);
        var services = new AppServices(storage);

        var snapping = args.Contains("--snap");
        var x = snapping ? -32000.0 : 24.0; var y = 24.0;
        var main = new MainWindow(services) { IsDemo = true, Left = x, Top = y, Topmost = false };
        main.ShowActivated = !snapping; main.Show();
        if (args.Contains("--snack")) main.DemoSnack("已记录 +300 ml");
        if (args.Contains("--edit")) main.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, main.BeginEditTotal);

        var compactServices = new AppServices(storage);
        compactServices.Settings.CompactMode = true;
        var compact = new MainWindow(compactServices) { IsDemo = true, Left = x, Top = y + main.ActualHeight + 8, Topmost = false };
        compact.ShowActivated = !snapping; compact.Show();
        if (args.Contains("--edit")) compact.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, compact.BeginEditTotal);

        var settingsWin = new SettingsWindow(services, firstRun: args.Contains("--first")) { WindowStartupLocation = WindowStartupLocation.Manual, Left = x + 360, Top = y };
        settingsWin.ShowActivated = !snapping; settingsWin.Show();

        var amount = new AmountWindow(initial: 300, cup: 300) { WindowStartupLocation = WindowStartupLocation.Manual, Left = x + 880, Top = y };
        amount.ShowActivated = !snapping; amount.Show();

        var records = new RecordsWindow(services) { WindowStartupLocation = WindowStartupLocation.Manual, Left = x + 880, Top = y + amount.ActualHeight };
        records.ShowActivated = !snapping; records.Show();

        var confirm = new ConfirmDialog("清空今天的所有记录？", "今天的饮水进度将归零，此操作无法撤销。", "清空", danger: true)
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = x, Top = y + main.ActualHeight + compact.ActualHeight + 8 };
        confirm.ShowActivated = !snapping; confirm.Show();

        var snapIdx = Array.IndexOf(args, "--snap");
        if (snapIdx >= 0 && snapIdx + 1 < args.Length)
        {
            var outDir = args[snapIdx + 1];
            Directory.CreateDirectory(outDir);
            var tag = args.Contains("--dark") ? "dark" : args.Contains("--light") ? "light" : "sys";
            if (done) tag += "-done"; if (empty) tag += "-empty"; if (args.Contains("--first")) tag += "-first"; if (args.Contains("--snack")) tag += "-snack"; if (args.Contains("--edit")) tag += "-edit";
            var named = new (Window w, string n)[] { (main, "main"), (compact, "compact"), (settingsWin, "settings"), (amount, "amount"), (records, "records"), (confirm, "confirm") };
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                foreach (var (w, n) in named) Snap(w, Path.Combine(outDir, $"{tag}-{n}.png"));
                Application.Current.Shutdown();
            };
            t.Start();
            return;
        }

        foreach (Window w in Application.Current.Windows) w.Closed += (_, _) => { if (Application.Current.Windows.Count == 0) Application.Current.Shutdown(); };
    }
}
