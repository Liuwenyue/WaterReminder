using WaterReminder.Services;

namespace WaterReminder.Server;

/// <summary>
/// Headless Water Reminder. Listens on port 8787 by default (WATERREMINDER_PORT).
/// Reminder decisions use Asia/Shanghai even when the VM clock is UTC.
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var dryRun = args.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));
        ServerOptions options;
        try
        {
            options = ServerOptions.FromEnvironment(requireApiToken: !dryRun);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
            return;
        }

        using var engine = new ReminderEngine(new ReminderEngineOptions
        {
            DataDirectory = options.DataDirectory,
            Clock = ShanghaiClock.Now,
            PushWardToken = () => SecretFiles.PushWardToken(options.DataDirectory),
            PollAnswers = !dryRun,
        });

        if (dryRun)
        {
            Console.WriteLine(engine.Describe());
            return;
        }

        var app = ServerHost.Build(options, engine);
        Console.WriteLine($"WaterReminder listening on {options.ListenUrl} ({ShanghaiClock.Id})");
        await app.RunAsync();
    }
}
