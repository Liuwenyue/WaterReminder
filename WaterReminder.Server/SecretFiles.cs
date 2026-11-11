namespace WaterReminder.Server;

/// <summary>
/// Resolves secrets from the environment or the server data directory.
/// Values are never written into the repository.
/// </summary>
public static class SecretFiles
{
    public const string ApiTokenFileName = "api.token";
    public const string PushWardTokenFileName = "pushward.token";

    public static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    public static string? ReadTokenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    public static string? ApiToken(string dataDirectory)
        => FirstNonEmpty(
            Environment.GetEnvironmentVariable("WATERREMINDER_API_TOKEN"),
            ReadTokenFile(Path.Combine(dataDirectory, ApiTokenFileName)));

    public static string? PushWardToken(string dataDirectory)
        => FirstNonEmpty(
            Environment.GetEnvironmentVariable("WATERREMINDER_PUSHWARD_TOKEN"),
            ReadTokenFile(Path.Combine(dataDirectory, PushWardTokenFileName)));
}
