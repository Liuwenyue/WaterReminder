using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using WaterReminder.Server;
using WaterReminder.Services;
using Xunit;

namespace WaterReminder.Tests;

public sealed class FamilySyncTests
{
    private const string OwnerCode = "test-family-sync-owner";

    /// <summary>Seeds one independently used (non-shared) member with a known access code.</summary>
    internal static (Guid Id, string AccessCode) SeedPrivateMember(string directory, string name = "独立账号")
    {
        var code = "wr_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var id = Guid.NewGuid();
        var accounts = new[]
        {
            new { Id = id, Name = name, CodeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))), SharedWithOwner = false },
        };
        File.WriteAllText(Path.Combine(directory, "family.json"), JsonSerializer.Serialize(accounts));
        return (id, code);
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

        var invitation = await AddMember(host.Owner, "家人甲");
        using var member = host.Client(invitation.AccessCode);
        Assert.Equal(0, (await Records(member, today)).GetProperty("consumedMl").GetInt32());
        await PostSync(member, Operation(today, "drink", amount: 500));
        Assert.Equal(500, (await Records(member, today)).GetProperty("consumedMl").GetInt32());
        Assert.Equal(300, (await Records(host.Owner, today)).GetProperty("consumedMl").GetInt32());

        var rotated = await ResetCode(host.Owner, invitation.Member.Id);
        Assert.Equal(invitation.Member.Id, rotated.Member.Id);
        Assert.NotEqual(invitation.AccessCode, rotated.AccessCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await member.GetAsync("api/status")).StatusCode);
        using var renewed = host.Client(rotated.AccessCode);
        Assert.Equal(500, (await Records(renewed, today)).GetProperty("consumedMl").GetInt32());

        var feedback = await host.Owner.PostAsync($"api/family/{invitation.Member.Id}/push-test", new StringContent("{}"));
        var push = await feedback.Content.ReadFromJsonAsync<MemberPushFeedback>();
        Assert.Equal("unconfigured", push!.State);
        Assert.Equal(500, (await Records(renewed, today)).GetProperty("consumedMl").GetInt32());
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await renewed.GetAsync("api/family/progress")).StatusCode);
    }

    [Fact]
    public void SyncDrinkWithoutSourceDefaultsToDesktop()
    {
        // In-process (no HTTP): the sandbox blocks all TCP, so the loopback
        // harness cannot run here. The default-source logic lives in
        // ReminderEngine.TrySyncOperations and is fully covered this way.
        using var files = new TestFiles();
        using var engine = new ReminderEngine(new ReminderEngineOptions
        {
            DataDirectory = files.Path, Clock = () => new DateTime(2026, 11, 19, 10, 0, 0), PollAnswers = false,
        });
        const string today = "2026-11-19";
        var drink = new SyncOperation { Id = Guid.NewGuid(), Date = today, Kind = "drink", AmountMl = 250, At = today + "T09:30:00" };
        Assert.True(engine.TrySyncOperations(new() { Operations = [drink] }, out _, out _, out _));
        var recorded = Assert.Single(engine.RecordsFor(today)!.Events);
        Assert.Equal("desktop", recorded.Source);
    }

    [Fact]
    public async Task ResetAccessCode_ReturnsNewCode_InvalidatesOldCode_AndKeepsMemberData()
    {
        using var files = new TestFiles();
        await using var host = await Host.Start(files.Path);
        var invitation = await AddMember(host.Owner, "家人甲");
        using var member = host.Client(invitation.AccessCode);
        var today = DateOnly.FromDateTime(ShanghaiClock.Now()).ToString("yyyy-MM-dd");
        await PostSync(member, Operation(today, "drink", amount: 500));

        using var denied = await member.PostAsync($"api/family/{invitation.Member.Id}/reset-code", null);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var missing = await host.Owner.PostAsync($"api/family/{Guid.NewGuid()}/reset-code", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var rotated = await ResetCode(host.Owner, invitation.Member.Id);
        Assert.Equal(invitation.Member.Id, rotated.Member.Id);
        Assert.Equal("家人甲", rotated.Member.Name);
        Assert.NotEqual(invitation.AccessCode, rotated.AccessCode);
        Assert.StartsWith("wr_", rotated.AccessCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("api/status")).StatusCode);
        using var renewed = host.Client(rotated.AccessCode);
        Assert.Equal(500, (await Records(renewed, today)).GetProperty("consumedMl").GetInt32());

        using var archived = await host.Owner.DeleteAsync($"api/family/{invitation.Member.Id}");
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        using var stopped = await host.Owner.PostAsync($"api/family/{invitation.Member.Id}/reset-code", null);
        Assert.Equal(HttpStatusCode.NotFound, stopped.StatusCode);
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
        var yesterdayFile = Path.Combine(files.Path, "records", yesterday + ".json");
        var todayFile = Path.Combine(files.Path, "records", today + ".json");
        Assert.DoesNotContain(yesterdayDrink.Id.ToString(), File.ReadAllText(todayFile + ".bak"));
        var beforeYesterday = File.ReadAllBytes(yesterdayFile);
        var beforeToday = File.ReadAllBytes(todayFile);
        var clear = new SyncOperation { Id = Guid.NewGuid(), Kind = "clear", Date = yesterday };
        todayDrink.AmountMl++;
        Assert.False(engine.TrySyncOperations(new() { Operations = [clear, todayDrink] }, out _, out _, out var conflict));
        Assert.True(conflict);
        Assert.Equal(beforeYesterday, File.ReadAllBytes(yesterdayFile));
        Assert.Equal(beforeToday, File.ReadAllBytes(todayFile));
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
        storage.SaveDailyRecord(new DailyRecord { Date = "2026-11-18", Events = [new WaterEvent { AmountMl = 500 }] });
        storage.SaveDailyRecord(new DailyRecord { Date = "2026-11-19", Events = [new WaterEvent { AmountMl = 100 }] });
        var path = Path.Combine(files.Path, "records", "2026-11-18.json");
        File.WriteAllText(path, "{");
        Assert.Equal(240, storage.LoadDailyRecord(new DateOnly(2026, 11, 18)).ConsumedMl);
        Assert.Equal(100, storage.LoadDailyRecord(new DateOnly(2026, 11, 19)).ConsumedMl);
        Assert.Equal(240, storage.ListDailyRecords().Single(day => day.Date == "2026-11-18").ConsumedMl);
        Assert.Equal(File.ReadAllText(path + ".bak"), File.ReadAllText(path));
        File.WriteAllText(path, "{");
        File.WriteAllText(path + ".bak", "{");
        Assert.ThrowsAny<JsonException>(() => storage.LoadDailyRecord(new DateOnly(2026, 11, 18)));
        Assert.Equal("{", File.ReadAllText(path));
        Assert.Equal("{", File.ReadAllText(path + ".bak"));
        // Damage is per-day now: other days can still be saved and read.
        storage.SaveDailyRecord(new DailyRecord { Date = "2026-11-19", Events = [new WaterEvent { AmountMl = 200 }] });
        Assert.Equal(200, storage.LoadDailyRecord(new DateOnly(2026, 11, 19)).ConsumedMl);
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

    private static async Task<FamilyInvitation> AddMember(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("api/family", new AddFamilyMember(name, null));
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
            var http = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
            if (token is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return http;
        }
        public static async Task<Host> Start(string directory)
        {
            var owner = new ReminderEngine(new ReminderEngineOptions { DataDirectory = directory, PollAnswers = false });
            var options = ServerOptions.Create(directory, OwnerCode, 0, "127.0.0.1", true, false);
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
