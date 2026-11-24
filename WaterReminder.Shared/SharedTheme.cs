using System.Windows;
using Microsoft.Win32;

namespace WaterReminder;

/// <summary>Loads the light/dark dictionaries both desktop clients share.</summary>
public static class SharedTheme
{
    public static void Ensure()
    {
        if (Application.Current is null)
            new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        var app = Application.Current!;
        if (app.Resources.MergedDictionaries.Any(d => d.Source?.OriginalString.Contains("Controls.xaml", StringComparison.Ordinal) == true))
            return;

        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DictionaryUri(PrefersDark() ? "Dark.xaml" : "Light.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DictionaryUri("Controls.xaml") });
    }

    /// <summary>Reads the Windows app light/dark setting. Defaults to light when it cannot be read.</summary>
    public static bool PrefersDark()
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

    public static Uri DictionaryUri(string fileName)
        => new($"pack://application:,,,/WaterReminder.Shared;component/Themes/{fileName}", UriKind.Absolute);
}
