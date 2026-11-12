using System.Windows;
using Microsoft.Win32;

namespace WaterReminder.Client;

/// <summary>Loads the same light/dark dictionaries the original desktop window uses.</summary>
internal static class ClientTheme
{
    public static void Ensure()
    {
        if (Application.Current is null)
            new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        var app = Application.Current!;
        if (app.Resources.MergedDictionaries.Any(d => d.Source?.OriginalString.Contains("Controls.xaml", StringComparison.Ordinal) == true))
            return;

        var colors = PrefersDark() ? "Themes/Dark.xaml" : "Themes/Light.xaml";
        app.Resources.MergedDictionaries.Add(Dictionary(colors));
        app.Resources.MergedDictionaries.Add(Dictionary("Themes/Controls.xaml"));
    }

    private static ResourceDictionary Dictionary(string path)
        => new() { Source = new Uri($"pack://application:,,,/{path}", UriKind.Absolute) };

    private static bool PrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
