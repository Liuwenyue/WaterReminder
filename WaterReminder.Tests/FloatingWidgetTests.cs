using WaterReminder.Services;
using Xunit;

namespace WaterReminder.Tests;

public sealed class FloatingWidgetTests
{
    [Fact]
    public void MissingFloatingWindow_DefaultsToShown_AndSaveKeepsTheApiToken()
    {
        const string original = """{"serverUrl":"http://vm:8787","apiToken":"desktop-api-token"}""";
        var prefs = ClientDisplayStore.Read(original);
        Assert.True(prefs.FloatingWindow);
        Assert.False(prefs.CompactMode);

        prefs.FloatingWindow = false;
        prefs.CompactMode = true;
        prefs.CompactLeft = 40;
        prefs.CompactTop = 80;
        var saved = ClientDisplayStore.Apply(original, prefs);
        Assert.Contains("desktop-api-token", saved);
        Assert.Contains("http://vm:8787", saved);
        Assert.DoesNotContain("hlk_", saved);

        var again = ClientDisplayStore.Read(saved);
        Assert.False(again.FloatingWindow);
        Assert.True(again.CompactMode);
        Assert.Equal(40, again.CompactLeft);
        Assert.Equal(80, again.CompactTop);
    }

    [Fact]
    public void RememberedPosition_StaysWithTheCurrentMode()
    {
        var prefs = new ClientDisplayPreferences { FloatingWindow = true };
        var settings = prefs.ToSettings();
        WindowPlacement.Remember(settings, compact: false, 120, 240);
        prefs.Capture(settings);
        Assert.Equal(120, prefs.ExpandedLeft);
        Assert.Equal(240, prefs.ExpandedTop);
        Assert.Null(prefs.CompactLeft);

        var restored = ClientDisplayStore.Read(ClientDisplayStore.Apply("{}", prefs)).ToSettings();
        var placed = WindowPlacement.Resolve(restored, compact: false, 100, 50, []);
        Assert.Equal((120d, 240d), placed);
    }

    [Fact]
    public void Widget_UsesServerProgress_AndTheCupForTheClock()
    {
        var status = new StatusResponse
        {
            Now = "2026-11-03T10:00:00",
            WorkStart = "08:30",
            WorkEnd = "17:30",
            WorkCupMl = 370,
            HomeCupMl = 800,
            ConsumedMl = 370,
            GoalMl = 3000,
        };
        Assert.Equal(370, FloatingWidgetView.CurrentCupMl(status));
        Assert.Equal(12, FloatingWidgetView.Percent(status.ConsumedMl, status.GoalMl));
        Assert.True(FloatingWidgetView.CanDrink(status.ConsumedMl, status.GoalMl));

        status.Now = "2026-11-03T19:00:00";
        Assert.Equal(800, FloatingWidgetView.CurrentCupMl(status));
        Assert.False(FloatingWidgetView.CanDrink(3000, 3000));
    }
}
