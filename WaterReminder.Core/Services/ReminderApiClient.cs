using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace WaterReminder.Services;

public readonly record struct ApiResult<T>(bool Ok, int StatusCode, T? Value)
{
    public string? Error { get; init; }
    public static ApiResult<T> Success(T value) => new(true, 200, value);
    public static ApiResult<T> Fail(int statusCode, string? error = null) => new(false, statusCode, default) { Error = error };
}

/// <summary>
/// HTTP client for the headless server. The only credential it holds is the
/// client-server API token, which the operator configures locally.
/// </summary>
public sealed class ReminderApiClient : IDisposable
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
        catch
        {
            return ApiResult<ClientSettingsResponse>.Fail(0);
        }
    }

    public async Task<ApiResult<DrinkResponse>> LogDesktopDrinkAsync(CancellationToken ct = default)
        => await PostDrinkAsync(new DrinkRequest { Source = "desktop" }, ct).ConfigureAwait(false);

    public async Task<ApiResult<DrinkResponse>> LogAmountAsync(int amountMl, CancellationToken ct = default)
        => await PostDrinkAsync(new DrinkRequest { AmountMl = amountMl, Source = "desktop" }, ct).ConfigureAwait(false);

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
        catch
        {
            return ApiResult<DrinkResponse>.Fail(0);
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
        catch
        {
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
        catch
        {
            return ApiResult<T>.Fail(0);
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

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
