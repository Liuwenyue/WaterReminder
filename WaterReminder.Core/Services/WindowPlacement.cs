namespace WaterReminder.Services;

public readonly record struct ScreenRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
    public bool Contains(ScreenRect r) => r.Left >= Left && r.Top >= Top && r.Right <= Right && r.Bottom <= Bottom;
    public double Overlap(ScreenRect r)
    {
        var w = Math.Min(Right, r.Right) - Math.Max(Left, r.Left);
        var h = Math.Min(Bottom, r.Bottom) - Math.Max(Top, r.Top);
        return w > 0 && h > 0 ? w * h : 0;
    }
    public double DistanceTo(ScreenRect r)
    {
        var dx = Math.Max(0, Math.Max(Left - r.Right, r.Left - Right));
        var dy = Math.Max(0, Math.Max(Top - r.Bottom, r.Top - Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
/// Remembers where the compact and the expanded window live, separately (pure logic, unit-tested).
/// Compact position = where the user last dragged the compact window; expanding/collapsing never overwrites it.
/// </summary>
public static class WindowPlacement
{
    /// <summary>One-time migration of the legacy WindowLeft/WindowTop (written by whichever mode was active) into the per-mode slot.</summary>
    public static void Migrate(UserSettings s)
    {
        if (s.CompactLeft is not null || s.ExpandedLeft is not null) return;
        if (s.WindowLeft is not double l || s.WindowTop is not double t) return;
        if (s.CompactMode) { s.CompactLeft = l; s.CompactTop = t; }
        else { s.ExpandedLeft = l; s.ExpandedTop = t; }
    }

    public static (double Left, double Top)? Saved(UserSettings s, bool compact)
        => compact
            ? (s.CompactLeft is double l && s.CompactTop is double t ? (l, t) : null)
            : (s.ExpandedLeft is double el && s.ExpandedTop is double et ? (el, et) : null);

    /// <summary>Called after the user drags the window: stores the position for the current mode only.</summary>
    public static void Remember(UserSettings s, bool compact, double left, double top)
    {
        if (compact) { s.CompactLeft = left; s.CompactTop = top; }
        else { s.ExpandedLeft = left; s.ExpandedTop = top; }
        s.WindowLeft = left; s.WindowTop = top; // legacy field kept in sync
    }

    /// <summary>
    /// Where to put the window for <paramref name="compact"/> mode. Uses the saved spot; the expanded window
    /// without a saved spot grows upward from the compact window's bottom edge; with nothing saved, bottom-right of the primary work area.
    /// Always pulled fully into a visible work area.
    /// </summary>
    public static (double Left, double Top) Resolve(UserSettings s, bool compact, double width, double height,
        IReadOnlyList<ScreenRect> workAreas, ScreenRect? current = null)
    {
        double left, top;
        if (Saved(s, compact) is { } p) (left, top) = p;
        else if (current is { } c) { left = c.Left; top = c.Bottom - height; }   // keep bottom edge anchored
        else
        {
            var a = workAreas.Count > 0 ? workAreas[0] : new ScreenRect(0, 0, 1920, 1040);
            left = a.Right - width - 8; top = a.Bottom - height - 8;
        }
        return ClampToVisible(left, top, width, height, workAreas);
    }

    /// <summary>Fully inside some work area: unchanged. Otherwise moved into the area it overlaps most (or the nearest one).</summary>
    public static (double Left, double Top) ClampToVisible(double left, double top, double width, double height, IReadOnlyList<ScreenRect> workAreas)
    {
        if (workAreas.Count == 0 || double.IsNaN(left) || double.IsNaN(top)) return (left, top);
        var win = new ScreenRect(left, top, width, height);
        if (workAreas.Any(a => a.Contains(win))) return (left, top);
        var target = workAreas.OrderByDescending(a => a.Overlap(win)).ThenBy(a => a.DistanceTo(win)).First();
        if (target.Overlap(win) == 0) target = workAreas.OrderBy(a => a.DistanceTo(win)).First();
        var l = Math.Clamp(left, target.Left, Math.Max(target.Left, target.Right - width));
        var t = Math.Clamp(top, target.Top, Math.Max(target.Top, target.Bottom - height));
        return (l, t);
    }
}
