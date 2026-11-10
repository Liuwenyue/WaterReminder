using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaterReminder.Services;

/// <summary>
/// PushWard 通知里的一个动作按钮。没有 url / foreground，点按只会被 PushWard 记录为
/// Notification Answer，由电脑轮询取回；不会打开 App 或浏览器。
/// </summary>
public sealed record PushWardAction(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("icon")] string Icon);

/// <summary>只包含请求体字段；API Token 走 Authorization 头，绝不写进 JSON。</summary>
public sealed record PushWardPayload(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("source_display_name")] string SourceDisplayName,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("collapse_id")] string? CollapseId,
    [property: JsonPropertyName("actions")] IReadOnlyList<PushWardAction>? Actions);

/// <summary>一条 PushWard 消息：令牌用于请求头，Payload 序列化为 JSON 正文。</summary>
public sealed record PushWardMessage(string Token, PushWardPayload Payload)
{
    public bool HasActions => Payload.Actions is { Count: > 0 };

    public string Json => JsonSerializer.Serialize(Payload, PushWardMessageBuilder.JsonOptions);
}

/// <summary>电脑从 Answers 流里取回的一条动作记录。</summary>
public sealed record PushWardAnswerItem(string NotificationId, string ActionId);

/// <summary>一次 Answers 请求的结果；Cursor 为空表示服务端没有返回游标。</summary>
public sealed record PushWardAnswerBatch(string? Cursor, IReadOnlyList<PushWardAnswerItem> Items);

/// <summary>
/// 纯函数消息构造器，不联网，便于单测。令牌全部来自设置。
/// 确认通知不带 actions，因此不可能因点按而循环记账。
/// </summary>
public static class PushWardMessageBuilder
{
    public const string ApiBase = "https://api.pushward.app";
    public const string Endpoint = ApiBase + "/notifications";
    public const string AnswersEndpoint = ApiBase + "/notifications/answers";

    public const string Source = "waterreminder";
    public const string SourceDisplayName = "喝水提醒";
    public const string ReminderLevel = "time-sensitive";
    public const string ConfirmationLevel = "active";
    public const string ReminderCollapseId = "water-evening";
    public const string TestCollapseId = "water-test";
    public const string ReminderTitle = "该喝水了";
    public const string TestTitle = "喝水提醒测试";

    public const string DrinkActionId = "drink";
    public const string DrinkActionTitle = "喝水了";
    public const string DrinkActionIcon = "drop.fill";

    /// <summary>手机点按记账时写入记录页的来源标记。</summary>
    public const string RecordSource = "pushward";
    /// <summary>设置页/状态栏里展示的手机通道名称。</summary>
    public const string ChannelLabel = "手机 Pushward";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        // 直接输出 UTF-8 中文，而不是 \uXXXX；请求体只发给 PushWard API，不嵌入 HTML。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>「喝水了」按钮：只声明 id/title/icon，点按由 PushWard 记为 Answer。</summary>
    public static PushWardAction BuildDrinkAction()
        => new(DrinkActionId, DrinkActionTitle, DrinkActionIcon);

    /// <summary>晚间/测试推送正文：已喝/目标 + 建议毫升，一句话。</summary>
    public static string FormatReminderBody(int consumedMl, int goalMl, int suggestedMl)
        => $"已喝 {consumedMl}/{goalMl} ml，建议 {suggestedMl} ml";

    /// <summary>晚间提醒：标题「该喝水了」，正文「已喝 已喝量/目标 ml，建议 N ml」。</summary>
    public static PushWardMessage BuildReminder(UserSettings s, int suggestedMl, int consumedMl)
        => BuildReminder(s.PushWardApiToken, suggestedMl, consumedMl, s.DailyGoalMl);

    public static PushWardMessage BuildReminder(string? token, int suggestedMl, int consumedMl, int goalMl)
        => new(token ?? "", new PushWardPayload(
            ReminderTitle,
            FormatReminderBody(consumedMl, goalMl, suggestedMl),
            Source,
            SourceDisplayName,
            ReminderLevel,
            ReminderCollapseId,
            [BuildDrinkAction()]));

    /// <summary>记账后的确认：标题「已记录」，正文「已记 N ml」，没有 actions。</summary>
    public static PushWardMessage BuildConfirmation(UserSettings s, int appliedMl)
        => BuildConfirmation(s.PushWardApiToken, appliedMl);

    public static PushWardMessage BuildConfirmation(string? token, int appliedMl)
        => new(token ?? "", new PushWardPayload(
            "已记录",
            $"已记 {appliedMl} ml",
            Source,
            SourceDisplayName,
            ConfirmationLevel,
            CollapseId: null,
            Actions: null));

    /// <summary>设置页测试：文案与真实提醒相同，仅 collapse_id 用 water-test，避免盖住晚间提醒。</summary>
    public static PushWardMessage BuildTest(UserSettings s, int consumedMl)
        => new(s.PushWardApiToken ?? "", new PushWardPayload(
            ReminderTitle,
            FormatReminderBody(consumedMl, s.DailyGoalMl, WaterCalculator.HomeCupMl(s)),
            Source,
            SourceDisplayName,
            ReminderLevel,
            TestCollapseId,
            [BuildDrinkAction()]));
}

/// <summary>
/// 发送 PushWard 通知并轮询 Notification Answers。注入 <see cref="HttpClient"/> 让测试完全离线；
/// 所有失败都被吞掉，网络问题不能打断提醒计时。令牌只放进 Authorization 头，不写日志。
/// </summary>
public sealed class PushWardService : IDisposable
{
    /// <summary>Answers 长轮询的等待秒数，与 API 的 wait 参数一致。</summary>
    public const int AnswersWaitSeconds = 25;

    private static readonly TimeSpan PollRequestTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly UserSettings _settings;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<int>? _recordDrink;
    private readonly Action<Action> _dispatch;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private readonly SeenIdCache _seenIds = new(1000);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _loop;
    private bool _disposed;

    /// <param name="recordDrink">
    /// 记录一杯家庭杯并返回实际记下的 ml。仅在需要接收手机点按时传入；纯发送场景可省略。
    /// </param>
    /// <param name="dispatch">把记账回调切回 UI 线程。</param>
    /// <param name="delay">测试注入点；生产使用 <see cref="Task.Delay"/>。</param>
    public PushWardService(UserSettings settings, HttpClient? http = null,
        Func<int>? recordDrink = null, Action<Action>? dispatch = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _settings = settings;
        _recordDrink = recordDrink;
        _dispatch = dispatch ?? (action => action());
        _delay = delay ?? ((duration, ct) => Task.Delay(duration, ct));
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>PushWard 只需要 API Token；空令牌表示「不推送」，绝不用占位值请求。</summary>
    public bool IsConfigured => IsConfiguredFor(_settings);

    public static bool IsConfiguredFor(UserSettings settings)
        => !string.IsNullOrWhiteSpace(settings.PushWardApiToken);

    public bool IsRunning
    {
        get { lock (_gate) return _loop is not null; }
    }

    /// <summary>手机推送开启且令牌可用时启动 Answers 轮询；重复调用等同于重启。</summary>
    public void Start()
    {
        Stop();
        if (_disposed || !_settings.MobilePushEnabled || !IsConfigured || _recordDrink is null) return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        lock (_gate) _loop = cts;
        _ = Task.Run(async () =>
        {
            try { await RunAnswerLoopAsync(cts.Token).ConfigureAwait(false); }
            catch { /* shutdown / disposed client: nothing to report */ }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_loop, cts)) _loop = null;
                }
                cts.Dispose(); // the loop is done with the token
            }
        });
    }

    public void Restart() => Start();

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _loop;
            _loop = null;
        }
        // The loop task owns disposal, after it has observed cancellation.
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task<bool> PublishReminderAsync(int suggestedMl, int consumedMl = 0, CancellationToken ct = default)
    {
        try
        {
            if (!IsConfigured) return false;
            return await PublishAsync(PushWardMessageBuilder.BuildReminder(_settings, suggestedMl, consumedMl), ct)
                .ConfigureAwait(false);
        }
        catch { return false; }
    }

    public async Task<bool> PublishConfirmationAsync(int appliedMl, CancellationToken ct = default)
    {
        try { return await PublishAsync(PushWardMessageBuilder.BuildConfirmation(_settings, appliedMl), ct).ConfigureAwait(false); }
        catch { return false; }
    }

    public async Task<bool> PublishTestAsync(int consumedMl = 0, CancellationToken ct = default)
    {
        try
        {
            if (!IsConfigured) return false;
            return await PublishAsync(PushWardMessageBuilder.BuildTest(_settings, consumedMl), ct).ConfigureAwait(false);
        }
        catch { return false; }
    }

    /// <summary>POST 一次；PushWard 返回 2xx（正常为 201）即视为成功。</summary>
    public async Task<bool> PublishAsync(PushWardMessage message, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(message.Token)) return false; // 绝不用空令牌请求
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(PushWardMessageBuilder.Endpoint));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", message.Token.Trim());
            request.Content = new StringContent(message.Json, Encoding.UTF8, "application/json");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false; // 离线 / DNS / TLS / 超时：静默，下个周期再试
        }
    }

    /// <summary>Answers 轮询主循环；测试可直接调用并在记录后取消。</summary>
    public Task PollAnswersAsync(CancellationToken ct) => RunAnswerLoopAsync(ct);

    private async Task RunAnswerLoopAsync(CancellationToken ct)
    {
        var retry = InitialRetryDelay;
        string? cursor = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (cursor is null)
                {
                    // 1) 先取游标；不处理历史 items，避免重启后重复记账。
                    var bootstrap = await FetchAnswersAsync(after: null, wait: false, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(bootstrap?.Cursor))
                    {
                        cursor = bootstrap!.Cursor;
                        retry = InitialRetryDelay;
                    }
                    else
                    {
                        await _delay(retry, ct).ConfigureAwait(false);
                        retry = NextRetry(retry);
                    }
                    continue;
                }

                // 2) 长轮询：after=cursor&wait=25。
                var batch = await FetchAnswersAsync(cursor, wait: true, ct).ConfigureAwait(false);
                if (batch is null)
                {
                    await _delay(retry, ct).ConfigureAwait(false);
                    retry = NextRetry(retry);
                    continue;
                }

                retry = InitialRetryDelay;
                if (!string.IsNullOrWhiteSpace(batch.Cursor)) cursor = batch.Cursor; // 每个响应都推进游标
                HandleAnswers(batch.Items);
                await _delay(ResumeDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                try { await _delay(retry, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                retry = NextRetry(retry);
            }
        }
    }

    private async Task<PushWardAnswerBatch?> FetchAnswersAsync(string? after, bool wait, CancellationToken ct)
    {
        try
        {
            var url = wait
                ? $"{PushWardMessageBuilder.AnswersEndpoint}?after={Uri.EscapeDataString(after ?? "")}&wait={AnswersWaitSeconds}"
                : PushWardMessageBuilder.AnswersEndpoint;

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.PushWardApiToken.Trim());

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PollRequestTimeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return ParseAnswerBatch(body);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            return null; // 单次长轮询超时：下一轮重试
        }
        catch
        {
            return null; // 离线 / DNS / TLS / 坏响应：静默重试
        }
    }

    /// <summary>逐条处理；同一条通知只记一次（notification_id 去重）。确认不带 actions。</summary>
    private void HandleAnswers(IReadOnlyList<PushWardAnswerItem> items)
    {
        foreach (var item in items)
        {
            if (!string.Equals(item.ActionId, PushWardMessageBuilder.DrinkActionId, StringComparison.Ordinal))
                continue;
            if (string.IsNullOrWhiteSpace(item.NotificationId))
                continue;
            if (!_seenIds.Add(item.NotificationId))
                continue;

            try
            {
                _dispatch(() =>
                {
                    var applied = _recordDrink?.Invoke() ?? 0; // 家庭杯；在 UI 线程执行
                    if (applied > 0) _ = PublishConfirmationAsync(applied);
                });
            }
            catch
            {
                // dispatcher 正在关闭 / 无窗口：忽略
            }
        }
    }

    /// <summary>解析 `{ items:[{notification_id, action_id}], cursor }`。</summary>
    public static PushWardAnswerBatch? ParseAnswerBatch(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var cursor = ReadCursor(root);
            var items = new List<PushWardAnswerItem>();
            if (TryGetArray(root, out var array, "items", "answers"))
            {
                foreach (var element in array.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object) continue;
                    var notificationId = ReadString(element, "notification_id") ?? ReadString(element, "id");
                    var actionId = ReadString(element, "action_id");
                    if (string.IsNullOrWhiteSpace(notificationId) || string.IsNullOrWhiteSpace(actionId))
                        continue;
                    items.Add(new PushWardAnswerItem(notificationId.Trim(), actionId.Trim()));
                }
            }

            return new PushWardAnswerBatch(cursor, items);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadCursor(JsonElement root)
    {
        foreach (var name in new[] { "cursor", "next_cursor" })
        {
            if (!root.TryGetProperty(name, out var value)) continue;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()?.Trim(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
        }
        return null;
    }

    private static bool TryGetArray(JsonElement root, out JsonElement array, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out array) && array.ValueKind == JsonValueKind.Array)
                return true;
        }
        array = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            // PushWard returns notification_id as a JSON number.
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static TimeSpan NextRetry(TimeSpan current)
        => TimeSpan.FromSeconds(Math.Min(current.TotalSeconds * 2, MaxRetryDelay.TotalSeconds));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        _lifetime.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
