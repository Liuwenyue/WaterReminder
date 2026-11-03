using System.IO;
using System.Threading;
using System.Windows;
using WaterReminder.Services;

namespace WaterReminder;

public partial class App : System.Windows.Application
{
    private const string MutexName = "WaterReminder.SingleInstance";
    private const string ShowEventName = "WaterReminder.ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private TrayIconService? _tray;
    private ReminderService? _reminderService;
    private MainWindow? _mainWindow;

    public AppServices Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--ui-demo"))
        {
            if (e.Args.Contains("--dark")) ThemeManager.ForceDark = true;
            if (e.Args.Contains("--light")) ThemeManager.ForceDark = false;
            ThemeManager.Initialize();
            UiDemo.Run(e.Args);
            return;
        }

        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // Another instance is running: ask it to show its window, then exit.
            try { if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing)) { existing.Set(); existing.Dispose(); } } catch { }
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        ThemeManager.Initialize();
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, Timeout.Infinite, false);

        Services = new AppServices();
        StartupService.SetEnabled(Services.Settings.AutoStart);
        _mainWindow = new MainWindow(Services);
        _tray = new TrayIconService(Services, ShowMainWindow, OpenSettings, ExitApplication);
        _reminderService = new ReminderService(Services, ShowMainWindow, text => _tray!.ShowReminder(text));
        _reminderService.Start();
        // any change to today's total / settings / reminder state -> recompute next reminder everywhere right away
        Services.Progress.Changed += () => { _tray?.UpdateText(); _mainWindow?.RefreshView(); };

        _mainWindow.Closing += (_, args) =>
        {
            if (!Services.IsExiting)
            {
                args.Cancel = true;
                _mainWindow.Hide();
            }
        };

        if (Services.Settings.FirstRun)
        {
            _mainWindow.Show();
            _mainWindow.OpenSettings(firstRun: true);
        }
        else if (Services.Settings.FloatingWindow)
        {
            _mainWindow.Show();
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void OpenSettings()
    {
        if (_mainWindow is null) return;
        ShowMainWindow();
        _mainWindow.OpenSettings();
    }

    private void ExitApplication()
    {
        Services.IsExiting = true;
        _reminderService?.Stop();
        _tray?.Dispose();
        _mainWindow?.Close();
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        Shutdown();
    }
}

public sealed class AppServices
{
    public StorageService Storage { get; }
    public UserSettings Settings { get; }
    public ProgressTracker Progress { get; }
    public DailyRecord Today => Progress.Today;
    public bool IsExiting { get; set; }

    public AppServices() : this(new StorageService()) { }

    public AppServices(StorageService storage)
    {
        Storage = storage;
        Settings = Storage.LoadSettings();
        WindowPlacement.Migrate(Settings);
        Progress = new ProgressTracker(Settings, Storage.LoadDailyRecord, Storage.SaveDailyRecord);
    }

    public void RefreshToday() => Progress.Reload();
}
