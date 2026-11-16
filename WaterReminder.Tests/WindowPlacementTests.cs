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
    public void ToggleKeepsTopLeft_ThenClamps()
    {
        var s = new UserSettings();
        WindowPlacement.Remember(s, 1189.6, 673.6);
        var compact = new ScreenRect(1189.6, 673.6, W, CompactH);
        // Expand: same top-left, taller window is clamped up into the work area.
        var exp = WindowPlacement.ClampToVisible(compact.Left, compact.Top, W, ExpandedH, One);
        Assert.Equal(1184, exp.Left); // right edge pulled in
        Assert.True(exp.Top <= compact.Top);
        Assert.True(Primary.Contains(new ScreenRect(exp.Left, exp.Top, W, ExpandedH)));

        // Collapse: keep that top-left (shared anchor).
        var back = WindowPlacement.ClampToVisible(exp.Left, exp.Top, W, CompactH, One);
        Assert.Equal(exp.Left, back.Left);
        Assert.Equal(exp.Top, back.Top);
    }

    [Fact]
    public void SharedRemember_IsUsedByBothModes()
    {
        var s = new UserSettings();
        WindowPlacement.Remember(s, 900, 600);
        Assert.Equal((900.0, 600.0), WindowPlacement.Saved(s));
        Assert.Equal((900.0, 600.0), WindowPlacement.Resolve(s, W, CompactH, One));
        // Expanded is taller: same left, top pulled up so the window stays on screen.
        Assert.Equal((900.0, Primary.Bottom - ExpandedH), WindowPlacement.Resolve(s, W, ExpandedH, One));
        Assert.Equal(900, s.CompactLeft);
        Assert.Equal(900, s.ExpandedLeft);
    }

    [Fact]
    public void PersistsAcrossRestart_JsonRoundTripKeepsOtherFields()
    {
        var s = new UserSettings { DailyGoalMl = 2220, CupCapacityMl = 370, CompactMode = true, NoDrinkBeforeEndMinutes = 30 };
        WindowPlacement.Remember(s, 900, 600);
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var r = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(json)!;
        WindowPlacement.Migrate(r);
        Assert.Equal((900.0, 600.0), WindowPlacement.Resolve(r, W, CompactH, One));
        Assert.Equal(2220, r.DailyGoalMl);
        Assert.Equal(370, r.CupCapacityMl);
        Assert.Equal(30, r.NoDrinkBeforeEndMinutes);
    }

    [Fact]
    public void LegacyPerModePositionMigratesIntoSharedAnchor()
    {
        var r = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(
            "{\"CompactMode\":true,\"CompactLeft\":1189.6,\"CompactTop\":673.6,\"DailyGoalMl\":2220}")!;
        WindowPlacement.Migrate(r);
        Assert.Equal((1189.6, 673.6), WindowPlacement.Saved(r));
        Assert.Equal(1189.6, r.WindowLeft);
    }

    [Fact]
    public void LegacyWindowLeftMigratesIntoSharedAnchor()
    {
        var r = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(
            "{\"CompactMode\":true,\"WindowLeft\":1189.6,\"WindowTop\":673.6,\"DailyGoalMl\":2220}")!;
        WindowPlacement.Migrate(r);
        Assert.Equal((1189.6, 673.6), WindowPlacement.Saved(r));
    }

    [Theory]
    [InlineData(3000, 600)]     // monitor that no longer exists (right)
    [InlineData(-2000, -900)]   // old left/top monitor
    [InlineData(1400, 790)]     // resolution shrank, partly off-screen
    public void OffScreenSpotIsPulledBackToPrimary(double l, double t)
    {
        var s = new UserSettings();
        WindowPlacement.Remember(s, l, t);
        var p = WindowPlacement.Resolve(s, W, CompactH, One);
        Assert.True(Primary.Contains(new ScreenRect(p.Left, p.Top, W, CompactH)), $"{p}");
    }

    [Fact]
    public void SecondMonitorSpotIsKept_UntilUnplugged()
    {
        ScreenRect[] two = [Primary, new(1536, 0, 1920, 1040)];
        var s = new UserSettings();
        WindowPlacement.Remember(s, 2500, 700);
        Assert.Equal((2500.0, 700.0), WindowPlacement.Resolve(s, W, CompactH, two));
        var p = WindowPlacement.Resolve(s, W, CompactH, One);
        Assert.True(Primary.Contains(new ScreenRect(p.Left, p.Top, W, CompactH)));
    }

    [Fact]
    public void ClampDuringDrag_KeepsAllEdgesInside()
    {
        var (left, top) = WindowPlacement.ClampToVisible(-40, -20, W, CompactH, One);
        Assert.Equal(0, left);
        Assert.Equal(0, top);
        var (right, bottom) = WindowPlacement.ClampToVisible(2000, 900, W, CompactH, One);
        Assert.Equal(Primary.Right - W, right);
        Assert.Equal(Primary.Bottom - CompactH, bottom);
    }

    [Fact]
    public void NothingSavedDefaultsToBottomRight()
        => Assert.Equal((1536 - W - 8, 816 - CompactH - 8), WindowPlacement.Resolve(new UserSettings(), W, CompactH, One));
}
