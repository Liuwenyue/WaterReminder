using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using WaterReminder.Server;
using WaterReminder.Services;
using Xunit;

namespace WaterReminder.Tests;

public sealed class FamilyAndPhoneTests
{
    [Fact]
    public async Task Family_SeparatesRecordsAcrossConcurrentClients_AndKeepsStoppedHistoryOnRestart()
    {
        using var files = new TempData();
        var ownerDate = ShanghaiClock.Now();
        var yesterday = DateOnly.FromDateTime(ownerDate).AddDays(-1);
        files.Storage.SaveDailyRecord(new DailyRecord
        {
            Date = yesterday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Events = [new WaterEvent { AmountMl = 900, At = yesterday.ToDateTime(new TimeOnly(10, 0)) }],
        });
        const string ownerToken = "family-test-owner";
        var options = ServerOptions.Create(files.Path, ownerToken, 0, "127.0.0.1", true, false);
        using var owner = Engine(files, () => ownerDate);
        FamilyInvitation first;
        FamilyInvitation second;
        await using (var app = await StartAsync(options, owner))
        {
            var url = app.Urls.Single();
            using var admin = new ReminderApiClient(url, ownerToken);
            Assert.Contains((await admin.GetHistoryAsync()).Value!.Days, day => day.ConsumedMl == 900);
            first = Assert.IsType<FamilyInvitation>((await admin.AddFamilyMemberAsync(new("家人甲", null))).Value);
            second = Assert.IsType<FamilyInvitation>((await admin.AddFamilyMemberAsync(new("家人乙", null))).Value);
            Assert.NotEqual(first.AccessCode, second.AccessCode);

            using var a = new ReminderApiClient(url, first.AccessCode);
            using var b = new ReminderApiClient(url, second.AccessCode);
            using var raw = new HttpClient { BaseAddress = new Uri(url) };
            raw.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.AccessCode);
            Assert.Equal(first.Member.Id, Assert.Single((await a.GetFamilyAsync()).Value!.Members).Id);
            using var denied = await raw.GetAsync($"/api/family/{second.Member.Id}/history");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var deniedArchive = await raw.DeleteAsync($"/api/family/{second.Member.Id}");
            Assert.Equal(HttpStatusCode.Forbidden, deniedArchive.StatusCode);
            Assert.False((await a.AddFamilyMemberAsync(new("无权限", null))).Ok);
            var writes = await Task.WhenAll(a.LogAmountAsync(123), b.LogAmountAsync(456), a.LogAmountAsync(99));
            Assert.All(writes, write => Assert.True(write.Ok));
            Assert.Equal(222, (await a.GetRecordsAsync()).Value!.ConsumedMl);
            Assert.Equal(456, (await b.GetRecordsAsync()).Value!.ConsumedMl);
            Assert.Equal(0, (await admin.GetRecordsAsync()).Value!.ConsumedMl);
            Assert.Equal(222, Assert.Single((await admin.GetFamilyHistoryAsync(first.Member.Id, 30)).Value!.Days).ConsumedMl);
            var date = (await a.GetRecordsAsync()).Value!.Date;
            var other = (await admin.GetFamilyRecordsAsync(first.Member.Id, date)).Value!;
            Assert.Equal(222, other.ConsumedMl);
            Assert.True(other.Editable);
            Assert.True(await admin.ArchiveFamilyMemberAsync(first.Member.Id));
            using var stopped = await raw.GetAsync("/api/status");
            Assert.Equal(HttpStatusCode.Unauthorized, stopped.StatusCode);
            Assert.False((await a.SetTodayTotalAsync(888)).Ok);
            await app.StopAsync();
        }
        using var restarted = Engine(files, () => ownerDate);
        await using var after = await StartAsync(options, restarted);
        using var ownerAfter = new ReminderApiClient(after.Urls.Single(), ownerToken);
        using var memberAfter = new ReminderApiClient(after.Urls.Single(), second.AccessCode);
        var archived = (await ownerAfter.GetFamilyAsync()).Value!.Members.Single(member => member.Id == first.Member.Id);
        Assert.True(archived.Archived);
        Assert.Equal(222, Assert.Single((await ownerAfter.GetFamilyHistoryAsync(first.Member.Id, 30)).Value!.Days).ConsumedMl);
        Assert.Equal(456, (await memberAfter.GetRecordsAsync()).Value!.ConsumedMl);
        Assert.DoesNotContain(first.AccessCode, File.ReadAllText(Path.Combine(files.Path, "family.json")));
        await after.StopAsync();
    }

    [Fact]
    public async Task Phone_ConsumesTextViaDocumentedStream_AfterRestart()
    {
        using var files = new TempData();
        using var fake = new PushInbox();
        using var http = new HttpClient(fake);
        var now = new DateTime(2026, 11, 3, 10, 0, 0);
        const string token = "test-only-phone-key";
        using (var first = Engine(files, () => now, http, token))
            await first.TickAsync();
        Assert.Contains("\"text_input\":true", fake.Reminder);
        Assert.Contains("\"id\":\"drink-", fake.Reminder);
        Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(files.Path, "phone-replies.json")));
        var action = PushWardMessageBuilder.BuildTotalAction(now).Id;
        fake.Answers = $$"""
            {"cursor":"begin+next", "items":[
              {"notification_id":999, "action_id":"{{action}}", "text":"8888"},
              {"notification_id":1234, "action_id":"{{action}}", "text":"1200"},
              {"notification_id":1234, "action_id":"drink"}
            ]}
            """;
        using var restart = Engine(files, () => now, http, token, poll: true);
        restart.LogDrink(300, "desktop");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var confirmation = await fake.Confirmations.Reader.ReadAsync(timeout.Token);
        Assert.Contains("今日已喝总量为 1200 ml", confirmation);
        Assert.DoesNotContain("\"actions\"", confirmation);
        var records = restart.TodayRecords();
        Assert.Equal(1200, records.ConsumedMl);
        Assert.Contains(records.Events, item => item.Source == "adjust" && item.AmountMl == 900);
        Assert.Contains("after=begin%2B001", fake.FirstGet);
    }

    [Fact]
    public void Phone_RejectsInvalidAndExpiredReplies_AndNeverReplaysAnAppliedAdjustment()
    {
        using var files = new TempData();
        using var fake = new PushInbox();
        using var http = new HttpClient(fake);
        var now = new DateTime(2026, 11, 3, 10, 0, 0);
        const string token = "test-only-phone-key";
        string path = Path.Combine(files.Path, "phone-replies.json");
        string before;
        var action = PushWardMessageBuilder.BuildTotalAction(now).Id;
        using (var engine = Engine(files, () => now, http, token))
        {
            var replies = (IPushWardReplies)engine;
            replies.Track(token, "valid", "start", now);
            replies.Track(token, "invalid", null, now);
            replies.Track(token, "expired", null, now.AddDays(-1));
            before = File.ReadAllText(path);
            Assert.True(replies.Accept(token, "start", new("next", [
                new("invalid", action, "中文"), new("valid", action, "1200"),
                new("expired", "drink")])));
            Assert.Single(engine.TodayRecords().Events);
            Assert.Contains("未修改", ReadConfirmation(fake));
            Assert.Contains("1200", ReadConfirmation(fake));
            Assert.Contains("已过期", ReadConfirmation(fake));
            engine.LogDrink(200, "desktop");
        }
        // Simulate an interruption after committing the daily record and before saving the cursor.
        File.WriteAllText(path, before);
        using var restart = Engine(files, () => now, http, token);
        Assert.True(((IPushWardReplies)restart).Accept(token, "start", new("next", [new("valid", action, "1200")])));
        Assert.Equal(1400, restart.TodayRecords().ConsumedMl);
        Assert.Equal(2, restart.TodayRecords().Events.Count);

        restart.Retire();
        Assert.Throws<ObjectDisposedException>(() => restart.LogDrink(200, "desktop"));
        Assert.False(((IPushWardReplies)restart).Accept(token, "next", new("later", [new("invalid", action, "900")])));
        Assert.Equal(1400, restart.TodayRecords().ConsumedMl);
    }

    [Theory]
    [InlineData(10, 19, 370)]
    [InlineData(20, 21, 800)]
    public async Task Phone_DelayedTapUsesDisplayedCupAfterSettingsChangeAndRestart(int sentHour, int tappedHour, int expected)
    {
        using var files = new TempData();
        files.Storage.SaveSettings(new UserSettings { DailyGoalMl = 3000, WorkCupCapacityMl = 370, HomeCupCapacityMl = 800 });
        using var fake = new PushInbox();
        using var http = new HttpClient(fake);
        var now = new DateTime(2026, 11, 3, sentHour, 0, 0);
        const string token = "test-only-phone-key";
        using (var first = Engine(files, () => now, http, token)) await first.TickAsync();
        using var payload = JsonDocument.Parse(fake.Reminder!);
        var actions = payload.RootElement.GetProperty("actions");
        Assert.Equal(2, actions.GetArrayLength());
        Assert.Equal($"已喝水 · {expected} ml", actions[0].GetProperty("title").GetString());
        var action = actions[0].GetProperty("id").GetString()!;
        files.Storage.SaveSettings(new UserSettings { DailyGoalMl = 3000, WorkCupCapacityMl = 999, HomeCupCapacityMl = 999 });
        now = now.Date.AddHours(tappedHour);
        using var restart = Engine(files, () => now, http, token);
        Assert.True(((IPushWardReplies)restart).Accept(token, "begin+001", new("next", [new("1234", action)])));
        Assert.Equal(expected, restart.TodayRecords().ConsumedMl);
        Assert.Contains($"已记 {expected} ml", ReadConfirmation(fake));
        Assert.True(((IPushWardReplies)restart).Accept(token, "next", new("later", [new("1234", action)])));
        Assert.Equal(expected, restart.TodayRecords().ConsumedMl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Phone_RemainingAmountCapsBothDisplayedAndDelayedTap(bool consumedBeforeSend)
    {
        using var files = new TempData();
        files.Storage.SaveSettings(new UserSettings { DailyGoalMl = 3000, WorkCupCapacityMl = 370, HomeCupCapacityMl = 800 });
        using var fake = new PushInbox();
        using var http = new HttpClient(fake);
        var now = new DateTime(2026, 11, 3, 10, 0, 0);
        const string token = "test-only-phone-key";
        using var engine = Engine(files, () => now, http, token);
        if (consumedBeforeSend) engine.LogDrink(2950, "desktop");
        var before = engine.Status().NextReminder;
        Assert.True(await engine.SendTestPushAsync());
        Assert.Equal(before, engine.Status().NextReminder);
        using var payload = JsonDocument.Parse(fake.Reminder!);
        var actions = payload.RootElement.GetProperty("actions");
        Assert.Equal(2, actions.GetArrayLength());
        Assert.Equal($"已喝水 · {(consumedBeforeSend ? 50 : 370)} ml", actions[0].GetProperty("title").GetString());
        if (!consumedBeforeSend) engine.LogDrink(2950, "desktop");
        Assert.True(((IPushWardReplies)engine).Accept(token, "begin+001", new("next", [
            new("1234", actions[0].GetProperty("id").GetString()!)])));
        Assert.Equal(3000, engine.TodayRecords().ConsumedMl);
        Assert.Contains("已记 50 ml", ReadConfirmation(fake));
    }

    [Fact]
    public async Task Phone_LegacyDrinkRemainsAccepted_AndTestRespectsDisabledPush()
    {
        using var files = new TempData();
        files.Storage.SaveSettings(new UserSettings { DailyGoalMl = 3000, WorkCupCapacityMl = 370, HomeCupCapacityMl = 800 });
        using var fake = new PushInbox();
        using var http = new HttpClient(fake);
        var now = new DateTime(2026, 11, 3, 10, 0, 0);
        const string token = "test-only-phone-key";
        using var engine = Engine(files, () => now, http, token);
        var replies = (IPushWardReplies)engine;
        replies.Track(token, "legacy", "start", now);
        now = now.Date.AddHours(19);
        Assert.True(replies.Accept(token, "start", new("next", [new("legacy", "drink")])));
        Assert.Equal(800, engine.TodayRecords().ConsumedMl);
        files.Storage.SaveSettings(new UserSettings { MobilePushEnabled = false });
        Assert.False(await engine.SendTestPushAsync());
        Assert.Null(fake.Reminder);
    }

    private static string ReadConfirmation(PushInbox fake)
    {
        Assert.True(fake.Confirmations.Reader.TryRead(out var message));
        return message!;
    }

    private static ReminderEngine Engine(TempData data, Func<DateTime> now, HttpClient? http = null,
        string? token = null, bool poll = false) => new(new ReminderEngineOptions
        {
            DataDirectory = data.Path,
            Clock = now,
            Http = http,
            PushWardToken = () => token,
            PollAnswers = poll,
        });

    private static async Task<WebApplication> StartAsync(ServerOptions options, ReminderEngine engine)
    {
        var app = ServerHost.Build(options, engine);
        await app.StartAsync();
        return app;
    }

    private sealed class PushInbox : HttpMessageHandler
    {
        public string? Reminder { get; private set; }
        public string? FirstGet { get; private set; }
        public string? Answers { get; set; }
        public Channel<string> Confirmations { get; } = Channel.CreateUnbounded<string>();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (request.Method == HttpMethod.Get)
            {
                FirstGet ??= request.RequestUri!.AbsoluteUri;
                return Json(HttpStatusCode.OK, Answers ?? """{"cursor":"begin+001","items":[]}""");
            }
            var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("actions", out _)) Reminder = payload;
            else Confirmations.Writer.TryWrite(payload);
            return Json(HttpStatusCode.Created, """{"id":1234,"answerable":true,"answer_cursor":"begin+001"}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
        {
            Content = new StringContent(json),
        };
    }

    private sealed class TempData : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WaterReminderTests", Guid.NewGuid().ToString("N"));
        public StorageService Storage => new(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
