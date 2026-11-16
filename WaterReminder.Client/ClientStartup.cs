using Microsoft.Win32;

namespace WaterReminder.Client;

/// <summary>HKCU Run entry for the Windows tray client. Local preference only.</summary>
internal static class ClientStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "WaterReminderClient";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(AppName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var path = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(path)) return;
                key?.SetValue(AppName, $"\"{path}\"");
            }
            else
            {
                key?.DeleteValue(AppName, false);
            }
        }
        catch { }
    }
}
