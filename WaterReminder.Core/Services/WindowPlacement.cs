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
/// Compact-mode top-left is the stable anchor (pure logic, unit-tested).
/// Expanding may move the window so the taller frame stays on screen; collapsing returns to the anchor.
/// </summary>
public static class WindowPlacement
{
    /// <summary>
    /// Prefer the shared WindowLeft/Top. Older per-mode Compact*/Expanded* slots collapse into that anchor.
    /// </summary>
    public static void Migrate(UserSettings s)
    {
        if (s.WindowLeft is double sharedLeft && s.WindowTop is double sharedTop)
        {
            s.CompactLeft = sharedLeft; s.CompactTop = sharedTop;
            s.ExpandedLeft = sharedLeft; s.ExpandedTop = sharedTop;
            return;
        }

        double? left = s.CompactLeft ?? s.ExpandedLeft;
        double? top = s.CompactTop ?? s.ExpandedTop;
        if (left is not double l || top is not double t) return;
        s.WindowLeft = l;
        s.WindowTop = t;
        s.CompactLeft = l; s.CompactTop = t;
        s.ExpandedLeft = l; s.ExpandedTop = t;
    }

    public static (double Left, double Top)? Saved(UserSettings s)
        => s.WindowLeft is double l && s.WindowTop is double t ? (l, t) : null;

    /// <summary>Obsolete per-mode lookup kept for older call sites; both modes share the same anchor.</summary>
    public static (double Left, double Top)? Saved(UserSettings s, bool compact) => Saved(s);

    /// <summary>
    /// Stores the compact-mode anchor. Expanded placement is derived from it and is not remembered separately.
    /// </summary>
    public static void Remember(UserSettings s, double left, double top)
    {
        s.WindowLeft = left; s.WindowTop = top;
        s.CompactLeft = left; s.CompactTop = top;
        s.ExpandedLeft = left; s.ExpandedTop = top;
    }

    /// <summary>Obsolete signature: the compact anchor is stored either way. Expanded placement is derived.</summary>
    public static void Remember(UserSettings s, bool compact, double left, double top)
        => Remember(s, left, top);

    /// <summary>
    /// Compact mode returns the saved anchor. Expanded mode starts there, then moves only as far as needed
    /// to keep the taller window inside a work area.
    /// </summary>
    public static (double Left, double Top) Place(UserSettings s, bool compact, double width, double height,
        IReadOnlyList<ScreenRect> workAreas, ScreenRect? current = null)
    {
        var anchor = Resolve(s, width, height, workAreas, current);
        return compact ? anchor : ClampToVisible(anchor.Left, anchor.Top, width, height, workAreas);
    }

    /// <summary>
    /// Where the compact anchor is. Uses the saved spot; otherwise keeps the current top-left;
    /// with nothing saved, bottom-right of the primary work area.
    /// Always pulled fully into a visible work area. If the saved monitor is gone, the primary area is used.
    /// </summary>
    public static (double Left, double Top) Resolve(UserSettings s, double width, double height,
        IReadOnlyList<ScreenRect> workAreas, ScreenRect? current = null)
    {
        double left, top;
        if (Saved(s) is { } p) (left, top) = p;
        else if (current is { } c) { left = c.Left; top = c.Top; }
        else
        {
            var a = workAreas.Count > 0 ? workAreas[0] : new ScreenRect(0, 0, 1920, 1040);
            left = a.Right - width - 8; top = a.Bottom - height - 8;
        }
        return ClampToVisible(left, top, width, height, workAreas);
    }

    /// <summary>Obsolete signature: mode is ignored.</summary>
    public static (double Left, double Top) Resolve(UserSettings s, bool compact, double width, double height,
        IReadOnlyList<ScreenRect> workAreas, ScreenRect? current = null)
        => Resolve(s, width, height, workAreas, current);

    /// <summary>
    /// Fully inside some work area: unchanged. Otherwise moved into the area it overlaps most
    /// (or the nearest / primary one when the old monitor is gone).
    /// </summary>
    public static (double Left, double Top) ClampToVisible(double left, double top, double width, double height, IReadOnlyList<ScreenRect> workAreas)
    {
        if (workAreas.Count == 0 || double.IsNaN(left) || double.IsNaN(top)) return (left, top);
        var win = new ScreenRect(left, top, width, height);
        if (workAreas.Any(a => a.Contains(win))) return (left, top);
        var target = workAreas.OrderByDescending(a => a.Overlap(win)).ThenBy(a => a.DistanceTo(win)).First();
        if (target.Overlap(win) == 0) target = workAreas[0];
        var l = Math.Clamp(left, target.Left, Math.Max(target.Left, target.Right - width));
        var t = Math.Clamp(top, target.Top, Math.Max(target.Top, target.Bottom - height));
        return (l, t);
    }
}
