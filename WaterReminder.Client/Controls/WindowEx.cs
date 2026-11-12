using System.Windows;
using System.Windows.Input;

namespace WaterReminder.Client.Controls;

public static class WindowEx
{
    /// <summary>Lets a borderless window be dragged by any empty area.</summary>
    public static void EnableDrag(Window window, Action? dragged = null)
    {
        window.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { window.DragMove(); dragged?.Invoke(); } catch (InvalidOperationException) { }
        };
    }
}
