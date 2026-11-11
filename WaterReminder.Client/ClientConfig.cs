using System.Text.Json;

namespace WaterReminder.Client;

/// <summary>
/// Local client configuration. Only the server URL and the API token belong here.
/// The phone push token stays on the server.
/// </summary>
internal sealed class ClientConfig
{
    public string ServerUrl { get; set; } = "";
    public string ApiToken { get; set; } = "";

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
        var config = JsonSerializer.Deserialize<ClientConfig>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("配置文件是空的。");

        if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.ApiToken))
            throw new InvalidOperationException("请在 client.json 里填写 serverUrl 和 apiToken。");

        if (!Uri.TryCreate(config.ServerUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("serverUrl 必须是 http 或 https 地址。");

        config.ServerUrl = config.ServerUrl.Trim().TrimEnd('/');
        config.ApiToken = config.ApiToken.Trim();
        return config;
    }
}
