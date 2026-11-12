using System.IO;
using System.Text.Json;
using WaterReminder.Services;

namespace WaterReminder.Client;

/// <summary>
/// Local client configuration. Only the server URL and the API token belong here.
/// The phone push token stays on the server.
/// </summary>
internal sealed class ClientConfig
{
    public string ServerUrl { get; set; } = "";
    public string ApiToken { get; set; } = "";
    public bool FloatingWindow { get; set; } = true;
    public bool CompactMode { get; set; }
    public double? CompactLeft { get; set; }
    public double? CompactTop { get; set; }
    public double? ExpandedLeft { get; set; }
    public double? ExpandedTop { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string SourcePath { get; set; } = "";

    public static string DefaultPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WaterReminderClient",
            "client.json");

    public static ClientConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException("找不到配置文件。请复制 client.sample.json 为 client.json，并填写 serverUrl 和 apiToken。");

        var json = File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize<ClientConfig>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (loaded is null)
            throw new InvalidOperationException("配置文件是空的。");
        var config = loaded;

        if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.ApiToken))
            throw new InvalidOperationException("请在 client.json 里填写 serverUrl 和 apiToken。");

        if (!Uri.TryCreate(config.ServerUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("serverUrl 必须是 http 或 https 地址。");

        config.ServerUrl = config.ServerUrl.Trim().TrimEnd('/');
        config.ApiToken = config.ApiToken.Trim();
        var display = ClientDisplayStore.Read(json);
        config.FloatingWindow = display.FloatingWindow;
        config.CompactMode = display.CompactMode;
        config.CompactLeft = display.CompactLeft;
        config.CompactTop = display.CompactTop;
        config.ExpandedLeft = display.ExpandedLeft;
        config.ExpandedTop = display.ExpandedTop;
        config.WindowLeft = display.WindowLeft;
        config.WindowTop = display.WindowTop;
        config.SourcePath = path;
        return config;
    }

    public ClientDisplayPreferences Display() => new()
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

    public void ApplyDisplay(ClientDisplayPreferences display)
    {
        FloatingWindow = display.FloatingWindow;
        CompactMode = display.CompactMode;
        CompactLeft = display.CompactLeft;
        CompactTop = display.CompactTop;
        ExpandedLeft = display.ExpandedLeft;
        ExpandedTop = display.ExpandedTop;
        WindowLeft = display.WindowLeft;
        WindowTop = display.WindowTop;
    }

    public void Save()
    {
        if (string.IsNullOrWhiteSpace(SourcePath))
            throw new InvalidOperationException("没有配置文件路径。");
        var current = File.Exists(SourcePath) ? File.ReadAllText(SourcePath) : "{}";
        File.WriteAllText(SourcePath, ClientDisplayStore.Apply(current, Display()));
    }
}
