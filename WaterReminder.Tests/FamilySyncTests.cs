using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebUtilities;
using WaterReminder.Server;
using WaterReminder.Services;
using Xunit;

namespace WaterReminder.Tests;

public sealed class FamilySyncTests
{
    private const string OwnerCode = "test-family-sync-owner";

    [Theory]
    [InlineData("device")]
    [InlineData("wechat")]
    public async Task LoginWithExpectedMember_RejectsUnknownAndOtherIdentitiesWithoutWritingOrDisclosingAccounts(string method)
    {
        using var files = new TestFiles();
        using var handler = new LoginIdentityHandler();
        using var weChat = new WeChatClient(new WeChatConfig { AppId = "test-app", AppSecret = "test-secret" }, handler);
        await using var host = await Host.Start(files.Path, weChat);
        var route = $"api/{method}/login";
        var identityField = method == "wechat" ? "code" : "deviceId";
        var original = new string('a', 64);
        var other = new string('b', 64);
        var unknown = new string('c', 64);
        using var firstLogin = await host.Anonymous.PostAsJsonAsync(route, new Dictionary<string, string> { [identityField] = original });
        firstLogin.EnsureSuccessStatusCode();
        var memberId = (await firstLogin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("memberId").GetGuid();
        using var otherLogin = await host.Anonymous.PostAsJsonAsync(route, new Dictionary<string, string> { [identityField] = other });
        otherLogin.EnsureSuccessStatusCode();
        var otherId = (await otherLogin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("memberId").GetGuid();
        Assert.NotEqual(memberId, otherId);

        var before = SnapshotFiles();
        Assert.Equal(2, JsonDocument.Parse(File.ReadAllBytes(Path.Combine(files.Path, "family.json"))).RootElement.GetArrayLength());
        var errorMessages = new List<string>();
        foreach (var identity in new[] { unknown, other })
        {
            using var result = await host.Anonymous.PostAsJsonAsync(route, new Dictionary<string, object>
            {
                [identityField] = identity,
                ["expectedMemberId"] = memberId,
            });
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
            var body = await result.Content.ReadFromJsonAsync<JsonElement>();
            var error = Assert.Single(body.EnumerateObject());
            Assert.Equal("error", error.Name);
            var message = error.Value.GetString()!;
            Assert.DoesNotContain(memberId.ToString(), message);
            Assert.DoesNotContain(otherId.ToString(), message);
            errorMessages.Add(message);
            Assert.Equal(before, SnapshotFiles());
        }
        Assert.Equal(errorMessages[0], errorMessages[1]);

        using var renewed = await host.Anonymous.PostAsJsonAsync(route, new Dictionary<string, object>
        {
            [identityField] = original,
            ["expectedMemberId"] = memberId,
        });
        renewed.EnsureSuccessStatusCode();
        var account = await renewed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(memberId, account.GetProperty("memberId").GetGuid());
        using var client = host.Client(account.GetProperty("accessCode").GetString());
        using var status = await client.GetAsync("api/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);

        (string Path, string Contents, DateTime Modified)[] SnapshotFiles() =>
            Directory.GetFiles(files.Path, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => (path, Convert.ToBase64String(File.ReadAllBytes(path)), File.GetLastWriteTimeUtc(path)))
                .ToArray();
    }

    [Fact]
    public async Task SyncedDrinkIsShared_RetryIsIdempotent_AndMembersStaySeparate()
    {
        using var files = new TestFiles();
        await using var host = await Host.Start(files.Path);
        var today = DateOnly.FromDateTime(ShanghaiClock.Now()).ToString("yyyy-MM-dd");
        var drink = Operation(today, "drink", amount: 300);
        var first = await PostSync(host.Owner, drink);
        Assert.True(first.GetProperty("results")[0].GetProperty("changed").GetBoolean());
        Assert.Equal(300, (await Records(host.Owner, today)).GetProperty("consumedMl").GetInt32());

        var replay = await PostSync(host.Owner, drink, expectOk: true);
        Assert.True(replay.GetProperty("results")[0].GetProperty("duplicate").GetBoolean());
        Assert.Single((await Records(host.Owner, today)).GetProperty("events").EnumerateArray());

        drink.AmountMl = 450;
        var conflict = await host.Owner.PostAsJsonAsync("api/sync/operations", new SyncOperationsRequest { Operations = [drink] });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(300, (await Records(host.Owner, today)).GetProperty("consumedMl").GetInt32());

        var broken = new SyncOperationsRequest
        {
            Operations =
            [
                Operation(today, "drink", amount: 100),
                new SyncOperation { Id = drink.Id, Date = today, Kind = "drink", AmountMl = 200, Source = "cup" },
            ],
        };
        var rejected = await host.Owner.PostAsJsonAsync("api/sync/operations", broken);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal(300, (await Records(host.Owner, today)).GetProperty("consumedMl").GetInt32());

        var invitation = await Invite(host.Owner, "家人甲", "https://cup.example");
        var preview = await host.Anonymous.PostAsJsonAsync("api/invitations/preview", new InvitationTokenRequest(invitation.InviteToken));
        Assert.Equal("家人甲", (await preview.Content.ReadFromJsonAsync<InvitationPreview>())!.Member.Name);
        var redeemed = await Redeem(host.Anonymous, invitation.InviteToken);
        using var member = host.Client(redeemed.AccessCode);
        Assert.Equal(0, (await Records(member, today)).GetProperty("consumedMl").GetInt32());
        await PostSync(member, Operation(today, "drink", amount: 500));
        Assert.Equal(500, (await Records(member, today)).GetProperty("consumedMl").GetInt32());
        Assert.Equal(300, (await Records(host.Owner, today)).GetProperty("consumedMl").GetInt32());

        var again = await Invite(host.Owner, null, "https://cup.example", redeemed.Member.Id);
        var rotated = await Redeem(host.Anonymous, again.InviteToken);
        Assert.NotEqual(redeemed.AccessCode, rotated.AccessCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await member.GetAsync("api/status")).StatusCode);
        using var renewed = host.Client(rotated.AccessCode);
        Assert.Equal(500, (await Records(renewed, today)).GetProperty("consumedMl").GetInt32());
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await host.Anonymous.PostAsJsonAsync("api/invitations/preview", new InvitationTokenRequest(again.InviteToken))).StatusCode);

        var feedback = await host.Owner.PostAsync($"api/family/{redeemed.Member.Id}/push-test", new StringContent("{}"));
        var push = await feedback.Content.ReadFromJsonAsync<MemberPushFeedback>();
        Assert.Equal("unconfigured", push!.State);
        Assert.Equal(500, (await Records(renewed, today)).GetProperty("consumedMl").GetInt32());
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await renewed.GetAsync("api/family/progress")).StatusCode);
    }

    [Fact]
    public void BatchAcrossMidnightIsAtomicAndSnapshotIncludesEmptiedDays()
    {
        using var files = new TestFiles();
        using var engine = new ReminderEngine(new ReminderEngineOptions
        {
            DataDirectory = files.Path, Clock = () => new DateTime(2026, 11, 19, 10, 0, 0), PollAnswers = false,
        });
        const string today = "2026-11-19", yesterday = "2026-11-18";
        var yesterdayDrink = Operation(yesterday, "drink", 3500);
        var todayDrink = Operation(today, "drink", 250);
        var stalePause = new SyncOperation { Id = Guid.NewGuid(), Kind = "pause", Date = yesterday };
        var batch = new SyncOperationsRequest { Operations = [yesterdayDrink, stalePause, todayDrink] };
        Assert.True(engine.TrySyncOperations(batch, out var applied, out _, out _));
        Assert.False(applied!.Status.RemindersPaused);
        var file = Path.Combine(files.Path, "daily-records.json");
        Assert.DoesNotContain(yesterdayDrink.Id.ToString(), File.ReadAllText(file + ".bak"));
        var before = File.ReadAllBytes(file);
        var clear = new SyncOperation { Id = Guid.NewGuid(), Kind = "clear", Date = yesterday };
        todayDrink.AmountMl++;
        Assert.False(engine.TrySyncOperations(new() { Operations = [clear, todayDrink] }, out _, out _, out var conflict));
        Assert.True(conflict);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.True(engine.TrySyncOperations(new() { Operations = [clear] }, out _, out _, out _));
        var snapshot = engine.SyncSnapshot(Guid.Empty, "我", 31);
        Assert.Equal(31, snapshot.Days.Count);
        Assert.Empty(snapshot.Days.Single(day => day.Date == yesterday).Events);
        Assert.Equal(250, snapshot.Days[0].ConsumedMl);
    }

    [Fact]
    public void DamagedLedgerRecoversFromBackupOrPreservesUnreadableFiles()
    {
        using var files = new TestFiles();
        var storage = new StorageService(files.Path);
        var first = new DailyRecord { Date = "2026-11-18", Events = [new WaterEvent { AmountMl = 240 }] };
        storage.SaveDailyRecord(first);
        storage.SaveDailyRecord(new DailyRecord { Date = "2026-11-19" });
        var path = Path.Combine(files.Path, "daily-records.json");
        File.WriteAllText(path, "{");
        Assert.Equal(240, Assert.Single(storage.ListDailyRecords()).ConsumedMl);
        Assert.Equal(File.ReadAllText(path + ".bak"), File.ReadAllText(path));
        File.WriteAllText(path, "{");
        File.WriteAllText(path + ".bak", "{");
        Assert.ThrowsAny<JsonException>(() => storage.SaveDailyRecord(first));
        Assert.Equal("{", File.ReadAllText(path));
    }

    [Fact]
    public void SettingsAndPauseOnlyChangeTheirOwnFieldsAndStaleDraftsAreRejected()
    {
        using var files = new TestFiles();
        using var engine = new ReminderEngine(new ReminderEngineOptions
        {
            DataDirectory = files.Path, Clock = () => new DateTime(2026, 11, 19, 10, 0, 0), PollAnswers = false,
        });
        var original = engine.ClientSettings();
        var edit = SettingsEditor.FromResponse(original);
        edit.ExpectedRevision = original.Revision;
        edit.PreservePause = true;
        edit.DailyGoalMl = 1800;
        var pause = new SyncOperation { Id = Guid.NewGuid(), Kind = "pause", Date = "2026-11-19" };
        Assert.True(engine.TrySyncOperations(new() { Operations = [pause] }, out _, out _, out _));
        Assert.True(engine.TryUpdateSettings(edit, out _));
        Assert.True(engine.Status().RemindersPaused);
        Assert.False(engine.TryUpdateSettings(edit, out var stale));
        Assert.Equal(SettingsEditor.StaleSettings, stale);
        var resume = SettingsEditor.FromResponse(original);
        resume.PauseOnly = true;
        Assert.True(engine.TryUpdateSettings(resume, out _));
        Assert.False(engine.Status().RemindersPaused);
        Assert.Equal(1800, engine.ClientSettings().DailyGoalMl);
    }

    [Fact]
    public void ChangingTodaysGoalDoesNotRewriteYesterday()
    {
        using var files = new TestFiles();
        var storage = new StorageService(files.Path);
        storage.SaveDailyRecord(new DailyRecord
        {
            Date = "2026-11-18",
            Events = [new WaterEvent { AmountMl = 1800, At = new DateTime(2026, 11, 18, 12, 0, 0), Source = "cup" }],
        });
        using var engine = new ReminderEngine(new ReminderEngineOptions
        {
            DataDirectory = files.Path,
            Clock = () => new DateTime(2026, 11, 19, 10, 0, 0),
            PollAnswers = false,
        });
        var before = engine.History(7).Days.Single();
        Assert.Equal("2026-11-18", before.Date);
        Assert.False(before.GoalKnown);
        Assert.Equal(1800, before.ConsumedMl);

        var update = SettingsEditor.FromResponse(engine.ClientSettings());
        update.DailyGoalMl = 2400;
        Assert.True(engine.TryUpdateSettings(update, out _));
        var after = engine.History(7).Days.Single();
        Assert.False(after.GoalKnown);
        Assert.Equal(1800, after.ConsumedMl);
        var today = engine.RecordsFor("2026-11-19");
        Assert.NotNull(today);
        Assert.True(today!.GoalKnown);
        Assert.Equal(2400, today.GoalMl);
        Assert.Empty(today.Events);
    }

    [Fact]
    public async Task OldFamilyRegistryStillLoads()
    {
        using var files = new TestFiles();
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(files.Path, "family.json"),
            $$"""[{"Id":"{{id}}","Name":"旧成员","CodeHash":"{{new string('a', 64)}}","Archived":false}]""");
        await using var host = await Host.Start(files.Path);
        var overview = await host.Owner.GetFromJsonAsync<FamilyOverview>("api/family");
        Assert.Contains(overview!.Members, member => member.Id == id && member.Name == "旧成员");
    }

    private static SyncOperation Operation(string date, string kind, int amount)
        => new() { Id = Guid.NewGuid(), Date = date, Kind = kind, AmountMl = amount, Source = "cup", At = date + "T09:30:00" };

    private static async Task<JsonElement> PostSync(HttpClient client, SyncOperation operation, bool expectOk = true)
    {
        using var response = await client.PostAsJsonAsync("api/sync/operations", new SyncOperationsRequest { Operations = [operation] });
        if (expectOk) response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> Records(HttpClient client, string date)
    {
        using var response = await client.GetAsync("api/records?date=" + date);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<MemberInvite> Invite(HttpClient client, string? name, string serverUrl, Guid? memberId = null)
    {
        using var response = await client.PostAsJsonAsync("api/family/invitations", new CreateMemberInvite(name, memberId, serverUrl));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MemberInvite>())!;
    }

    private static async Task<RedeemedInvitation> Redeem(HttpClient client, string token)
    {
        using var response = await client.PostAsJsonAsync("api/invitations/redeem", new InvitationTokenRequest(token));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RedeemedInvitation>())!;
    }

    private sealed class TestFiles : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WaterReminderTests", Guid.NewGuid().ToString("N"));
        public TestFiles() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }

    private sealed class LoginIdentityHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var code = QueryHelpers.ParseQuery(request.RequestUri!.Query)["js_code"].ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { openid = "test-openid-" + code[..8] }),
            });
        }
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
            var http = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
            if (token is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return http;
        }
        public static async Task<Host> Start(string directory, WeChatClient? weChat = null)
        {
            var owner = new ReminderEngine(new ReminderEngineOptions { DataDirectory = directory, PollAnswers = false });
            var options = ServerOptions.Create(directory, OwnerCode, 0, "127.0.0.1", true, false);
            if (weChat is not null) options.WeChat = weChat;
            var app = ServerHost.Build(options, owner);
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
