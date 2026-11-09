using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WaterReminder.Services;

/// <summary>One ntfy publish request: URL, HTTP headers and plain-text body. No network here.</summary>
public sealed record NtfyMessage(Uri Url, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public bool HasAction => Headers.ContainsKey("Actions");
}

/// <summary>Hard-to-guess ntfy topics (16 random bytes, hex encoded).</summary>
public static class NtfyTopics
{
    public const string MainPrefix = "wr-";
    public const string ActionPrefix = "wr-act-";

    public static string Generate(string prefix = MainPrefix)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return prefix + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool IsValid(string? topic)
        => !string.IsNullOrEmpty(topic) && topic.Length <= 64
           && topic.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>
/// Builds ntfy requests without touching the network, so URLs/headers/body are unit-testable.
/// Topic and token come from settings only; nothing is hardcoded.
/// </summary>
public static class NtfyMessageBuilder
{
    public static string NormalizeServer(string? server)
    {
        var value = string.IsNullOrWhiteSpace(server) ? "https://ntfy.sh" : server.Trim();
        return value.TrimEnd('/');
    }

    public static Uri TopicUrl(string server, string topic) => new($"{NormalizeServer(server)}/{topic}");

    public static Uri StreamUrl(UserSettings s) => new($"{TopicUrl(s.NtfyServer, s.NtfyActionTopic)}/json");

    public static NtfyMessage BuildReminder(UserSettings s, int suggestedMl)
        => BuildReminder(s.NtfyServer, s.NtfyTopic, s.NtfyActionTopic, s.NtfyToken, suggestedMl);

    public static NtfyMessage BuildReminder(string server, string topic, string actionTopic, string? token, int suggestedMl)
    {
        var normalized = NormalizeServer(server);
        var headers = BaseHeaders(token);
        headers["Title"] = "该喝水了";
        headers["Tags"] = "droplet";
        headers["Actions"] = $"http, 喝水了, {normalized}/{actionTopic}, method=POST, body=DRINK, clear=true";
        return new NtfyMessage(TopicUrl(normalized, topic), headers, $"建议现在补充 {suggestedMl} ml");
    }

    /// <summary>Confirmation has no action button, so tapping/recording can never loop.</summary>
    public static NtfyMessage BuildConfirmation(UserSettings s, int appliedMl)
        => BuildConfirmation(s.NtfyServer, s.NtfyTopic, s.NtfyToken, appliedMl);

    public static NtfyMessage BuildConfirmation(string server, string topic, string? token, int appliedMl)
    {
        var headers = BaseHeaders(token);
        headers["Title"] = $"已记录 {appliedMl} ml";
        headers["Tags"] = "white_check_mark";
        return new NtfyMessage(TopicUrl(server, topic), headers, $"已记录 {appliedMl} ml");
    }

    /// <summary>Settings test: same button as a real reminder so the iPhone subscription can be verified.</summary>
    public static NtfyMessage BuildTest(UserSettings s)
        => BuildTest(s.NtfyServer, s.NtfyTopic, s.NtfyActionTopic, s.NtfyToken);

    public static NtfyMessage BuildTest(string server, string topic, string actionTopic, string? token)
    {
        var normalized = NormalizeServer(server);
        var headers = BaseHeaders(token);
        headers["Title"] = "喝水提醒测试";
        headers["Tags"] = "droplet";
        headers["Actions"] = $"http, 喝水了, {normalized}/{actionTopic}, method=POST, body=DRINK, clear=true";
        return new NtfyMessage(TopicUrl(normalized, topic), headers, "这是一条测试通知，点击「喝水了」可记录一杯。");
    }

    private static Dictionary<string, string> BaseHeaders(string? token)
    {
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(token)) headers["Authorization"] = $"Bearer {token.Trim()}";
        return headers;
    }
}

/// <summary>
/// Publishes reminders and listens on the action topic (ntfy JSON stream) for DRINK callbacks.
/// All failures are swallowed: an unreachable ntfy server must never crash or block the app.
/// The token is only sent as an Authorization header and is never logged.
/// </summary>
public sealed class NtfyService : IDisposable
{
    private readonly UserSettings _settings;
    private readonly Func<int> _recordDrink;
    private readonly Action<Action> _dispatch;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly object _gate = new();
    private readonly HashSet<string> _seenIds = [];
    private CancellationTokenSource? _cts;

    public NtfyService(UserSettings settings, Func<int> recordDrink, Action<Action>? dispatch = null, HttpClient? http = null)
    {
        _settings = settings;
        _recordDrink = recordDrink;
        _dispatch = dispatch ?? (action => action());
        _ownsHttp = http is null;
        // ntfy header values (Title / Actions label) are UTF-8; .NET otherwise restricts headers to Latin-1.
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            RequestHeaderEncodingSelector = (_, _) => Encoding.UTF8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    public bool IsRunning
    {
        get { lock (_gate) return _cts is not null; }
    }

    /// <summary>Starts/restarts the SSE listener when phone push is enabled and a topic exists.</summary>
    public void Start()
    {
        Stop();
        if (!_settings.MobilePushEnabled) return;
        if (!NtfyTopics.IsValid(_settings.NtfyTopic) || !NtfyTopics.IsValid(_settings.NtfyActionTopic)) return;
        if (!Uri.TryCreate(NtfyMessageBuilder.NormalizeServer(_settings.NtfyServer), UriKind.Absolute, out var server)
            || (server.Scheme != Uri.UriSchemeHttp && server.Scheme != Uri.UriSchemeHttps)) return;

        var cts = new CancellationTokenSource();
        lock (_gate) _cts = cts;
        _ = Task.Run(() => ListenLoopAsync(cts.Token));
    }

    public void Restart() => Start();

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
        }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        cts?.Dispose();
    }

    public async Task<bool> PublishReminderAsync(int suggestedMl, CancellationToken ct = default)
    {
        try { return await PublishAsync(NtfyMessageBuilder.BuildReminder(_settings, suggestedMl), ct).ConfigureAwait(false); }
        catch { return false; } // malformed saved server/topic: never throw into the reminder timer
    }

    public async Task<bool> PublishConfirmationAsync(int appliedMl, CancellationToken ct = default)
    {
        try { return await PublishAsync(NtfyMessageBuilder.BuildConfirmation(_settings, appliedMl), ct).ConfigureAwait(false); }
        catch { return false; }
    }

    public async Task<bool> SendTestAsync(CancellationToken ct = default)
    {
        try { return await PublishAsync(NtfyMessageBuilder.BuildTest(_settings), ct).ConfigureAwait(false); }
        catch { return false; }
    }

    public async Task<bool> PublishAsync(NtfyMessage message, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, message.Url)
            {
                Content = new StringContent(message.Body, Encoding.UTF8),
            };
            foreach (var (name, value) in message.Headers) request.Headers.TryAddWithoutValidation(name, value);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false; // offline / bad server / timeout: stay silent and try again next tick
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, NtfyMessageBuilder.StreamUrl(_settings));
                if (!string.IsNullOrWhiteSpace(_settings.NtfyToken))
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_settings.NtfyToken.Trim()}");

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                delay = TimeSpan.FromSeconds(2); // healthy connection: reset backoff

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null) break; // server closed the stream, reconnect below
                    HandleLine(line);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // unreachable server / DNS / TLS / stream error: back off and retry
            }

            if (ct.IsCancellationRequested) return;
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }
    }

    private void HandleLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("event", out var evt) || evt.GetString() != "message") return;
            if (!root.TryGetProperty("message", out var msgElement)) return;
            if (!string.Equals(msgElement.GetString()?.Trim(), "DRINK", StringComparison.OrdinalIgnoreCase)) return;

            // Deduplicate reconnects/replays by ntfy message id.
            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            if (!string.IsNullOrEmpty(id))
            {
                lock (_gate)
                {
                    if (!_seenIds.Add(id)) return;
                    if (_seenIds.Count > 1000) _seenIds.Clear();
                }
            }

            try
            {
                _dispatch(() =>
                {
                    var applied = _recordDrink(); // home cup after work; runs on the UI thread
                    if (applied > 0) _ = PublishConfirmationAsync(applied);
                });
            }
            catch
            {
                // dispatcher shutting down / windowless: ignore
            }
        }
        catch
        {
            // keepalive/open payloads and anything unexpected are ignored
        }
    }

    public void Dispose()
    {
        Stop();
        if (_ownsHttp) _http.Dispose();
    }
}
