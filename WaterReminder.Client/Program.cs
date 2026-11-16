using System.IO;
using System.Threading;
using System.Windows.Forms;
using WaterReminder.Services;

namespace WaterReminder.Client;

internal static class Program
{
    private const string MutexName = @"Local\WaterReminder.Client.SingleInstance";
    private const string ShowEventName = @"Local\WaterReminder.Client.ShowWindow";

    [STAThread]
    private static void Main(string[] args)
    {
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
            try { tunnel.EnsureReadyAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult(); }
            catch { }
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var context = new TrayApplicationContext(config, tunnel, showEvent);
        Application.Run(context);
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
