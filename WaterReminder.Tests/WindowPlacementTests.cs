using WaterReminder.Services;
using WaterReminder;
using Xunit;

namespace WaterReminder.Tests;

public sealed class WindowPlacementTests
{
    private static readonly ScreenRect Primary = new(0, 0, 1536, 816);          // 1920x1080 @125%, taskbar excluded
    private static readonly ScreenRect[] One = [Primary];
    private const double W = 352, CompactH = 76, ExpandedH = 520;

    [Fact]
    public void CollapseReturnsToCompactSpot_AfterExpand()
    {
        var s = new UserSettings { CompactMode = true };
        WindowPlacement.Remember(s, compact: true, 1189.6, 673.6);                // user dragged compact window
        var compactRect = new ScreenRect(1189.6, 673.6, W, CompactH);
        // expand (no expanded spot yet): grows upward, clamped on screen
        var exp = WindowPlacement.Resolve(s, compact: false, W, ExpandedH, One, compactRect);
        Assert.Equal(1184, exp.Left);                                               // pulled in from the right edge
        Assert.Equal(673.6 + CompactH - ExpandedH, exp.Top, 3);
        // collapse: exactly the compact spot again
        var back = WindowPlacement.Resolve(s, compact: true, W, CompactH, One, new ScreenRect(exp.Left, exp.Top, W, ExpandedH));
        Assert.Equal((1184.0, 673.6), (back.Left, Math.Round(back.Top, 1)));       // 1189.6 itself is 5.6px off-screen -> clamped
    }

    [Fact]
    public void CollapseReturnsExactlyWhenFullyVisible()
    {
        var s = new UserSettings { CompactMode = true };
        WindowPlacement.Remember(s, true, 900, 600);
        var exp = WindowPlacement.Resolve(s, false, W, ExpandedH, One, new ScreenRect(900, 600, W, CompactH));
        var back = WindowPlacement.Resolve(s, true, W, CompactH, One, new ScreenRect(exp.Left, exp.Top, W, ExpandedH));
        Assert.Equal((900.0, 600.0), back);
    }

    [Fact]
    public void ExpandedDragDoesNotOverwriteCompactSpot()
    {
        var s = new UserSettings();
        WindowPlacement.Remember(s, true, 900, 600);
        WindowPlacement.Remember(s, false, 100, 50);                              // user drags the big window
        Assert.Equal((900.0, 600.0), WindowPlacement.Saved(s, true));
        Assert.Equal((100.0, 50.0), WindowPlacement.Saved(s, false));
        Assert.Equal((100.0, 50.0), WindowPlacement.Resolve(s, false, W, ExpandedH, One, new ScreenRect(900, 600, W, CompactH)));
        Assert.Equal((900.0, 600.0), WindowPlacement.Resolve(s, true, W, CompactH, One, new ScreenRect(100, 50, W, ExpandedH)));
    }

    [Fact]
    public void PersistsAcrossRestart_JsonRoundTripKeepsOtherFields()
    {
        var s = new UserSettings { DailyGoalMl = 2220, CupCapacityMl = 370, CompactMode = true, NoDrinkBeforeEndMinutes = 30 };
        WindowPlacement.Remember(s, true, 900, 600);
        WindowPlacement.Remember(s, false, 100, 50);
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var r = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(json)!;
        WindowPlacement.Migrate(r);
        Assert.Equal((900.0, 600.0), WindowPlacement.Resolve(r, true, W, CompactH, One));
        Assert.Equal(2220, r.DailyGoalMl);
        Assert.Equal(370, r.CupCapacityMl);
        Assert.Equal(30, r.NoDrinkBeforeEndMinutes);
    }

    [Fact]
    public void LegacyPositionMigratesIntoCurrentMode()
    {
        // user's existing settings.json: compact mode, WindowLeft/Top only
        var r = System.Text.Json.JsonSerializer.Deserialize<UserSettings>("{\"CompactMode\":true,\"WindowLeft\":1189.6,\"WindowTop\":673.6,\"DailyGoalMl\":2220}")!;
        WindowPlacement.Migrate(r);
        Assert.Equal((1189.6, 673.6), WindowPlacement.Saved(r, true));
        Assert.Null(WindowPlacement.Saved(r, false));
    }

    [Theory]
    [InlineData(3000, 600)]     // monitor that no longer exists (right)
    [InlineData(-2000, -900)]   // old left/top monitor
    [InlineData(1400, 790)]     // resolution shrank, partly off-screen
    public void OffScreenSpotIsPulledBack(double l, double t)
    {
        var s = new UserSettings();
        WindowPlacement.Remember(s, true, l, t);
        var p = WindowPlacement.Resolve(s, true, W, CompactH, One);
        Assert.True(Primary.Contains(new ScreenRect(p.Left, p.Top, W, CompactH)), $"{p}");
    }

    [Fact]
    public void SecondMonitorSpotIsKept()
    {
        ScreenRect[] two = [Primary, new(1536, 0, 1920, 1040)];
        var s = new UserSettings();
        WindowPlacement.Remember(s, true, 2500, 700);
        Assert.Equal((2500.0, 700.0), WindowPlacement.Resolve(s, true, W, CompactH, two));
        // monitor unplugged -> back onto the primary
        var p = WindowPlacement.Resolve(s, true, W, CompactH, One);
        Assert.True(Primary.Contains(new ScreenRect(p.Left, p.Top, W, CompactH)));
    }

    [Fact]
    public void NothingSavedDefaultsToBottomRight()
        => Assert.Equal((1536 - W - 8, 816 - CompactH - 8), WindowPlacement.Resolve(new UserSettings(), true, W, CompactH, One));
}
