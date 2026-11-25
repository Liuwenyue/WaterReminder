using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace WaterReminder.Services;

public readonly record struct ApiResult<T>(bool Ok, int StatusCode, T? Value)
{
    public string? Error { get; init; }

    /// <summary>
    /// Diagnostic-only detail (exception type + message) for a failed call.
    /// Never shown to users; read only by logging/diagnostics code.
    /// </summary>
    public string? Diagnostic { get; init; }

    public static ApiResult<T> Success(T value) => new(true, 200, value);
    public static ApiResult<T> Fail(int statusCode, string? error = null)
        => new(false, statusCode, default) { Error = FriendlyError(statusCode, error) };

    /// <summary>
    /// User-facing error text. A clear server message wins; otherwise the
    /// status code maps to a plain Chinese explanation so a dead access code,
    /// a permission denial, or a bad server address is never shown as a bare
    /// "unauthorized" or a generic "please retry".
    /// </summary>
    internal static string? FriendlyError(int statusCode, string? serverError)
    {
        if (!string.IsNullOrWhiteSpace(serverError)
            && !serverError.Equals("unauthorized", StringComparison.OrdinalIgnoreCase))
            return serverError;
        return statusCode switch
        {
            400 => "请求内容有误，请重试。",
            401 => "访问码或 API 令牌无效或已过期，请联系管理员获取新的访问码。",
            403 => "没有权限执行此操作，普通成员不能管理家庭成员。",
            404 => "找不到这条记录或成员。",
            409 => "数据已变更，请刷新后重试。",
            429 => "请求太频繁，稍后再试。",
            >= 500 => "服务器出错，请稍后重试。",
            _ => serverError,
        };
    }

    /// <summary>
    /// User-facing text for a transport failure (no HTTP status at all):
    /// distinguishes a timeout from an unreachable/bad server address.
    /// </summary>
    internal static string FriendlyTransportError(Exception ex) => ex switch
    {
        TaskCanceledException => "连接服务器超时，请检查服务器地址和网络。",
        OperationCanceledException => "连接服务器超时，请检查服务器地址和网络。",
        UriFormatException => "服务器地址格式错误，请检查 client.json 中的 serverUrl。",
        HttpRequestException => "无法连接服务器，请检查服务器地址和网络。",
        _ => "无法连接服务器，请检查服务器地址和网络。",
    };
}

/// <summary>
/// HTTP client for the headless server. The only credential it holds is the
/// client-server API token, which the operator configures locally.
/// </summary>
public sealed partial class ReminderApiClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public ReminderApiClient(string serverUrl, string apiToken, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            throw new ArgumentException("Server URL is required.", nameof(serverUrl));
        if (string.IsNullOrWhiteSpace(apiToken))
            throw new ArgumentException("API token is required.", nameof(apiToken));

        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.BaseAddress = new Uri(serverUrl.Trim().TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
    }

    public Task<ApiResult<StatusResponse>> GetStatusAsync(CancellationToken ct = default)
        => ReadAsync<StatusResponse>("api/status", ct);

    public Task<ApiResult<ClientSettingsResponse>> GetSettingsAsync(CancellationToken ct = default)
        => ReadAsync<ClientSettingsResponse>("api/settings", ct);

    public async Task<ApiResult<ClientSettingsResponse>> SaveSettingsAsync(SettingsUpdate update, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PutAsJsonAsync("api/settings", update, Json, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return ApiResult<ClientSettingsResponse>.Fail((int)response.StatusCode, ReadError(body));
            var value = JsonSerializer.Deserialize<ClientSettingsResponse>(body, Json);
            return value is null
                ? ApiResult<ClientSettingsResponse>.Fail((int)response.StatusCode)
                : ApiResult<ClientSettingsResponse>.Success(value);
        }
        catch (Exception ex)
        {
            return FailFromException<ClientSettingsResponse>(ex, "PUT api/settings");
        }
    }

    /// <summary>
    /// Self-service update of the caller's own PushWard credential. Members only:
    /// the server rejects the owner (403) and a token already used by someone else
    /// (400 with a Chinese message). A null or blank token clears the credential.
    /// The token value is never logged.
    /// </summary>
    public async Task<ApiResult<ClientSettingsResponse>> UpdateOwnPushTokenAsync(string? pushWardToken, CancellationToken ct = default)
    {
        var token = string.IsNullOrWhiteSpace(pushWardToken) ? null : pushWardToken.Trim();
        try
        {
            using var response = await _http.PutAsJsonAsync("api/settings/push-token", new { pushWardToken = token }, Json, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = ReadError(body);
                if ((int)response.StatusCode == 403 && string.IsNullOrWhiteSpace(error))
                    error = "管理员请在服务器上配置推送密钥。";
                return ApiResult<ClientSettingsResponse>.Fail((int)response.StatusCode, error);
            }
            var value = JsonSerializer.Deserialize<ClientSettingsResponse>(body, Json);
            return value is null
                ? ApiResult<ClientSettingsResponse>.Fail((int)response.StatusCode)
                : ApiResult<ClientSettingsResponse>.Success(value);
        }
        catch (Exception ex)
        {
            return FailFromException<ClientSettingsResponse>(ex, "PUT api/settings/push-token");
        }
    }

    public async Task<ApiResult<DrinkResponse>> LogDesktopDrinkAsync(CancellationToken ct = default)
        => await PostDrinkAsync(new DrinkRequest { Source = "desktop" }, ct).ConfigureAwait(false);

    public async Task<ApiResult<DrinkResponse>> LogAmountAsync(int amountMl, CancellationToken ct = default)
        => await PostDrinkAsync(new DrinkRequest { AmountMl = amountMl, Source = "desktop" }, ct).ConfigureAwait(false);

    public Task<ApiResult<TodayRecordsResponse>> GetRecordsAsync(CancellationToken ct = default)
        => ReadAsync<TodayRecordsResponse>("api/records", ct);

    public Task<ApiResult<TodayRecordsResponse>> GetRecordsAsync(string date, CancellationToken ct = default)
        => ReadAsync<TodayRecordsResponse>($"api/records?date={Uri.EscapeDataString(date)}", ct);

    public Task<ApiResult<HistoryResponse>> GetHistoryAsync(int days = 30, CancellationToken ct = default)
        => ReadAsync<HistoryResponse>($"api/history?days={days}", ct);

    public Task<ApiResult<FamilyOverview>> GetFamilyAsync(CancellationToken ct = default)
        => ReadAsync<FamilyOverview>("api/family", ct);

    public Task<ApiResult<ClientSettingsResponse>> GetFamilySettingsAsync(Guid memberId, CancellationToken ct = default)
        => ReadAsync<ClientSettingsResponse>($"api/family/{memberId}/settings", ct);

    public async Task<ApiResult<ClientSettingsResponse>> SaveFamilySettingsAsync(
        Guid memberId, FamilySettingsUpdate update, CancellationToken ct = default)
    {
        var request = update with
        {
            PushWardToken = string.IsNullOrWhiteSpace(update.PushWardToken) ? null : update.PushWardToken.Trim()
        };
        try
        {
            using var response = await _http.PutAsJsonAsync($"api/family/{memberId}/settings", request, Json, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return ApiResult<ClientSettingsResponse>.Fail((int)response.StatusCode, ReadError(body));
            var value = JsonSerializer.Deserialize<ClientSettingsResponse>(body, Json);
            return value is null ? ApiResult<ClientSettingsResponse>.Fail((int)response.StatusCode)
                : ApiResult<ClientSettingsResponse>.Success(value);
        }
        catch (Exception ex) { return FailFromException<ClientSettingsResponse>(ex, $"PUT api/family/{memberId}/settings"); }
    }

    public Task<ApiResult<HistoryResponse>> GetFamilyHistoryAsync(Guid memberId, int days, CancellationToken ct = default)
        => ReadAsync<HistoryResponse>($"api/family/{memberId}/history?days={days}", ct);

    public Task<ApiResult<TodayRecordsResponse>> GetFamilyRecordsAsync(Guid memberId, string date, CancellationToken ct = default)
        => ReadAsync<TodayRecordsResponse>($"api/family/{memberId}/records?date={Uri.EscapeDataString(date)}", ct);

    public async Task<ApiResult<bool>> ChangeFamilyRecordAsync(Guid memberId, string date, Guid eventId,
        int? amountMl, CancellationToken ct = default)
    {
        var operation = new SyncOperation { Id = Guid.NewGuid(), Date = date, EventId = eventId,
            Kind = amountMl.HasValue ? "edit" : "delete", AmountMl = amountMl };
        try
        {
            using var response = await _http.PostAsJsonAsync($"api/family/{memberId}/records/operations",
                new SyncOperationsRequest { Operations = [operation] }, Json, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return ApiResult<bool>.Success(true);
            return ApiResult<bool>.Fail((int)response.StatusCode,
                ReadError(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        { return FailFromException<bool>(e, $"POST api/family/{memberId}/records/operations"); }
    }

    public async Task<ApiResult<FamilyInvitation>> AddFamilyMemberAsync(AddFamilyMember request)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("api/family", request, Json).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return ApiResult<FamilyInvitation>.Fail((int)response.StatusCode, ReadError(body));
            var invitation = JsonSerializer.Deserialize<FamilyInvitation>(body, Json);
            return invitation is null ? ApiResult<FamilyInvitation>.Fail(0) : ApiResult<FamilyInvitation>.Success(invitation);
        }
        catch (Exception ex) { return FailFromException<FamilyInvitation>(ex, "POST api/family"); }
    }

    public async Task<bool> ArchiveFamilyMemberAsync(Guid memberId)
    {
        try
        {
            using var response = await _http.DeleteAsync($"api/family/{memberId}").ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) { LogException(ex, $"DELETE api/family/{memberId}"); return false; }
    }

    public Task<ApiResult<object>> GetHealthAsync(CancellationToken ct = default)
        => ReadAsync<object>("health", ct);

    public async Task<ApiResult<SetTotalResponse>> SetTodayTotalAsync(int totalMl, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PutAsJsonAsync("api/records/total", new SetTotalRequest { TotalMl = totalMl }, Json, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return ApiResult<SetTotalResponse>.Fail((int)response.StatusCode, ReadError(body));
            var value = JsonSerializer.Deserialize<SetTotalResponse>(body, Json);
            return value is null
                ? ApiResult<SetTotalResponse>.Fail((int)response.StatusCode, ReadError(body))
                : ApiResult<SetTotalResponse>.Success(value);
        }
        catch (Exception ex)
        {
            return FailFromException<SetTotalResponse>(ex, "PUT api/records/total");
        }
    }

    public async Task<ApiResult<TodayRecordsResponse>> EditRecordAsync(Guid id, int amountMl, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PutAsJsonAsync($"api/records/{id}", new EditRecordRequest { AmountMl = amountMl }, Json, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return ApiResult<TodayRecordsResponse>.Fail((int)response.StatusCode, ReadError(body));
            var value = JsonSerializer.Deserialize<TodayRecordsResponse>(body, Json);
            return value is null ? ApiResult<TodayRecordsResponse>.Fail((int)response.StatusCode) : ApiResult<TodayRecordsResponse>.Success(value);
        }
        catch (Exception ex)
        {
            return FailFromException<TodayRecordsResponse>(ex, $"PUT api/records/{id}");
        }
    }

    public async Task<ApiResult<TodayRecordsResponse>> DeleteRecordAsync(Guid id, CancellationToken ct = default)
        => await SendRecordsAsync(HttpMethod.Delete, $"api/records/{id}", ct).ConfigureAwait(false);

    public async Task<ApiResult<TodayRecordsResponse>> ClearTodayAsync(CancellationToken ct = default)
        => await SendRecordsAsync(HttpMethod.Delete, "api/records", ct).ConfigureAwait(false);

    public Task<ApiResult<TodayRecordsResponse>> ClearTodayForDateAsync(string date, CancellationToken ct = default)
        => SendRecordsAsync(HttpMethod.Delete, $"api/records/today?date={Uri.EscapeDataString(date)}", ct);

    private async Task<ApiResult<TodayRecordsResponse>> SendRecordsAsync(HttpMethod method, string path, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return ApiResult<TodayRecordsResponse>.Fail((int)response.StatusCode, ReadError(body));
            var value = JsonSerializer.Deserialize<TodayRecordsResponse>(body, Json);
            return value is null ? ApiResult<TodayRecordsResponse>.Fail((int)response.StatusCode) : ApiResult<TodayRecordsResponse>.Success(value);
        }
        catch (Exception ex)
        {
            return FailFromException<TodayRecordsResponse>(ex, $"{method} {path}");
        }
    }

    private async Task<ApiResult<DrinkResponse>> PostDrinkAsync(DrinkRequest request, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("api/drinks", request, Json, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return ApiResult<DrinkResponse>.Fail((int)response.StatusCode);
            var value = await response.Content.ReadFromJsonAsync<DrinkResponse>(Json, ct).ConfigureAwait(false);
            return value is null ? ApiResult<DrinkResponse>.Fail((int)response.StatusCode) : ApiResult<DrinkResponse>.Success(value);
        }
        catch (Exception ex)
        {
            return FailFromException<DrinkResponse>(ex, "POST api/drinks");
        }
    }

    public async Task<bool> AckDesktopAsync(string alertId, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                "api/desktop-ack", new AckRequest { Id = alertId }, Json, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LogException(ex, "POST api/desktop-ack");
            return false;
        }
    }

    private async Task<ApiResult<T>> ReadAsync<T>(string path, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return ApiResult<T>.Fail((int)response.StatusCode, ReadError(body));
            var value = JsonSerializer.Deserialize<T>(body, Json);
            return value is null
                ? ApiResult<T>.Fail((int)response.StatusCode, ReadError(body))
                : ApiResult<T>.Success(value);
        }
        catch (Exception ex)
        {
            return FailFromException<T>(ex, $"GET {path}");
        }
    }

    private static string? ReadError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
                return error.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>
    /// Builds a failed result from a transport exception: keeps a one-line
    /// diagnostic (exception type + message) on the result and writes the full
    /// exception to the log. The user-visible <see cref="ApiResult{T}.Error"/>
    /// is a plain Chinese explanation of the connection problem, never the raw
    /// exception text.
    /// </summary>
    private static ApiResult<T> FailFromException<T>(Exception ex, string operation)
    {
        var diagnostic = ex.GetType().Name + ": " + ex.Message;
        FileLogger.Default.Error($"Reminder API {operation} failed ({diagnostic})", ex);
        return ApiResult<T>.Fail(0, ApiResult<T>.FriendlyTransportError(ex)) with { Diagnostic = diagnostic };
    }

    /// <summary>Logs a transport exception for API calls that return only true/false.</summary>
    private static void LogException(Exception ex, string operation)
    {
        FileLogger.Default.Error($"Reminder API {operation} failed ({ex.GetType().Name}: {ex.Message})", ex);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
