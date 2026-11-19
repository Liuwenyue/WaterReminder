using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WaterReminder.Services;

public sealed class WeChatConfig
{
    public const string DefaultAppId = "wx33d9effed15ff69a";

    public string AppId { get; init; } = "";
    public string AppSecret { get; init; } = "";
    public string TemplateId { get; init; } = "";
    public string MiniProgramState { get; init; } = "developer";
    public string Page { get; init; } = "pages/today/today";
    public IReadOnlyDictionary<string, string> Fields { get; init; } = DefaultFields;

    public bool CanIdentify => AppId.Length > 0 && AppSecret.Length > 0;

    public bool IsConfigured => CanIdentify && TemplateId.Length > 0;

    public static IReadOnlyDictionary<string, string> DefaultFields { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["thing1"] = "该喝水了",
            ["time2"] = "{time}",
            ["thing3"] = "建议喝{suggested}毫升",
        };
}

public readonly record struct WeChatReminder(string OpenId, int SuggestedMl, int ConsumedMl, int GoalMl, DateTime At);

public readonly record struct WeChatSendResult(bool Sent, bool QuotaExhausted);

/// <summary>
/// WeChat mini program login and one-time subscribe messages. The app secret stays in this process.
/// </summary>
public sealed class WeChatClient : IDisposable
{
    private static readonly Regex FieldKey = new(
        "^(thing|number|letter|symbol|character_string|time|date|amount|phone_number|car_number|name|phrase|const)[0-9]{1,2}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex OpenIdPattern = new("^[A-Za-z0-9_-]{6,64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly WeChatConfig _config;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private DateTime _tokenUntilUtc;

    public static WeChatClient Disabled { get; } = new(new WeChatConfig());

    public WeChatClient(WeChatConfig config, HttpMessageHandler? handler = null, bool disposeHandler = false)
    {
        _config = config;
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(12) }
            : new HttpClient(handler, disposeHandler) { Timeout = TimeSpan.FromSeconds(12) };
    }

    public bool IsConfigured => _config.IsConfigured;
    public bool CanIdentify => _config.CanIdentify;
    public string TemplateId => _config.IsConfigured ? _config.TemplateId : "";

    public static WeChatClient FromDirectory(string dataDirectory)
    {
        try
        {
            return new WeChatClient(LoadConfig(dataDirectory));
        }
        catch (IOException)
        {
            return new WeChatClient(new WeChatConfig());
        }
    }

    public static WeChatConfig LoadConfig(string dataDirectory)
    {
        JsonElement file = default;
        var hasFile = false;
        try
        {
            var path = Path.Combine(dataDirectory, "wechat.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    file = doc.RootElement.Clone();
                    hasFile = true;
                }
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            hasFile = false;
        }

        var state = First(Environment.GetEnvironmentVariable("WATERREMINDER_WECHAT_STATE"), Text(file, hasFile, "miniprogramState"), "developer");
        if (state is not ("developer" or "trial" or "formal")) state = "developer";
        var page = Text(file, hasFile, "page");
        if (string.IsNullOrWhiteSpace(page) || page.Length > 80 || !page.StartsWith("pages/", StringComparison.Ordinal) || page.Contains('?', StringComparison.Ordinal))
            page = "pages/today/today";

        return new WeChatConfig
        {
            AppId = First(Environment.GetEnvironmentVariable("WATERREMINDER_WECHAT_APP_ID"), Text(file, hasFile, "appId"), WeChatConfig.DefaultAppId) ?? "",
            AppSecret = First(Environment.GetEnvironmentVariable("WATERREMINDER_WECHAT_APP_SECRET"), Text(file, hasFile, "appSecret")) ?? "",
            TemplateId = First(Environment.GetEnvironmentVariable("WATERREMINDER_WECHAT_TEMPLATE_ID"), Text(file, hasFile, "templateId")) ?? "",
            MiniProgramState = state,
            Page = page,
            Fields = Fields(file, hasFile),
        };
    }

    public async Task<string?> ExchangeOpenIdAsync(string code, CancellationToken cancellationToken)
    {
        if (!CanIdentify || string.IsNullOrWhiteSpace(code)) return null;
        var url = "https://api.weixin.qq.com/sns/jscode2session?appid="
            + Uri.EscapeDataString(_config.AppId)
            + "&secret=" + Uri.EscapeDataString(_config.AppSecret)
            + "&js_code=" + Uri.EscapeDataString(code.Trim())
            + "&grant_type=authorization_code";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("errcode", out var error) && error.ValueKind == JsonValueKind.Number && error.GetInt32() != 0)
                return null;
            if (!doc.RootElement.TryGetProperty("openid", out var openId) || openId.ValueKind != JsonValueKind.String)
                return null;
            var value = openId.GetString() ?? "";
            return OpenIdPattern.IsMatch(value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<WeChatSendResult> SendAsync(WeChatReminder reminder, CancellationToken cancellationToken)
    {
        if (!IsConfigured || !OpenIdPattern.IsMatch(reminder.OpenId)) return new(false, false);
        var payload = BuildPayload(_config, reminder);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await AccessTokenAsync(force: attempt > 0, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token)) return new(false, false);
            using var body = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(
                "https://api.weixin.qq.com/cgi-bin/message/subscribe/send?access_token=" + Uri.EscapeDataString(token),
                body, cancellationToken).ConfigureAwait(false);
            var json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            if (!TryErrorCode(json, out var code)) return new(false, false);
            if (code == 0) return new(true, false);
            if (code is 40001 or 42001) continue;
            return new(false, code == 43101);
        }
        return new(false, false);
    }

    public static string BuildPayload(WeChatConfig config, WeChatReminder reminder)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("touser", reminder.OpenId);
            writer.WriteString("template_id", config.TemplateId);
            writer.WriteString("page", config.Page);
            writer.WriteString("miniprogram_state", config.MiniProgramState);
            writer.WriteString("lang", "zh_CN");
            writer.WritePropertyName("data");
            writer.WriteStartObject();
            foreach (var field in config.Fields)
            {
                writer.WritePropertyName(field.Key);
                writer.WriteStartObject();
                writer.WriteString("value", Fit(field.Key, Fill(field.Value, reminder)));
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public void Dispose()
    {
        if (!ReferenceEquals(this, Disabled)) _http.Dispose();
        _tokenGate.Dispose();
    }

    private async Task<string?> AccessTokenAsync(bool force, CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && _token is not null && DateTime.UtcNow < _tokenUntilUtc) return _token;
            var url = "https://api.weixin.qq.com/cgi-bin/token?grant_type=client_credential&appid="
                + Uri.EscapeDataString(_config.AppId)
                + "&secret=" + Uri.EscapeDataString(_config.AppSecret);
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("access_token", out var token) || token.ValueKind != JsonValueKind.String)
                return null;
            var expires = doc.RootElement.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var seconds) ? seconds : 7200;
            _token = token.GetString();
            _tokenUntilUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expires - 120));
            return _token;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return text.Length <= 8000 ? text : text[..8000];
    }

    private static bool TryErrorCode(string json, out int code)
    {
        code = -1;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("errcode", out var error) || !error.TryGetInt32(out code)) return false;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Fill(string template, WeChatReminder reminder)
    {
        var time = reminder.At.ToString("yyyy年M月d日 HH:mm", CultureInfo.InvariantCulture);
        return (template ?? "")
            .Replace("{time}", time, StringComparison.Ordinal)
            .Replace("{suggested}", reminder.SuggestedMl.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{consumed}", reminder.ConsumedMl.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{goal}", reminder.GoalMl.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static string Fit(string key, string value)
    {
        var limit = key.StartsWith("thing", StringComparison.Ordinal) ? 20
            : key.StartsWith("phrase", StringComparison.Ordinal) ? 5
            : key.StartsWith("name", StringComparison.Ordinal) ? 10
            : key.StartsWith("character_string", StringComparison.Ordinal) ? 32
            : 32;
        if (key.StartsWith("time", StringComparison.Ordinal) || key.StartsWith("date", StringComparison.Ordinal) || key.StartsWith("number", StringComparison.Ordinal))
            return value;
        return value.Length <= limit ? value : value[..limit];
    }

    private static string? First(params string?[] values)
    {
        foreach (var value in values)
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrEmpty(trimmed)) return trimmed.Length > 128 ? trimmed[..128] : trimmed;
        }
        return null;
    }

    private static string? Text(JsonElement file, bool hasFile, string name)
    {
        if (!hasFile || !file.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        return value.GetString();
    }

    private static IReadOnlyDictionary<string, string> Fields(JsonElement file, bool hasFile)
    {
        if (!hasFile || !file.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
            return WeChatConfig.DefaultFields;
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields.EnumerateObject())
        {
            if (parsed.Count == 5) break;
            if (!FieldKey.IsMatch(field.Name) || field.Value.ValueKind != JsonValueKind.String) continue;
            var value = field.Value.GetString()?.Trim() ?? "";
            if (value.Length == 0 || value.Length > 40) continue;
            parsed[field.Name] = value;
        }
        return parsed.Count == 0 ? WeChatConfig.DefaultFields : parsed;
    }
}

internal sealed class WeChatUser
{
    public string OpenId { get; set; } = "";
    public int Credits { get; set; }
    public List<string> Seen { get; set; } = [];
}

internal static class WeChatUsers
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static WeChatUser Load(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "wechat-user.json");
            if (!File.Exists(path)) return new WeChatUser();
            var user = JsonSerializer.Deserialize<WeChatUser>(File.ReadAllText(path), Json) ?? new WeChatUser();
            user.OpenId ??= "";
            user.Seen ??= [];
            if (user.Credits < 0) user.Credits = 0;
            return user;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return new WeChatUser();
        }
    }

    public static void Save(string directory, WeChatUser user)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "wechat-user.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(user, Json));
        File.Move(temp, path, overwrite: true);
    }
}
