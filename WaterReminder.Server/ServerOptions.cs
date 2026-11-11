namespace WaterReminder.Server;

public sealed class ServerOptions
{
    public const int DefaultPort = 8787;

    public required string DataDirectory { get; init; }
    public string ApiToken { get; init; } = "";
    public int Port { get; init; } = DefaultPort;
    public string BindAddress { get; init; } = "0.0.0.0";
    public bool RunScheduler { get; init; } = true;

    public string ListenUrl => $"http://{BindAddress}:{Port}";

    public static ServerOptions FromEnvironment(bool requireApiToken)
    {
        var data = Environment.GetEnvironmentVariable("WATERREMINDER_DATA_DIR");
        if (string.IsNullOrWhiteSpace(data))
            data = Path.Combine(AppContext.BaseDirectory, "data");

        var port = DefaultPort;
        var portText = Environment.GetEnvironmentVariable("WATERREMINDER_PORT");
        if (int.TryParse(portText, out var parsed) && parsed is > 0 and < 65536)
            port = parsed;

        var bind = Environment.GetEnvironmentVariable("WATERREMINDER_BIND");
        if (string.IsNullOrWhiteSpace(bind)) bind = "0.0.0.0";

        return Create(data, SecretFiles.ApiToken(data), port, bind.Trim(), requireApiToken);
    }

    public static ServerOptions Create(string dataDirectory, string? apiToken, int port, string bindAddress, bool requireApiToken, bool runScheduler = true)
    {
        if (requireApiToken && string.IsNullOrWhiteSpace(apiToken))
        {
            throw new InvalidOperationException(
                "Missing API token. Set WATERREMINDER_API_TOKEN or create api.token in the server data directory.");
        }

        return new ServerOptions
        {
            DataDirectory = dataDirectory,
            ApiToken = apiToken?.Trim() ?? "",
            Port = port,
            BindAddress = string.IsNullOrWhiteSpace(bindAddress) ? "0.0.0.0" : bindAddress.Trim(),
            RunScheduler = runScheduler,
        };
    }
}
