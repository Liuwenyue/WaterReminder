using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using WaterReminder.Server;
using WaterReminder.Services;
using Xunit;

namespace WaterReminder.Tests;

public sealed class FamilySyncTests
{
    private const string OwnerCode = "test-family-sync-owner";

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
        public static async Task<Host> Start(string directory)
        {
            var owner = new ReminderEngine(new ReminderEngineOptions { DataDirectory = directory, PollAnswers = false });
            var app = ServerHost.Build(ServerOptions.Create(directory, OwnerCode, 0, "127.0.0.1", true, false), owner);
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
