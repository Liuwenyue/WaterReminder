using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Threading;
using WaterReminder.Services;

namespace WaterReminder.Client;

internal static class Program
{
    private const string MutexName = @"Local\WaterReminder.Client.SingleInstance";
    private const string ShowEventName = @"Local\WaterReminder.Client.ShowWindow";

    [STAThread]
    private static void Main(string[] args)
    {
        AttachGlobalExceptionHandlers();
        FileLogger.Default.Info("WaterReminder.Client started.");

        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch { }
            return;
        }

        var path = ResolvePath(args);
        ClientConfig config;
        try
        {
            config = ClientConfig.Load(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message + Environment.NewLine + Environment.NewLine + path,
                "喝水提醒",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        SshTunnelManager? tunnel = null;
        if (SshTunnelSpec.IsConfigured(config.Tunnel))
        {
            tunnel = new SshTunnelManager(config.Tunnel!);
            tunnel.Start();
            try { tunnel.EnsureReadyAsync(SshTunnelSpec.ReadyTimeout(config.Tunnel!)).GetAwaiter().GetResult(); }
            catch { }
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var context = new TrayApplicationContext(config, tunnel, showEvent);
        Application.Run(context);
    }

    /// <summary>
    /// Global last-resort handlers: log every unhandled exception instead of
    /// crashing silently. Hooked before anything else runs.
    /// </summary>
    private static void AttachGlobalExceptionHandlers()
    {
        // WinForms UI thread: log and let the message loop continue.
        Application.ThreadException += (_, e) =>
            FileLogger.Default.Error("Unhandled WinForms exception", e.Exception);

        // The client hosts WPF windows on this thread, but no WPF Application
        // exists yet at startup, so hook this thread's dispatcher directly.
        // Marking the exception handled keeps one UI fault from killing the process.
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            FileLogger.Default.Error("Unhandled WPF dispatcher exception", e.Exception);
            e.Handled = true;
        };

        // Background threads / fatal errors: can only log; the runtime still exits.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            FileLogger.Default.Error("Unhandled exception (terminating: " + e.IsTerminating + ")",
                e.ExceptionObject as Exception);
    }

    private static string ResolvePath(string[] args)
    {
        var fromArgs = args.FirstOrDefault(a => a.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        if (fromArgs is { Length: > 0 }) return fromArgs;
        var besideExe = Path.Combine(AppContext.BaseDirectory, "client.json");
        if (File.Exists(besideExe)) return besideExe;
        return ClientConfig.DefaultPath();
    }
}
