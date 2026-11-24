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
    public void ToggleReturnsToCompactAnchor_AfterExpandedClamp()
    {
        var s = new UserSettings();
        WindowPlacement.Remember(s, 1189.6, 673.6);
        var compactPoint = WindowPlacement.Place(s, compact: true, W, CompactH, One);
        var compact = new ScreenRect(compactPoint.Left, compactPoint.Top, W, CompactH);
        // Expand: start at the compact anchor, then pull the taller window fully onto the desktop.
        var exp = WindowPlacement.Place(s, compact: false, W, ExpandedH, One);
        Assert.Equal(1184, exp.Left); // right edge pulled in
        Assert.True(exp.Top <= compact.Top);
        Assert.True(Primary.Contains(new ScreenRect(exp.Left, exp.Top, W, ExpandedH)));

        // Collapse: return to the compact spot the user originally chose.
        var back = WindowPlacement.Place(s, compact: true, W, CompactH, One);
        Assert.Equal(compact.Left, back.Left);
        Assert.Equal(compact.Top, back.Top);
    }

    [Fact]
    public void CompactAnchor_IsUsedByBothModes()
    {
        var s = new UserSettings();
        WindowPlacement.Remember(s, 900, 600);
        Assert.Equal((900.0, 600.0), WindowPlacement.Saved(s));
        Assert.Equal((900.0, 600.0), WindowPlacement.Place(s, compact: true, W, CompactH, One));
        // Expanded is taller: same left, top pulled up so the window stays on screen.
        Assert.Equal((900.0, Primary.Bottom - ExpandedH), WindowPlacement.Place(s, compact: false, W, ExpandedH, One));
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


    [Fact]
    public void DialogHangingOffBottomRight_IsPulledInsideOwnerMonitor()
    {
        var owner = new ScreenRect(1184, 740, 352, 76);
        const double width = 480, height = 700;
        var left = owner.Left + (owner.Width - width) / 2;
        var top = owner.Top + (owner.Height - height) / 2;
        var fit = WindowPlacement.FitDialog(left, top, width, height, One, owner);
        Assert.True(Primary.Contains(new ScreenRect(fit.Left, fit.Top, width, height)), $"{fit}");
        Assert.Equal(Primary.Width - 16, fit.MaxWidth);
        Assert.Equal(Primary.Height - 16, fit.MaxHeight);
    }

    [Fact]
    public void DialogTallerThanScreen_ReportsCapAndStaysInside()
    {
        var fit = WindowPlacement.FitDialog(100, 100, 480, 2000, One);
        Assert.Equal(Primary.Height - 16, fit.MaxHeight);
        var height = Math.Min(2000, fit.MaxHeight);
        Assert.True(Primary.Contains(new ScreenRect(fit.Left, fit.Top, 480, height)), $"{fit}");
    }

    [Fact]
    public void DialogOnSecondMonitor_DoesNotJumpToPrimary()
    {
        var second = new ScreenRect(1536, 0, 1280, 720);
        ScreenRect[] two = [Primary, second];
        var owner = new ScreenRect(2100, 500, 352, 76);
        const double width = 480, height = 500;
        var left = owner.Left + (owner.Width - width) / 2;
        var top = owner.Top + (owner.Height - height) / 2;
        var fit = WindowPlacement.FitDialog(left, top, width, height, two, owner);
        Assert.True(second.Contains(new ScreenRect(fit.Left, fit.Top, width, Math.Min(height, fit.MaxHeight))), $"{fit}");
    }

    [Fact]
    public void DialogAlreadyVisible_StaysPut()
    {
        var fit = WindowPlacement.FitDialog(40, 50, 480, 400, One);
        Assert.Equal(40, fit.Left);
        Assert.Equal(50, fit.Top);
    }


    [Fact]
    public void CenterOnScreen_UsesMiddleOfTheMonitor_NotTheEdge()
    {
        var fit = WindowPlacement.CenterOnScreen(480, 700, One, new ScreenRect(1400, 780, 1, 1));
        Assert.Equal((Primary.Width - 480) / 2, fit.Left);
        Assert.Equal((Primary.Height - 700) / 2, fit.Top);
    }

    [Fact]
    public void CenterOnScreen_ShrinksATooTallDialogAndKeepsItCentered()
    {
        var fit = WindowPlacement.CenterOnScreen(480, 2000, One);
        var height = Math.Min(2000, fit.MaxHeight);
        Assert.Equal(Primary.Height - 16, fit.MaxHeight);
        Assert.Equal(Primary.Left + (Primary.Width - 480) / 2, fit.Left);
        Assert.Equal(Primary.Top + (Primary.Height - height) / 2, fit.Top);
    }

    [Fact]
    public void CenterOnScreen_FollowsTheSecondMonitor()
    {
        var second = new ScreenRect(1536, 0, 1280, 720);
        ScreenRect[] two = [Primary, second];
        var fit = WindowPlacement.CenterOnScreen(480, 400, two, new ScreenRect(2000, 100, 1, 1));
        Assert.Equal(second.Left + (second.Width - 480) / 2, fit.Left);
        Assert.Equal(second.Top + (second.Height - 400) / 2, fit.Top);
    }
}
