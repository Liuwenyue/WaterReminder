using System.Net.Http.Json;
using System.Text.Json;

namespace WaterReminder.Services;

public sealed partial class ReminderApiClient
{
    public Task<ApiResult<FamilyProgressOverview>> GetFamilyProgressAsync(CancellationToken ct = default)
        => ReadAsync<FamilyProgressOverview>("api/family/progress", ct);

    public Task<ApiResult<MemberPushFeedback>> SendPushTestAsync(Guid memberId, CancellationToken ct = default)
        => PostAsync<MemberPushFeedback>($"api/family/{memberId}/push-test", new { }, ct);

    /// <summary>Owner-only: rotates a member's desktop access code. The old code stops working immediately.</summary>
    public Task<ApiResult<FamilyInvitation>> ResetMemberAccessCodeAsync(Guid memberId, CancellationToken ct = default)
        => PostAsync<FamilyInvitation>($"api/family/{memberId}/reset-code", new { }, ct);

    private async Task<ApiResult<T>> PostAsync<T>(string path, object request, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(path, request, Json, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return ApiResult<T>.Fail((int)response.StatusCode, ReadError(body));
            var value = JsonSerializer.Deserialize<T>(body, Json);
            return value is null ? ApiResult<T>.Fail((int)response.StatusCode, ReadError(body)) : ApiResult<T>.Success(value);
        }
        catch (Exception ex)
        {
            return FailFromException<T>(ex, $"POST {path}");
        }
    }
}
