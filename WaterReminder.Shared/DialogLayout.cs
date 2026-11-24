using System.Windows;
using System.Windows.Media;
using WaterReminder.Services;
using FormsScreen = System.Windows.Forms.Screen;

namespace WaterReminder;

/// <summary>
/// Opens a dialog in the middle of the screen. A window that is too tall shrinks and keeps its buttons visible.
/// </summary>
public static class DialogLayout
{
    public static void KeepOnScreen(Window window)
    {
        window.SourceInitialized += (_, _) => Limit(window);
        window.ContentRendered += (_, _) => Move(window);
    }

    private static void Limit(Window window)
    {
        var areas = WorkAreas(window);
        if (areas.Count == 0) return;
        var fit = WindowPlacement.CenterOnScreen(320, 240, areas, CursorPoint(window));
        window.MaxWidth = fit.MaxWidth;
        window.MaxHeight = fit.MaxHeight;
    }

    private static void Move(Window window)
    {
        if (window.ActualWidth <= 1 || window.ActualHeight <= 1) return;
        var areas = WorkAreas(window);
        if (areas.Count == 0) return;
        ScreenRect? near = CursorPoint(window);
        if (double.IsFinite(window.Left) && double.IsFinite(window.Top))
            near = new ScreenRect(window.Left + window.ActualWidth / 2, window.Top + window.ActualHeight / 2, 1, 1);
        var fit = WindowPlacement.CenterOnScreen(window.ActualWidth, window.ActualHeight, areas, near);
        window.MaxWidth = fit.MaxWidth;
        window.MaxHeight = fit.MaxHeight;
        window.UpdateLayout();
        fit = WindowPlacement.CenterOnScreen(Math.Max(1, window.ActualWidth), Math.Max(1, window.ActualHeight), areas, near);
        window.Left = fit.Left;
        window.Top = fit.Top;
    }

    private static ScreenRect? CursorPoint(Visual visual)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(visual);
            var point = System.Windows.Forms.Cursor.Position;
            var scaleX = dpi.DpiScaleX == 0 ? 1 : dpi.DpiScaleX;
            var scaleY = dpi.DpiScaleY == 0 ? 1 : dpi.DpiScaleY;
            return new ScreenRect(point.X / scaleX, point.Y / scaleY, 1, 1);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<ScreenRect> WorkAreas(Visual visual)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(visual);
            var scaleX = dpi.DpiScaleX == 0 ? 1 : dpi.DpiScaleX;
            var scaleY = dpi.DpiScaleY == 0 ? 1 : dpi.DpiScaleY;
            return FormsScreen.AllScreens
                .OrderByDescending(x => x.Primary)
                .Select(x => new ScreenRect(
                    x.WorkingArea.Left / scaleX,
                    x.WorkingArea.Top / scaleY,
                    x.WorkingArea.Width / scaleX,
                    x.WorkingArea.Height / scaleY))
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
