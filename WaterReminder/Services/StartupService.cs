using Microsoft.Win32;

namespace WaterReminder.Services;

public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "WaterReminder";

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key?.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
            else key?.DeleteValue(AppName, false);
        }
        catch { }
    }
}
