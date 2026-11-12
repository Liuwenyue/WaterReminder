using System.Globalization;
using System.Text.Json.Nodes;

namespace WaterReminder.Services;

/// <summary>
/// Desktop-only display state for the floating widget. These fields already exist on
/// <see cref="UserSettings"/>; they are not reminder rules, so the tray client keeps them
/// in client.json instead of sending them to the server.
/// </summary>
public sealed class ClientDisplayPreferences
{
    public bool FloatingWindow { get; set; } = true;
    public bool CompactMode { get; set; }
    public double? CompactLeft { get; set; }
    public double? CompactTop { get; set; }
    public double? ExpandedLeft { get; set; }
    public double? ExpandedTop { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    public UserSettings ToSettings()
    {
        var settings = new UserSettings
        {
            FloatingWindow = FloatingWindow,
            CompactMode = CompactMode,
            CompactLeft = CompactLeft,
            CompactTop = CompactTop,
            ExpandedLeft = ExpandedLeft,
            ExpandedTop = ExpandedTop,
            WindowLeft = WindowLeft,
            WindowTop = WindowTop,
        };
        WindowPlacement.Migrate(settings);
        return settings;
    }

    public void Capture(UserSettings settings)
    {
        FloatingWindow = settings.FloatingWindow;
        CompactMode = settings.CompactMode;
        CompactLeft = settings.CompactLeft;
        CompactTop = settings.CompactTop;
        ExpandedLeft = settings.ExpandedLeft;
        ExpandedTop = settings.ExpandedTop;
        WindowLeft = settings.WindowLeft;
        WindowTop = settings.WindowTop;
    }
}

public static class ClientDisplayStore
{
    public static ClientDisplayPreferences Read(string? json)
    {
        var prefs = new ClientDisplayPreferences();
        if (string.IsNullOrWhiteSpace(json)) return prefs;
        var root = JsonNode.Parse(json) as JsonObject;
        if (root is null) return prefs;
        if (root["floatingWindow"] is JsonValue shown && shown.TryGetValue<bool>(out var floating))
            prefs.FloatingWindow = floating;
        if (root["compactMode"] is JsonValue compact && compact.TryGetValue<bool>(out var mode))
            prefs.CompactMode = mode;
        prefs.CompactLeft = ReadNumber(root, "compactLeft");
        prefs.CompactTop = ReadNumber(root, "compactTop");
        prefs.ExpandedLeft = ReadNumber(root, "expandedLeft");
        prefs.ExpandedTop = ReadNumber(root, "expandedTop");
        prefs.WindowLeft = ReadNumber(root, "windowLeft");
        prefs.WindowTop = ReadNumber(root, "windowTop");
        return prefs;
    }

    /// <summary>Writes display fields and leaves every other property, including the API token, unchanged.</summary>
    public static string Apply(string? json, ClientDisplayPreferences prefs)
    {
        var root = string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        root["floatingWindow"] = prefs.FloatingWindow;
        root["compactMode"] = prefs.CompactMode;
        SetNumber(root, "compactLeft", prefs.CompactLeft);
        SetNumber(root, "compactTop", prefs.CompactTop);
        SetNumber(root, "expandedLeft", prefs.ExpandedLeft);
        SetNumber(root, "expandedTop", prefs.ExpandedTop);
        SetNumber(root, "windowLeft", prefs.WindowLeft);
        SetNumber(root, "windowTop", prefs.WindowTop);
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static double? ReadNumber(JsonObject root, string name)
    {
        if (root[name] is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var number)) return number;
        if (value.TryGetValue<int>(out var whole)) return whole;
        return null;
    }

    private static void SetNumber(JsonObject root, string name, double? value)
    {
        if (value is null) root.Remove(name);
        else root[name] = value.Value;
    }
}

/// <summary>Numbers the floating widget shows. It does not schedule reminders.</summary>
public static class FloatingWidgetView
{
    public static int Percent(int consumedMl, int goalMl)
    {
        if (goalMl <= 0) return 0;
        var ratio = consumedMl / (double)goalMl;
        return (int)Math.Floor(Math.Min(100, Math.Max(0, ratio) * 100));
    }

    public static bool CanDrink(int consumedMl, int goalMl) => goalMl <= 0 || consumedMl < goalMl;

    /// <summary>Same cup rule as the old window: work cup between work start and work end, otherwise the home cup.</summary>
    public static int CurrentCupMl(StatusResponse status)
    {
        if (!DateTime.TryParse(status.Now, CultureInfo.InvariantCulture, DateTimeStyles.None, out var now))
            return status.WorkCupMl;
        if (!SettingsEditor.TryParseClock(status.WorkStart, out var start) || !SettingsEditor.TryParseClock(status.WorkEnd, out var end))
            return status.WorkCupMl;
        var time = now.TimeOfDay;
        return time >= start && time < end ? status.WorkCupMl : status.HomeCupMl;
    }
}
