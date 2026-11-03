using System.Windows;
using Microsoft.Win32;

namespace WaterReminder.Services;

/// <summary>Follows the Windows app light/dark setting by swapping the color dictionary.</summary>
public static class ThemeManager
{
    public static bool? ForceDark { get; set; }
    public static bool IsDark { get; private set; }

    public static void Initialize()
    {
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
    }

    public static void Apply()
    {
        var dark = ForceDark ?? SystemPrefersDark();
        var app = Application.Current;
        if (app is null) return;
        var dicts = app.Resources.MergedDictionaries;
        var uri = new Uri(dark ? "pack://application:,,,/Themes/Dark.xaml" : "pack://application:,,,/Themes/Light.xaml", UriKind.Absolute);
        var existing = dicts.FirstOrDefault(d => d.Source is not null && (d.Source.OriginalString.EndsWith("Light.xaml") || d.Source.OriginalString.EndsWith("Dark.xaml")));
        if (existing is not null && IsDark == dark && existing.Source!.OriginalString.EndsWith(dark ? "Dark.xaml" : "Light.xaml")) return;
        var replacement = new ResourceDictionary { Source = uri };
        if (existing is null) dicts.Insert(0, replacement);
        else dicts[dicts.IndexOf(existing)] = replacement;
        IsDark = dark;
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }
}
