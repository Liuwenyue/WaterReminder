using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace WaterReminder.Client.Controls;

public static class WindowEx
{
    /// <summary>Lets a borderless window be dragged from empty space. Text boxes and buttons keep the click.</summary>
    public static void EnableDrag(Window window, Action? dragged = null)
    {
        window.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            if (IsInteractive(e.OriginalSource as DependencyObject)) return;
            try { window.DragMove(); dragged?.Invoke(); }
            catch (InvalidOperationException) { }
        };
    }

    private static bool IsInteractive(DependencyObject? source)
    {
        for (var current = source; current is not null; current = Parent(current))
        {
            if (current is TextBoxBase or ButtonBase or PasswordBox) return true;
        }
        return false;
    }

    private static DependencyObject? Parent(DependencyObject current)
    {
        if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        return LogicalTreeHelper.GetParent(current);
    }
}
