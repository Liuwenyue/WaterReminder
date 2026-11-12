using System.Windows;
using System.Windows.Interop;
using FormsIntegration = System.Windows.Forms.Integration;

namespace WaterReminder.Client;

/// <summary>
/// The tray process runs a WinForms message loop. A WPF window opened from that loop
/// does not receive keystrokes unless modeless keyboard interop is turned on.
/// </summary>
internal static class WpfInterop
{
    public static void EnableKeyboard(Window window)
    {
        ClientTheme.Ensure();
        if (PresentationSource.FromVisual(window) is not null)
            FormsIntegration.ElementHost.EnableModelessKeyboardInterop(window);
        else
            window.SourceInitialized += OnSourceInitialized;
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        var window = (Window)sender!;
        window.SourceInitialized -= OnSourceInitialized;
        FormsIntegration.ElementHost.EnableModelessKeyboardInterop(window);
    }
}
