using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using WaterReminder;
using WaterReminder.Server;
using WaterReminder.Services;
using Xunit;

namespace WaterReminder.Tests;

/// <summary>
/// Headless server, Shanghai schedule, and the thin client contract.
/// No WPF, no network, no real tokens.
/// </summary>
public sealed class ServerSplitTests
{
    private static readonly DateTime Tuesday = new(2026, 11, 3);

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body, string? Authorization);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<RecordedRequest> _requests = [];
        public Func<RecordedRequest, (HttpStatusCode Status, string Body)> Respond { get; set; }
            = _ => (HttpStatusCode.Created, """{"id":"n1"}""");

        public IReadOnlyList<RecordedRequest> Requests
        {
            get { lock (_gate) return _requests.ToArray(); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new RecordedRequest(
                request.Method,
                request.RequestUri!,
                body,
                request.Headers.Authorization?.Parameter);
            lock (_gate) _requests.Add(recorded);
            var (status, responseBody) = Respond(recorded);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }

    private static string TempDir()
        => Path.Combine(Path.GetTempPath(), "WaterReminderTests", Guid.NewGuid().ToString("N"));

    private static ReminderEngine Engine(string dir, FakeHandler handler, Func<DateTime> clock, string? pushToken = "hlk_test_token")
    {
        var http = new HttpClient(handler);
        return new ReminderEngine(new ReminderEngineOptions
        {
            DataDirectory = dir,
            Clock = clock,
            PushWardToken = () => pushToken,
            Http = http,
            PollAnswers = false,
        });
    }

    [Fact]
    public void ShanghaiClock_IsEightHoursAheadOfUtc()
    {
        var utc = new DateTime(2026, 11, 11, 1, 45, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 11, 11, 9, 45, 0), ShanghaiClock.FromUtc(utc));
        Assert.Equal(DateTimeKind.Unspecified, ShanghaiClock.FromUtc(utc).Kind);
    }

    [Fact]
    public void EngineWithoutInjectedClock_ReportsShanghaiWallTime()
    {
        var dir = TempDir();
        try
        {
            using var engine = new ReminderEngine(new ReminderEngineOptions
            {
                DataDirectory = dir,
                PollAnswers = false,
            });
            var status = engine.Status();
            Assert.Equal(ShanghaiClock.Id, status.TimeZone);
            var wall = DateTime.Parse(status.Now, CultureInfo.InvariantCulture);
            Assert.InRange((wall - DateTime.UtcNow).TotalHours, 7.5, 8.5);
            Assert.Contains("timezone: Asia/Shanghai", engine.Describe());
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void CanonicalDefaults_KeepTheReminderRules()
    {
        var s = new UserSettings();
        var day = Tuesday;
        Assert.Equal(3000, s.DailyGoalMl);
        Assert.Equal(new TimeSpan(8, 30, 0), s.WorkStart);
        Assert.Equal(new TimeSpan(17, 30, 0), s.WorkEnd);
        Assert.Equal(new TimeSpan(11, 50, 0), s.LunchStart);
        Assert.Equal(new TimeSpan(13, 30, 0), s.LunchEnd);
        Assert.Equal(new TimeSpan(23, 0, 0), s.QuietStart);
        Assert.Equal(370, WaterCalculator.WorkCupMl(s));
        Assert.Equal(800, WaterCalculator.HomeCupMl(s));
        Assert.Equal(TimeSpan.FromMinutes(20), WaterCalculator.MinReminderGap);

        Assert.True(WaterCalculator.ShouldShowDesktop(s, day.AddHours(10)));
        Assert.True(WaterCalculator.ShouldSendMobilePush(s, day.AddHours(10)));

        var lunch = day.AddHours(12).AddMinutes(20);
        Assert.False(WaterCalculator.ShouldShowDesktop(s, lunch));
        Assert.False(WaterCalculator.ShouldSendMobilePush(s, lunch));

        var noDrink = day.AddHours(17).AddMinutes(10);
        Assert.Equal(WaterCalculator.DayPhase.NoDrinkWindow, WaterCalculator.PhaseAt(s, noDrink));
        Assert.False(WaterCalculator.ShouldShowDesktop(s, noDrink));
        Assert.False(WaterCalculator.ShouldSendMobilePush(s, noDrink));

        var evening = day.AddHours(18);
        Assert.False(WaterCalculator.ShouldShowDesktop(s, evening));
        Assert.True(WaterCalculator.ShouldSendMobilePush(s, evening));

        var quiet = day.AddHours(23);
        Assert.False(WaterCalculator.ShouldShowDesktop(s, quiet));
        Assert.False(WaterCalculator.ShouldSendMobilePush(s, quiet));
    }

    [Fact]
    public async Task WorkWindow_PushesAndStagesDesktopToast()
    {
        var dir = TempDir();
        var handler = new FakeHandler();
        var now = Tuesday.AddHours(10);
        try
        {
            using var engine = Engine(dir, handler, () => now);
            await engine.TickAsync();

            var post = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, post.Method);
            Assert.EndsWith("/notifications", post.Uri.AbsolutePath);
            Assert.Equal("hlk_test_token", post.Authorization);
            Assert.Contains("该喝水了", post.Body);
            Assert.Contains("已喝 0/3000 ml，建议 370 ml", post.Body);
            Assert.Contains("\"id\":\"drink\"", post.Body);
            Assert.DoesNotContain("\"url\"", post.Body);
            Assert.DoesNotContain("foreground", post.Body);
            Assert.DoesNotContain("hlk_test_token", post.Body);

            var status = engine.Status();
            Assert.True(status.ShowDesktop);
            Assert.True(status.PushWindow);
            Assert.Equal("Working", status.Phase);
            Assert.StartsWith("下次 ", status.CompactStatusText);
            Assert.EndsWith(" ml", status.CompactStatusText);
            Assert.DoesNotContain("建议", status.CompactStatusText);
            Assert.Contains("建议", status.StatusText);
            Assert.NotNull(status.DesktopAlert);
            var alert = status.DesktopAlert!;
            Assert.Equal("该喝水了", alert.Title);
            Assert.Equal("已喝 0/3000 ml，建议 370 ml", alert.Body);
            Assert.True(DesktopToastPolicy.ShouldToast(status.ShowDesktop, status.Phase, alert.Id, lastShownId: null));
            Assert.False(DesktopToastPolicy.ShouldToast(status.ShowDesktop, status.Phase, alert.Id, alert.Id));

            await engine.TickAsync();
            Assert.Single(handler.Requests);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task AfterWork_PushesWithoutDesktopAlert()
    {
        var dir = TempDir();
        var handler = new FakeHandler();
        var now = FindDue(Tuesday.AddHours(18), Tuesday.AddHours(23));
        try
        {
            using var engine = Engine(dir, handler, () => now);
            await engine.TickAsync();

            Assert.Single(handler.Requests);
            var status = engine.Status();
            Assert.False(status.ShowDesktop);
            Assert.True(status.PushWindow);
            Assert.Equal("AfterWork", status.Phase);
            Assert.Null(status.DesktopAlert);
            Assert.False(DesktopToastPolicy.ShouldToast(true, status.Phase, "stale", null));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task QuietHours_StaySilent()
    {
        var dir = TempDir();
        var handler = new FakeHandler();
        var now = Tuesday.AddHours(23).AddMinutes(5);
        try
        {
            using var engine = Engine(dir, handler, () => now);
            await engine.TickAsync();
            Assert.Empty(handler.Requests);
            var status = engine.Status();
            Assert.False(status.ShowDesktop);
            Assert.False(status.PushWindow);
            Assert.Null(status.DesktopAlert);
            Assert.False(DesktopToastPolicy.ShouldToast(status.ShowDesktop, status.Phase, "x", null));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task PushFailure_DoesNotBurnTheGap_AndRetriesAfterBackoff()
    {
        var dir = TempDir();
        var handler = new FakeHandler { Respond = _ => (HttpStatusCode.BadGateway, "{}") };
        var now = Tuesday.AddHours(10);
        try
        {
            using var engine = Engine(dir, handler, () => now);
            await engine.TickAsync();
            Assert.Single(handler.Requests);

            now = now.AddSeconds(30);
            await engine.TickAsync();
            Assert.Single(handler.Requests);

            now = Tuesday.AddHours(10) + MobilePushDeliveryPolicy.FailureBackoff;
            await engine.TickAsync();
            Assert.Equal(2, handler.Requests.Count);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void PhoneTap_UsesWorkCupDuringWork_AndHomeCupAfter()
    {
        var dir = TempDir();
        var handler = new FakeHandler();
        var now = Tuesday.AddHours(10);
        try
        {
            using var engine = Engine(dir, handler, () => now, pushToken: null);
            Assert.Equal(370, engine.RecordPhoneDrink());
            now = Tuesday.AddHours(19);
            Assert.Equal(800, engine.RecordPhoneDrink());
            Assert.Equal(1170, engine.Status().ConsumedMl);
            Assert.Empty(handler.Requests);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void OverlayToken_IsNotWrittenBackIntoSettings()
    {
        var dir = TempDir();
        try
        {
            var storage = new StorageService(dir);
            var now = Tuesday.AddHours(10);
            storage.SaveSettings(new UserSettings
            {
                RemindersPaused = true,
                PauseUntil = now.AddMinutes(-1),
                PushWardApiToken = "",
            });

            using var engine = new ReminderEngine(new ReminderEngineOptions
            {
                DataDirectory = dir,
                Clock = () => now,
                PushWardToken = () => "hlk_from_env",
                PollAnswers = false,
            });

            var status = engine.Status();
            Assert.False(status.RemindersPaused);
            var saved = File.ReadAllText(Path.Combine(dir, "settings.json"));
            Assert.DoesNotContain("hlk_from_env", saved);
            Assert.DoesNotContain("hlk_", saved);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task HttpApi_RequiresToken_AndDoesNotLeakPushWard()
    {
        var dir = TempDir();
        var handler = new FakeHandler();
        var now = Tuesday.AddHours(10);
        const string apiToken = "desktop-api-token";
        const string pushToken = "hlk_server_only";
        WebApplication? app = null;
        try
        {
            var engine = Engine(dir, handler, () => now, pushToken);
            await engine.TickAsync();

            Assert.Equal(8787, ServerOptions.DefaultPort);
            var options = ServerOptions.Create(dir, apiToken, port: 0, "127.0.0.1", requireApiToken: true, runScheduler: false);
            app = ServerHost.Build(options, engine);
            await app.StartAsync();
            var baseUrl = app.Urls.Single();

            using var anonymous = new HttpClient { BaseAddress = new Uri(baseUrl) };
            using var health = await anonymous.GetAsync("/health");
            health.EnsureSuccessStatusCode();
            var healthBody = await health.Content.ReadAsStringAsync();
            Assert.Contains("Asia/Shanghai", healthBody);
            Assert.DoesNotContain(pushToken, healthBody);

            using var denied = await anonymous.GetAsync("/api/status");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            var deniedBody = await denied.Content.ReadAsStringAsync();
            Assert.DoesNotContain(apiToken, deniedBody);
            Assert.DoesNotContain(pushToken, deniedBody);

            using var client = new ReminderApiClient(baseUrl, apiToken);
            var status = await client.GetStatusAsync();
            Assert.True(status.Ok);
            Assert.NotNull(status.Value);
            Assert.True(status.Value!.ShowDesktop);
            Assert.Equal("该喝水了", status.Value.DesktopAlert?.Title);
            Assert.Equal("已喝 0/3000 ml，建议 370 ml", status.Value.DesktopAlert?.Body);

            var settings = await client.GetSettingsAsync();
            Assert.True(settings.Ok);
            Assert.Equal(3000, settings.Value!.DailyGoalMl);
            Assert.Equal(370, settings.Value.WorkCupMl);
            Assert.Equal(800, settings.Value.HomeCupMl);
            Assert.Equal("08:30", settings.Value.WorkStart);
            Assert.Equal("17:30", settings.Value.WorkEnd);
            Assert.Equal("11:50", settings.Value.LunchStart);
            Assert.Equal("13:30", settings.Value.LunchEnd);
            Assert.Equal("23:00", settings.Value.QuietStart);
            Assert.Equal(20, settings.Value.MinReminderGapMinutes);

            var rawStatus = await anonymous.SendAsync(Authorized("/api/status", apiToken));
            var rawSettings = await anonymous.SendAsync(Authorized("/api/settings", apiToken));
            AssertNoSecrets(await rawStatus.Content.ReadAsStringAsync(), pushToken);
            AssertNoSecrets(await rawSettings.Content.ReadAsStringAsync(), pushToken);

            var drink = await client.LogDesktopDrinkAsync();
            Assert.True(drink.Ok);
            Assert.Equal(370, drink.Value!.RecordedMl);
            Assert.Equal("已记录", drink.Value.Title);
            Assert.Equal("已记 370 ml", drink.Value.Message);
            Assert.Equal(370, drink.Value.ConsumedMl);

            var alertId = status.Value.DesktopAlert!.Id;
            Assert.True(await client.AckDesktopAsync(alertId));
            var after = await client.GetStatusAsync();
            Assert.Null(after.Value!.DesktopAlert);

            now = Tuesday.AddHours(19);
            var evening = await client.GetStatusAsync();
            Assert.False(evening.Value!.ShowDesktop);
            Assert.Null(evening.Value.DesktopAlert);
            Assert.False(DesktopToastPolicy.ShouldToast(evening.Value.ShowDesktop, evening.Value.Phase, "late", null));
        }
        finally
        {
            if (app is not null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
            TryDelete(dir);
        }
    }

    [Fact]
    public void MissingApiToken_RefusesToStart()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ServerOptions.Create(Path.GetTempPath(), "  ", 8787, "0.0.0.0", requireApiToken: true));
        Assert.Contains("WATERREMINDER_API_TOKEN", ex.Message);
        Assert.DoesNotContain("hlk_", ex.Message);
    }

    [Fact]
    public void SecretFiles_PreferEnvironmentThenFile()
    {
        Assert.Equal("from-env", SecretFiles.FirstNonEmpty("  ", "from-env", "from-file"));
        Assert.Equal("from-file", SecretFiles.FirstNonEmpty(null, "  ", "from-file"));
        Assert.Null(SecretFiles.FirstNonEmpty(null, "  "));

        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "api.token"), "  local-token \n");
            Assert.Equal("local-token", SecretFiles.ReadTokenFile(Path.Combine(dir, "api.token")));
            Assert.Null(SecretFiles.ReadTokenFile(Path.Combine(dir, "missing.token")));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void DesktopPolicy_RejectsLunchQuietAndRepeatedAlerts()
    {
        Assert.True(DesktopToastPolicy.ShouldToast(true, "Working", "a", null));
        Assert.False(DesktopToastPolicy.ShouldToast(false, "Working", "a", null));
        Assert.False(DesktopToastPolicy.ShouldToast(true, "Lunch", "a", null));
        Assert.False(DesktopToastPolicy.ShouldToast(true, "AfterWork", "a", null));
        Assert.False(DesktopToastPolicy.ShouldToast(true, "NoDrinkWindow", "a", null));
        Assert.False(DesktopToastPolicy.ShouldToast(true, "Working", "  ", null));
        Assert.False(DesktopToastPolicy.ShouldToast(true, "Working", "a", "a"));
    }

    [Fact]
    public void Repo_DoesNotShipPushWardOrApiSecrets()
    {
        var root = RepoRoot();
        foreach (var file in SourceFiles(Path.Combine(root, "WaterReminder.Client"))
                     .Concat(SourceFiles(Path.Combine(root, "deploy")))
                     .Append(Path.Combine(root, "DEPLOY.md")))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("hlk_", text);
            Assert.DoesNotContain("api.pushward.app", text);
        }

        foreach (var file in SourceFiles(Path.Combine(root, "WaterReminder.Client")))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("PushWard", text);
        }

        var deploy = File.ReadAllText(Path.Combine(root, "DEPLOY.md"));
        Assert.Contains("8787", deploy);
        Assert.Contains("Asia/Shanghai", deploy);
        Assert.Contains("iptables", deploy);
        Assert.Contains("nft", deploy);
        Assert.Contains("security list", deploy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NSG", deploy);

        var unit = File.ReadAllText(Path.Combine(root, "deploy", "waterreminder.service"));
        Assert.Contains("8787", unit);
        Assert.Contains("Asia/Shanghai", unit);
        Assert.DoesNotContain("iptables", unit);
        Assert.Contains("YOUR_VM:8787", File.ReadAllText(Path.Combine(root, "WaterReminder.Client", "client.sample.json")));
    }

    private static DateTime FindDue(DateTime from, DateTime until)
    {
        var settings = new UserSettings();
        var record = new DailyRecord { Date = from.ToString("yyyy-MM-dd") };
        for (var t = from; t < until; t = t.AddMinutes(1))
        {
            var plan = WaterCalculator.CalculateDynamic(settings, record, t);
            if (plan.SuggestedMl > 0 && plan.NextReminder is { } next && next <= t)
                return t;
        }
        throw new InvalidOperationException("no due reminder in range");
    }

    private static HttpRequestMessage Authorized(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static void AssertNoSecrets(string json, string pushToken)
    {
        Assert.DoesNotContain(pushToken, json);
        Assert.DoesNotContain("hlk_", json);
        using var doc = JsonDocument.Parse(json);
        AssertNoProperty(doc.RootElement, "pushWardApiToken");
        AssertNoProperty(doc.RootElement, "apiToken");
    }

    private static void AssertNoProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                Assert.NotEqual(name, property.Name, StringComparer.OrdinalIgnoreCase);
                AssertNoProperty(property.Value, name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) AssertNoProperty(item, name);
        }
    }

    private static IEnumerable<string> SourceFiles(string directory)
        => Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WaterReminder.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("repository root not found");
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}
