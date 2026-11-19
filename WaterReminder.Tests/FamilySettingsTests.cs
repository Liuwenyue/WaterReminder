using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using WaterReminder.Server;
using WaterReminder.Services;
using Xunit;

namespace WaterReminder.Tests;

public sealed class FamilySettingsTests
{
    private const string OwnerCode = "test-family-settings-owner";
    private const string OwnerPush = "test-owner-phone-token";

    [Fact]
    public async Task PushTest_AllowsOwnerAndSharedTargetsOnly()
    {
        using var files = new TestFiles();
        new StorageService(files.Path).SaveSettings(new UserSettings { MobilePushEnabled = false });
        await using var host = await Host.Start(files.Path);
        var shared = await Invite(host.Owner, "共享成员", null);
        using var member = host.Client(shared.AccessCode);
        using var login = await host.Owner.PostAsJsonAsync("api/device/login", new { deviceId = new string('a', 64) });
        login.EnsureSuccessStatusCode();
        var privateAccount = await login.Content.ReadFromJsonAsync<JsonElement>();
        var privateId = privateAccount.GetProperty("memberId").GetGuid();
        Assert.False(privateAccount.GetProperty("sharedWithOwner").GetBoolean());

        static string PushTest(Guid id) => $"api/family/{id}/push-test";
        using var ownerTest = await host.Owner.PostAsync(PushTest(Guid.Empty), null);
        Assert.Equal(HttpStatusCode.OK, ownerTest.StatusCode);
        using var sharedTest = await host.Owner.PostAsync(PushTest(shared.Member.Id), null);
        Assert.Equal(HttpStatusCode.OK, sharedTest.StatusCode);
        using var privateTest = await host.Owner.PostAsync(PushTest(privateId), null);
        Assert.Equal(HttpStatusCode.NotFound, privateTest.StatusCode);
        using var memberToOwner = await member.PostAsync(PushTest(Guid.Empty), null);
        Assert.Equal(HttpStatusCode.Forbidden, memberToOwner.StatusCode);
        using var memberToSelf = await member.PostAsync(PushTest(shared.Member.Id), null);
        Assert.Equal(HttpStatusCode.Forbidden, memberToSelf.StatusCode);
    }

    [Fact]
    public async Task Settings_ReportsEffectivePushWardConfigurationWithoutAcceptingItInUpdates()
    {
        using var files = new TestFiles();
        await using var host = await Host.Start(files.Path);
        var invitation = await Invite(host.Owner, "尚未配置", null);
        using var member = host.Client(invitation.AccessCode);
        using var ownerJson = JsonDocument.Parse(await host.Owner.GetStringAsync("api/settings"));
        Assert.True(ownerJson.RootElement.GetProperty("pushWardConfigured").GetBoolean());
        AssertNoSecrets(ownerJson.RootElement.GetRawText(), OwnerPush, OwnerCode);

        var before = (await member.GetFromJsonAsync<ClientSettingsResponse>("api/settings"))!;
        Assert.False(before.PushWardConfigured);
        Assert.False(string.IsNullOrEmpty(before.Revision));
        var edit = SettingsEditor.FromResponse(before);
        edit.ExpectedRevision = before.Revision;
        edit.DailyGoalMl = before.DailyGoalMl + 100;
        var body = JsonSerializer.SerializeToNode(edit, ReminderApiClient.Json)!.AsObject();
        body["pushWardConfigured"] = true;
        using var rejected = await member.PutAsJsonAsync("api/settings", body);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        body.Remove("pushWardConfigured");
        using var saved = await member.PutAsJsonAsync("api/settings", body);
        saved.EnsureSuccessStatusCode();
        var after = (await saved.Content.ReadFromJsonAsync<ClientSettingsResponse>())!;
        Assert.False(after.PushWardConfigured);
        Assert.Equal(edit.DailyGoalMl, after.DailyGoalMl);
        Assert.NotEqual(before.Revision, after.Revision);
        using var stale = await member.PutAsJsonAsync("api/settings", body);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task OwnerEditsFullMemberSettingsAndPhoneToken_PersistsWithoutLeakingOrChangingOtherData()
    {
        using var files = new TestFiles();
        FamilyInvitation invitation;
        SettingsUpdate changed;
        string otherBefore = "";
        Guid otherId = Guid.Empty;
        await using (var host = await Host.Start(files.Path))
        {
            invitation = await Invite(host.Owner, "家人甲", "test-old-phone");
            var other = await Invite(host.Owner, "家人乙", null);
            otherId = other.Member.Id;
            otherBefore = await host.Owner.GetStringAsync(Route(otherId));
            using var member = host.Client(invitation.AccessCode);
            using var logged = await member.PostAsJsonAsync("api/drinks", new DrinkRequest { AmountMl = 123 });
            Assert.Equal(HttpStatusCode.OK, logged.StatusCode);
            var ownerBefore = await host.Owner.GetStringAsync("api/settings");
            changed = SettingsEditor.FromResponse(await Settings(host.Owner, invitation.Member.Id));
            changed.DailyGoalMl = 2400;
            changed.WorkCupMl = 400;
            changed.HomeCupMl = 700;
            changed.WorkStart = "09:00";
            changed.WorkEnd = "18:30";
            changed.LunchEnabled = true;
            changed.LunchStart = "12:30";
            changed.LunchEnd = "13:30";
            changed.NoDrinkBeforeEndMinutes = 20;
            changed.QuietStart = "22:30";
            changed.MinReminderGapMinutes = 35;
            changed.MobilePushEnabled = false;
            changed.ActiveDays = [true, false, true, false, true, true, true];
            changed.RemindersPaused = true;
            changed.PauseUntil = ShanghaiClock.Now().AddDays(1).ToString("yyyy-MM-ddTHH:mm:ss");
            using var saved = await host.Owner.PutAsJsonAsync(Route(invitation.Member.Id), new FamilySettingsUpdate(changed, "  test-new-phone  "));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var body = await saved.Content.ReadAsStringAsync();
            AssertNoSecrets(body, "test-old-phone", "test-new-phone", OwnerPush, invitation.AccessCode, OwnerCode);
            var result = JsonSerializer.Deserialize<ClientSettingsResponse>(body, ReminderApiClient.Json)!;
            Assert.Equal(JsonSerializer.Serialize(changed), JsonSerializer.Serialize(SettingsEditor.FromResponse(result)));
            var memberBody = await member.GetStringAsync("api/settings");
            AssertNoSecrets(memberBody, "test-new-phone", OwnerPush, invitation.AccessCode);
            var memberSettings = JsonSerializer.Deserialize<ClientSettingsResponse>(memberBody, ReminderApiClient.Json)!;
            Assert.Equal(2400, memberSettings.DailyGoalMl);
            Assert.True(memberSettings.PushWardConfigured);
            var status = await member.GetFromJsonAsync<StatusResponse>("api/status");
            Assert.Equal(400, status!.WorkCupMl);
            Assert.Equal(700, status.HomeCupMl);
            Assert.Equal(123, status.ConsumedMl);
            Assert.Equal(35, status.MinReminderGapMinutes);
            Assert.Equal(ownerBefore, await host.Owner.GetStringAsync("api/settings"));
            Assert.Equal(otherBefore, await host.Owner.GetStringAsync(Route(otherId)));
            Assert.Equal("test-new-phone", files.MemberStorage(invitation.Member.Id).LoadSettings().PushWardApiToken);

            using var preserve = await host.Owner.PutAsJsonAsync(Route(invitation.Member.Id), new FamilySettingsUpdate(changed, "  "));
            Assert.Equal(HttpStatusCode.OK, preserve.StatusCode);
            Assert.Equal("test-new-phone", files.MemberStorage(invitation.Member.Id).LoadSettings().PushWardApiToken);
        }
        await using var restarted = await Host.Start(files.Path);
        var reloaded = await Settings(restarted.Owner, invitation.Member.Id);
        Assert.Equal(JsonSerializer.Serialize(changed), JsonSerializer.Serialize(SettingsEditor.FromResponse(reloaded)));
        Assert.Equal(otherBefore, await restarted.Owner.GetStringAsync(Route(otherId)));
        AssertNoSecrets(await restarted.Owner.GetStringAsync(Route(invitation.Member.Id)), "test-new-phone", "test-old-phone");
        using var returning = restarted.Client(invitation.AccessCode);
        Assert.Equal(123, (await returning.GetFromJsonAsync<StatusResponse>("api/status"))!.ConsumedMl);
    }

    [Fact]
    public async Task InvalidOrSharedPhoneTokensAndInvalidSettingsAreRejectedAtomically()
    {
        using var files = new TestFiles();
        await using var host = await Host.Start(files.Path);
        var first = await Invite(host.Owner, "家人甲", "test-first-phone");
        await Invite(host.Owner, "家人乙", "test-second-phone");
        var update = SettingsEditor.FromResponse(await Settings(host.Owner, first.Member.Id));
        update.DailyGoalMl = 2400;
        var path = files.MemberSettingsPath(first.Member.Id);
        var original = await File.ReadAllTextAsync(path);
        foreach (var token in new[] { OwnerPush, "test-second-phone", "invalid token", new string('x', 513), "bad\nkey" })
        {
            using var response = await host.Owner.PutAsJsonAsync(Route(first.Member.Id), new FamilySettingsUpdate(update, token));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            AssertNoSecrets(await response.Content.ReadAsStringAsync(), token, OwnerPush, "test-second-phone");
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        update.DailyGoalMl = 1;
        using var invalid = await host.Owner.PutAsJsonAsync(Route(first.Member.Id), new FamilySettingsUpdate(update, "valid-new-phone"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(original, await File.ReadAllTextAsync(path));

        var goodJson = JsonSerializer.Serialize(SettingsEditor.FromResponse(await Settings(host.Owner, first.Member.Id)), ReminderApiClient.Json);
        foreach (var body in new[]
        {
            "{\"settings\":null}", "{\"pushWardToken\":\"untrusted\"}",
            $"{{\"settings\":{goodJson},\"pushWardToken\":123}}",
            $"{{\"settings\":{goodJson},\"apiToken\":\"secret-extra-field\"}}",
            "{\"settings\":{\"pushWardApiToken\":\"nested-secret\"}}",
            $"{{\"settings\":{goodJson},\"pushWardToken\":\"one\",\"PushWardToken\":\"two\"}}",
        })
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await host.Owner.PutAsync(Route(first.Member.Id), content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            AssertNoSecrets(await response.Content.ReadAsStringAsync(), "secret-extra-field", "nested-secret", "untrusted");
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        var unchangedTokenUpdate = SettingsEditor.FromResponse(await Settings(host.Owner, first.Member.Id));
        using var ownToken = await host.Owner.PutAsJsonAsync(Route(first.Member.Id), new FamilySettingsUpdate(unchangedTokenUpdate, "test-first-phone"));
        Assert.Equal(HttpStatusCode.OK, ownToken.StatusCode);
    }

    [Fact]
    public async Task SettingsEndpointsRequireOwnerAndActiveMember_AndStoppedMemberStaysStopped()
    {
        using var files = new TestFiles();
        await using var host = await Host.Start(files.Path);
        var first = await Invite(host.Owner, "家人甲", null);
        var second = await Invite(host.Owner, "家人乙", null);
        var update = new FamilySettingsUpdate(SettingsEditor.FromResponse(await Settings(host.Owner, first.Member.Id)));
        using var anonymous = host.Client(null);
        using var member = host.Client(first.AccessCode);
        foreach (var id in new[] { first.Member.Id, second.Member.Id })
        {
            using var deniedGet = await member.GetAsync(Route(id));
            using var deniedPut = await member.PutAsJsonAsync(Route(id), update);
            Assert.Equal(HttpStatusCode.Forbidden, deniedGet.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, deniedPut.StatusCode);
        }
        using var anonymousGet = await anonymous.GetAsync(Route(first.Member.Id));
        using var anonymousPut = await anonymous.PutAsJsonAsync(Route(first.Member.Id), update);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousGet.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousPut.StatusCode);
        foreach (var id in new[] { Guid.NewGuid(), Guid.Empty })
        {
            using var missingGet = await host.Owner.GetAsync(Route(id));
            using var missingPut = await host.Owner.PutAsJsonAsync(Route(id), update);
            Assert.Equal(HttpStatusCode.NotFound, missingGet.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, missingPut.StatusCode);
        }
        using var archived = await host.Owner.DeleteAsync($"api/family/{first.Member.Id}");
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var original = await File.ReadAllTextAsync(files.MemberSettingsPath(first.Member.Id));
        using var archivedGet = await host.Owner.GetAsync(Route(first.Member.Id));
        using var archivedPut = await host.Owner.PutAsJsonAsync(Route(first.Member.Id), update);
        using var stopped = await member.GetAsync("api/status");
        Assert.Equal(HttpStatusCode.NotFound, archivedGet.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, archivedPut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, stopped.StatusCode);
        Assert.Equal(original, await File.ReadAllTextAsync(files.MemberSettingsPath(first.Member.Id)));
    }

    [Fact]
    public async Task ConcurrentMemberUpdatesCannotShareOnePhoneToken()
    {
        using var files = new TestFiles();
        await using var host = await Host.Start(files.Path);
        var first = await Invite(host.Owner, "家人甲", null);
        var second = await Invite(host.Owner, "家人乙", null);
        var firstUpdate = new FamilySettingsUpdate(SettingsEditor.FromResponse(await Settings(host.Owner, first.Member.Id)), "shared-new-phone");
        var secondUpdate = new FamilySettingsUpdate(SettingsEditor.FromResponse(await Settings(host.Owner, second.Member.Id)), "shared-new-phone");
        var responses = await Task.WhenAll(host.Owner.PutAsJsonAsync(Route(first.Member.Id), firstUpdate),
            host.Owner.PutAsJsonAsync(Route(second.Member.Id), secondUpdate));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.BadRequest);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    private static string Route(Guid id) => $"api/family/{id}/settings";
    private static async Task<ClientSettingsResponse> Settings(HttpClient http, Guid id)
        => (await http.GetFromJsonAsync<ClientSettingsResponse>(Route(id)))!;
    private static async Task<FamilyInvitation> Invite(HttpClient http, string name, string? token)
    {
        using var response = await http.PostAsJsonAsync("api/family", new AddFamilyMember(name, token));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FamilyInvitation>())!;
    }
    private static void AssertNoSecrets(string body, params string[] secrets)
    {
        foreach (var secret in secrets) Assert.DoesNotContain(secret, body);
        Assert.DoesNotContain("pushWardToken", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pushWardApiToken", body, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestFiles : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WaterReminderTests", Guid.NewGuid().ToString("N"));
        public StorageService MemberStorage(Guid id) => new(System.IO.Path.Combine(Path, "members", id.ToString("N")));
        public string MemberSettingsPath(Guid id) => System.IO.Path.Combine(Path, "members", id.ToString("N"), "settings.json");
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ReminderEngine _engine;
        public HttpClient Owner { get; }
        private Host(WebApplication app, ReminderEngine engine) { _app = app; _engine = engine; Owner = Client(OwnerCode); }
        public HttpClient Client(string? token)
        {
            var http = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
            if (token is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return http;
        }
        public static async Task<Host> Start(string directory)
        {
            var owner = new ReminderEngine(new ReminderEngineOptions { DataDirectory = directory, PushWardToken = () => OwnerPush, PollAnswers = false });
            var app = ServerHost.Build(ServerOptions.Create(directory, OwnerCode, 0, "127.0.0.1", true, false), owner);
            await app.StartAsync();
            return new(app, owner);
        }
        public async ValueTask DisposeAsync() { Owner.Dispose(); await _app.StopAsync(); await _app.DisposeAsync(); _engine.Dispose(); }
    }
}
