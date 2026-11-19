using System.Net.Http.Json;
using System.Text.Json;

namespace WaterReminder.Services;

public sealed partial class ReminderApiClient
{
    public Task<ApiResult<FamilyProgressOverview>> GetFamilyProgressAsync(CancellationToken ct = default)
        => ReadAsync<FamilyProgressOverview>("api/family/progress", ct);

    public Task<ApiResult<MemberInvite>> CreateInvitationAsync(CreateMemberInvite request, CancellationToken ct = default)
        => PostAsync<MemberInvite>("api/family/invitations", request, ct);

    public Task<ApiResult<MemberPushFeedback>> SendPushTestAsync(Guid memberId, CancellationToken ct = default)
        => PostAsync<MemberPushFeedback>($"api/family/{memberId}/push-test", new { }, ct);

    public async Task<ApiResult<bool>> RevokeInvitationAsync(Guid memberId, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.DeleteAsync($"api/family/{memberId}/invitation", ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? ApiResult<bool>.Success(true)
                : ApiResult<bool>.Fail((int)response.StatusCode, ReadError(body));
        }
        catch
        {
            return ApiResult<bool>.Fail(0);
        }
    }

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
        catch
        {
            return ApiResult<T>.Fail(0);
        }
    }
}
