using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using WaterReminder.Server;
using WaterReminder.Services;
using Xunit;

namespace WaterReminder.Tests;

/// <summary>
/// End-to-end multi-user flow (task doc "多用户流程" steps 1-10) plus the record-date,
/// push-token self-service, push-feedback isolation, and family-registry recovery checks.
/// Runs over TestServer (in-memory transport): no TCP, so it also runs in the sandbox
/// where loopback sockets are blocked. Every test uses a fresh temp directory and fake
/// tokens; the real server data directory and client.json are never touched.
/// </summary>
public sealed class MultiUserFlowTests
{
    private const string OwnerCode = "test-multiuser-owner";
    private const string FakeTokenA = "fake-push-token-member-a";
    private const string FakeTokenB = "fake-push-token-member-b";
    private const string FakeTokenOwner = "fake-push-token-owner";

    private static string Today => DateOnly.FromDateTime(ShanghaiClock.Now()).ToString("yyyy-MM-dd");
    private static string Yesterday => DateOnly.FromDateTime(ShanghaiClock.Now()).AddDays(-1).ToString("yyyy-MM-dd");

    [Fact]
    public async Task TenStepFlow()
    {
        using var files = new TestFiles();
        await using var host = await Host.Start(files.Path);
        var today = Today;

        // 1. Owner reads the family list.
        var overview = await host.Owner.GetFromJsonAsync<FamilyOverview>("api/family");
        Assert.NotNull(overview);
        Assert.True(overview!.IsOwner);
        Assert.Contains(overview.Members, m => m.Id == Guid.Empty && m.Name == "我");

        // 2. Owner creates member A and gets an access code.
        var invitationA = await AddMember(host.Owner, "成员甲");
        Assert.StartsWith("wr_", invitationA.AccessCode);
        using var memberA = host.Client(invitationA.AccessCode);

        // 3. Member A reads their own records.
        Assert.Equal(0, (await Records(memberA, today)).ConsumedMl);
        await PostSync(memberA, Operation(today, "drink", 300));
        Assert.Equal(300, (await Records(memberA, today)).ConsumedMl);

        // 4. Member A reads the family list: must see only themselves.
        var memberView = await memberA.GetFromJsonAsync<FamilyOverview>("api/family");
        Assert.NotNull(memberView);
        Assert.False(memberView!.IsOwner);
        var only = Assert.Single(memberView.Members);
        Assert.Equal(invitationA.Member.Id, only.Id);
        Assert.Equal("成员甲", only.Name);

        // 5. Owner edits member A's record for today.
        var familyRecords = await host.Owner.GetFromJsonAsync<TodayRecordsResponse>(
            $"api/family/{invitationA.Member.Id}/records?date={today}");
        Assert.NotNull(familyRecords);
        Assert.True(familyRecords!.Editable);
        var eventId = Assert.Single(familyRecords.Events).Id;
        var edit = new SyncOperationsRequest
        {
            Operations = [new SyncOperation { Id = Guid.NewGuid(), Date = today, Kind = "edit", EventId = eventId, AmountMl = 450 }],
        };
        using var edited = await host.Owner.PostAsJsonAsync($"api/family/{invitationA.Member.Id}/records/operations", edit);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        // 6. Member A re-reads and sees the change.
        Assert.Equal(450, (await Records(memberA, today)).ConsumedMl);

        // 7. Create member B; B must not see A's or the owner's records.
        var invitationB = await AddMember(host.Owner, "成员乙");
        using var memberB = host.Client(invitationB.AccessCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberB.GetAsync($"api/family/{invitationA.Member.Id}/records?date={today}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberB.GetAsync($"api/family/{invitationA.Member.Id}/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberB.GetAsync($"api/family/{invitationA.Member.Id}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberB.GetAsync($"api/family/{Guid.Empty}/records?date={today}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberB.GetAsync("api/family/progress")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberB.GetAsync($"api/family/{invitationA.Member.Id}/push-feedback")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberB.PostAsJsonAsync("api/family", new AddFamilyMember("成员丙", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberB.DeleteAsync($"api/family/{invitationA.Member.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberB.PostAsync($"api/family/{invitationA.Member.Id}/reset-code", null)).StatusCode);
        using var forbiddenOp = await memberB.PostAsJsonAsync($"api/family/{invitationA.Member.Id}/records/operations", edit);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenOp.StatusCode);
        Assert.Equal(0, (await Records(memberB, today)).ConsumedMl);
        var snapshot = await memberB.GetFromJsonAsync<SyncSnapshotResponse>("api/sync/snapshot");
        Assert.NotNull(snapshot);
        Assert.Equal(invitationB.Member.Id, snapshot!.MemberId);
        Assert.Equal("成员乙", snapshot.MemberName);
        Assert.Equal(0, snapshot.Days.Single(day => day.Date == today).ConsumedMl);

        // 8. Member A changes their own settings; owner and B are unaffected.
        var ownerGoalBefore = (await host.Owner.GetFromJsonAsync<ClientSettingsResponse>("api/settings"))!.DailyGoalMl;
        var bGoalBefore = (await memberB.GetFromJsonAsync<ClientSettingsResponse>("api/settings"))!.DailyGoalMl;
        var selfUpdate = SettingsEditor.FromResponse((await memberA.GetFromJsonAsync<ClientSettingsResponse>("api/settings"))!);
        selfUpdate.DailyGoalMl = 1800;
        using var selfSaved = await memberA.PutAsJsonAsync("api/settings", selfUpdate, ReminderApiClient.Json);
        Assert.Equal(HttpStatusCode.OK, selfSaved.StatusCode);
        Assert.Equal(1800, (await memberA.GetFromJsonAsync<ClientSettingsResponse>("api/settings"))!.DailyGoalMl);
        Assert.Equal(ownerGoalBefore, (await host.Owner.GetFromJsonAsync<ClientSettingsResponse>("api/settings"))!.DailyGoalMl);
        Assert.Equal(bGoalBefore, (await memberB.GetFromJsonAsync<ClientSettingsResponse>("api/settings"))!.DailyGoalMl);
        var adminUpdate = SettingsEditor.FromResponse(
            (await host.Owner.GetFromJsonAsync<ClientSettingsResponse>($"api/family/{invitationA.Member.Id}/settings"))!);
        adminUpdate.DailyGoalMl = 2200;
        using var adminSaved = await host.Owner.PutAsJsonAsync(
            $"api/family/{invitationA.Member.Id}/settings", new FamilySettingsUpdate(adminUpdate), ReminderApiClient.Json);
        Assert.Equal(HttpStatusCode.OK, adminSaved.StatusCode);
        Assert.Equal(2200, (await memberA.GetFromJsonAsync<ClientSettingsResponse>("api/settings"))!.DailyGoalMl);
        Assert.Equal(bGoalBefore, (await memberB.GetFromJsonAsync<ClientSettingsResponse>("api/settings"))!.DailyGoalMl);

        // 9. Reset A's code: old code dies, new code works, history is kept.
        var rotated = await ResetCode(host.Owner, invitationA.Member.Id);
        Assert.Equal(invitationA.Member.Id, rotated.Member.Id);
        Assert.NotEqual(invitationA.AccessCode, rotated.AccessCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await memberA.GetAsync("api/status")).StatusCode);
        using var renewedA = host.Client(rotated.AccessCode);
        Assert.Equal(450, (await Records(renewedA, today)).ConsumedMl);
        Assert.Equal(2200, (await renewedA.GetFromJsonAsync<ClientSettingsResponse>("api/settings"))!.DailyGoalMl);

        // 10. Archive A: code dies, owner still sees the history.
        using var archived = await host.Owner.DeleteAsync($"api/family/{invitationA.Member.Id}");
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await renewedA.GetAsync("api/status")).StatusCode);
        var history = await host.Owner.GetFromJsonAsync<HistoryResponse>($"api/family/{invitationA.Member.Id}/history?days=30");
        Assert.NotNull(history);
        var day = Assert.Single(history!.Days);
        Assert.Equal(today, day.Date);
        Assert.Equal(450, day.ConsumedMl);
        Assert.Equal(1, day.EventCount);
        var archivedRecords = await host.Owner.GetFromJsonAsync<TodayRecordsResponse>(
            $"api/family/{invitationA.Member.Id}/records?date={today}");
        Assert.NotNull(archivedRecords);
        Assert.False(archivedRecords!.Editable);
        Assert.Equal(450, archivedRecords.ConsumedMl);
        var finalOverview = await host.Owner.GetFromJsonAsync<FamilyOverview>("api/family");
        Assert.Contains(finalOverview!.Members, m => m.Id == invitationA.Member.Id && m.Archived);
    }

    [Fact]
    public async Task OwnerCanEditMemberHistory()
    {
        using var files = new TestFiles();
        await using var host = await Host.Start(files.Path);
        var today = Today;
        var yesterday = Yesterday;
        var invitation = await AddMember(host.Owner, "成员甲");
        using var member = host.Client(invitation.AccessCode);

        await PostSync(member, Operation(yesterday, "drink", 300));
        var yesterdayRecords = await Records(member, yesterday);
        Assert.Equal(300, yesterdayRecords.ConsumedMl);
        var oldId = Assert.Single(yesterdayRecords.Events).Id;

        // An old record id cannot be edited or deleted through the no-date endpoints.
        using var editOld = await member.PutAsJsonAsync($"api/records/{oldId}", new EditRecordRequest { AmountMl = 100 });
        Assert.Equal(HttpStatusCode.NotFound, editOld.StatusCode);
        using var deleteOld = await member.DeleteAsync($"api/records/{oldId}");
        Assert.Equal(HttpStatusCode.NotFound, deleteOld.StatusCode);
        Assert.Equal(300, (await Records(member, yesterday)).ConsumedMl);

        // Family view: yesterday and today are both editable for the owner.
        var familyYesterday = await host.Owner.GetFromJsonAsync<TodayRecordsResponse>(
            $"api/family/{invitation.Member.Id}/records?date={yesterday}");
        Assert.NotNull(familyYesterday);
        Assert.True(familyYesterday!.Editable);
        var familyToday = await host.Owner.GetFromJsonAsync<TodayRecordsResponse>(
            $"api/family/{invitation.Member.Id}/records?date={today}");
        Assert.NotNull(familyToday);
        Assert.True(familyToday!.Editable);

        // Owner record operations accept a historical date.
        var historyEdit = new SyncOperationsRequest
        {
            Operations = [new SyncOperation { Id = Guid.NewGuid(), Date = yesterday, Kind = "edit", EventId = oldId, AmountMl = 100 }],
        };
        using var accepted = await host.Owner.PostAsJsonAsync($"api/family/{invitation.Member.Id}/records/operations", historyEdit);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(100, (await Records(member, yesterday)).ConsumedMl);

        // Today edits still work through the member endpoint.
        await PostSync(member, Operation(today, "drink", 200));
        var todayId = Assert.Single((await Records(member, today)).Events).Id;
        using var editToday = await member.PutAsJsonAsync($"api/records/{todayId}", new EditRecordRequest { AmountMl = 250 });
        Assert.Equal(HttpStatusCode.OK, editToday.StatusCode);
        Assert.Equal(250, (await Records(member, today)).ConsumedMl);

        // Clearing today requires the matching ledger date, otherwise 409.
        using var wrongDate = await member.DeleteAsync("api/records/today?date=" + yesterday);
        Assert.Equal(HttpStatusCode.Conflict, wrongDate.StatusCode);
        Assert.Equal(250, (await Records(member, today)).ConsumedMl);
        using var cleared = await member.DeleteAsync("api/records/today?date=" + today);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(0, (await Records(member, today)).ConsumedMl);
        // Yesterday keeps the owner's edit (100); clearing today must not touch it.
        Assert.Equal(100, (await Records(member, yesterday)).ConsumedMl);
    }

    [Fact]
    public async Task MemberPushTokenSelfService()
    {
        using var files = new TestFiles();
        await using var host = await Host.Start(files.Path, ownerPushToken: FakeTokenOwner);
        var invitationA = await AddMember(host.Owner, "成员甲");
        var invitationB = await AddMember(host.Owner, "成员乙", FakeTokenB);
        using var memberA = host.Client(invitationA.AccessCode);
        using var memberB = host.Client(invitationB.AccessCode);

        // Member A can set their own token.
        using var set = await memberA.PutAsJsonAsync("api/settings/push-token", new { pushWardToken = FakeTokenA });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.True((await memberA.GetFromJsonAsync<ClientSettingsResponse>("api/settings"))!.PushWardConfigured);

        // ...but cannot claim B's token, the owner's token, or a malformed one.
        using var clashB = await memberA.PutAsJsonAsync("api/settings/push-token", new { pushWardToken = FakeTokenB });
        Assert.Equal(HttpStatusCode.BadRequest, clashB.StatusCode);
        using var clashOwner = await memberA.PutAsJsonAsync("api/settings/push-token", new { pushWardToken = FakeTokenOwner });
        Assert.Equal(HttpStatusCode.BadRequest, clashOwner.StatusCode);
        using var malformed = await memberA.PutAsJsonAsync("api/settings/push-token", new { pushWardToken = "has space" });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var missing = await memberA.PutAsJsonAsync("api/settings/push-token", new { });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.True((await memberA.GetFromJsonAsync<ClientSettingsResponse>("api/settings"))!.PushWardConfigured);
        Assert.True((await memberB.GetFromJsonAsync<ClientSettingsResponse>("api/settings"))!.PushWardConfigured);

        // Clearing the token works, and the owner cannot use the member endpoint.
        using var cleared = await memberA.PutAsJsonAsync("api/settings/push-token", new { pushWardToken = "" });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.False((await memberA.GetFromJsonAsync<ClientSettingsResponse>("api/settings"))!.PushWardConfigured);
        using var ownerDenied = await host.Owner.PutAsJsonAsync("api/settings/push-token", new { pushWardToken = FakeTokenA });
        Assert.Equal(HttpStatusCode.Forbidden, ownerDenied.StatusCode);

        // A token smuggled into PUT /api/settings is still rejected, never stored.
        var update = SettingsEditor.FromResponse((await memberA.GetFromJsonAsync<ClientSettingsResponse>("api/settings"))!);
        var json = JsonSerializer.Serialize(update, ReminderApiClient.Json);
        json = json[..^1] + ",\"pushWardToken\":\"fake-should-be-rejected\"}";
        using var smuggled = await memberA.PutAsync("api/settings", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, smuggled.StatusCode);
        Assert.False((await memberA.GetFromJsonAsync<ClientSettingsResponse>("api/settings"))!.PushWardConfigured);

        // The member can still switch push itself on and off.
        update.MobilePushEnabled = false;
        using var off = await memberA.PutAsJsonAsync("api/settings", update, ReminderApiClient.Json);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await memberA.GetFromJsonAsync<ClientSettingsResponse>("api/settings"))!.MobilePushEnabled);
    }

    [Fact]
    public async Task PushFeedbackIsolation()
    {
        using var files = new TestFiles();
        await using var host = await Host.Start(files.Path);
        var invitationA = await AddMember(host.Owner, "成员甲");
        var invitationB = await AddMember(host.Owner, "成员乙");
        using var memberA = host.Client(invitationA.AccessCode);
        using var memberB = host.Client(invitationB.AccessCode);

        var own = await memberA.GetFromJsonAsync<MemberPushFeedback>($"api/family/{invitationA.Member.Id}/push-feedback");
        Assert.NotNull(own);
        Assert.Equal("not_tested", own!.State);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await memberB.GetAsync($"api/family/{invitationA.Member.Id}/push-feedback")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await memberA.GetAsync($"api/family/{Guid.Empty}/push-feedback")).StatusCode);
        var byOwner = await host.Owner.GetFromJsonAsync<MemberPushFeedback>($"api/family/{invitationA.Member.Id}/push-feedback");
        Assert.NotNull(byOwner);
        var ownerOwn = await host.Owner.GetFromJsonAsync<MemberPushFeedback>($"api/family/{Guid.Empty}/push-feedback");
        Assert.NotNull(ownerOwn);
    }

    [Fact]
    public async Task CorruptFamilyRegistryRecoversFromBackup()
    {
        using var files = new TestFiles();
        FamilyInvitation invitationA;
        await using (var host = await Host.Start(files.Path))
        {
            invitationA = await AddMember(host.Owner, "成员甲");
            await AddMember(host.Owner, "成员乙");
        }
        // The second save kept the previous generation as family.json.bak.
        Assert.True(File.Exists(Path.Combine(files.Path, "family.json.bak")));
        File.WriteAllText(Path.Combine(files.Path, "family.json"), "{");

        await using var recovered = await Host.Start(files.Path);
        var overview = await recovered.Owner.GetFromJsonAsync<FamilyOverview>("api/family");
        Assert.NotNull(overview);
        // The backup holds the previous generation: A is restored, B (added after it) is not.
        Assert.Contains(overview!.Members, m => m.Id == invitationA.Member.Id && m.Name == "成员甲");
        using var memberA = recovered.Client(invitationA.AccessCode);
        Assert.Equal(HttpStatusCode.OK, (await memberA.GetAsync("api/status")).StatusCode);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(files.Path, "family.json")));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task CorruptFamilyRegistryWithoutBackupStartsEmpty()
    {
        using var files = new TestFiles();
        File.WriteAllText(Path.Combine(files.Path, "family.json"), "{not json");
        Assert.False(File.Exists(Path.Combine(files.Path, "family.json.bak")));

        await using var host = await Host.Start(files.Path);
        var overview = await host.Owner.GetFromJsonAsync<FamilyOverview>("api/family");
        Assert.NotNull(overview);
        Assert.True(overview!.IsOwner);
        Assert.Single(overview.Members);
        Assert.Equal(Guid.Empty, overview.Members[0].Id);
        Assert.Single(Directory.GetFiles(files.Path, "family.json.corrupt-*"));
        // The server stays usable: the owner can onboard members again.
        var invitation = await AddMember(host.Owner, "成员甲");
        Assert.StartsWith("wr_", invitation.AccessCode);
    }

    private static SyncOperation Operation(string date, string kind, int amount)
        => new() { Id = Guid.NewGuid(), Date = date, Kind = kind, AmountMl = amount, Source = "cup", At = date + "T09:30:00" };

    private static async Task PostSync(HttpClient client, SyncOperation operation)
    {
        using var response = await client.PostAsJsonAsync("api/sync/operations", new SyncOperationsRequest { Operations = [operation] });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<TodayRecordsResponse> Records(HttpClient client, string date)
    {
        using var response = await client.GetAsync("api/records?date=" + date);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TodayRecordsResponse>())!;
    }

    private static async Task<FamilyInvitation> AddMember(HttpClient client, string name, string? pushWardToken = null)
    {
        using var response = await client.PostAsJsonAsync("api/family", new AddFamilyMember(name, pushWardToken));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FamilyInvitation>())!;
    }

    private static async Task<FamilyInvitation> ResetCode(HttpClient client, Guid memberId)
    {
        using var response = await client.PostAsync($"api/family/{memberId}/reset-code", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FamilyInvitation>())!;
    }

    private sealed class TestFiles : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WaterReminderTests", Guid.NewGuid().ToString("N"));
        public TestFiles() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ReminderEngine _engine;
        public HttpClient Owner { get; }
        public HttpClient Anonymous { get; }
        private Host(WebApplication app, ReminderEngine engine)
        {
            _app = app;
            _engine = engine;
            Owner = Client(OwnerCode);
            Anonymous = Client(null);
        }
        public HttpClient Client(string? token)
        {
            var http = _app.GetTestClient();
            if (token is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return http;
        }
        public static async Task<Host> Start(string directory, string? ownerPushToken = null)
        {
            Func<string?>? pushToken = ownerPushToken is null ? null : () => ownerPushToken;
            var owner = new ReminderEngine(new ReminderEngineOptions { DataDirectory = directory, PollAnswers = false, PushWardToken = pushToken });
            var options = ServerOptions.Create(directory, OwnerCode, 0, "127.0.0.1", true, false);
            var app = ServerHost.Build(options, owner, webHost => webHost.UseTestServer());
            await app.StartAsync();
            return new(app, owner);
        }
        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            Anonymous.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            _engine.Dispose();
        }
    }
}
