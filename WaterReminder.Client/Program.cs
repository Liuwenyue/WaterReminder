using System.IO;
using System.Windows.Forms;

namespace WaterReminder.Client;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
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

        Application.Run(new TrayApplicationContext(config));
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
