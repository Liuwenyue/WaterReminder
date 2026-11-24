using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WaterReminder.Services;

namespace WaterReminder.Controls;

public static class WindowEx
{
    /// <summary>
    /// Lets a borderless window be dragged from empty space. Text boxes and buttons keep the click.
    /// When <paramref name="workAreas"/> is provided, edges stay inside the desktop working area while dragging.
    /// </summary>
    public static void EnableDrag(Window window, Action? dragged = null, Func<IReadOnlyList<ScreenRect>>? workAreas = null,
        Func<bool>? canClamp = null)
    {
        var clamping = false;
        if (workAreas is not null)
        {
            window.LocationChanged += (_, _) =>
            {
                if (clamping || !window.IsLoaded || canClamp?.Invoke() == false) return;
                var areas = workAreas();
                if (areas.Count == 0) return;
                var (left, top) = WindowPlacement.ClampToVisible(window.Left, window.Top, window.ActualWidth, window.ActualHeight, areas);
                if (Math.Abs(left - window.Left) < 0.01 && Math.Abs(top - window.Top) < 0.01) return;
                clamping = true;
                try
                {
                    window.Left = left;
                    window.Top = top;
                }
                finally
                {
                    clamping = false;
                }
            };
        }

        window.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            if (IsInteractive(e.OriginalSource as DependencyObject)) return;
            try
            {
                window.DragMove();
                if (workAreas is not null)
                {
                    var areas = workAreas();
                    var (left, top) = WindowPlacement.ClampToVisible(window.Left, window.Top, window.ActualWidth, window.ActualHeight, areas);
                    window.Left = left;
                    window.Top = top;
                }
                dragged?.Invoke();
            }
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
