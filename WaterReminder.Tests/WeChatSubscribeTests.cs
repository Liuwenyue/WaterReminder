using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using WaterReminder;
using WaterReminder.Server;
using WaterReminder.Services;
using Xunit;

namespace WaterReminder.Tests;

public sealed class WeChatSubscribeTests
{
    private const string Secret = "test-wechat-secret";
    private const string Template = "template-id-0123456789";
    private static readonly DateTime Tuesday = new(2026, 11, 3, 10, 0, 0);

    [Fact]
    public void ConfiguredWeChat_ReplacesPushWard()
    {
        Assert.Equal(PhoneChannel.WeChat, ReminderRouting.Choose(true, true, true));
        Assert.Equal(PhoneChannel.PushWard, ReminderRouting.Choose(true, true, false));
        Assert.Equal(PhoneChannel.None, ReminderRouting.Choose(false, true, true));
    }

    [Fact]
    public void Payload_UsesTemplateFields_AndOmitsSecret()
    {
        var config = Config();
        var payload = WeChatClient.BuildPayload(config, new WeChatReminder("openid-test", 370, 0, 3000, Tuesday));
        Assert.Contains("\"touser\":\"openid-test\"", payload);
        Assert.Contains(Template, payload);
        Assert.Contains("该喝水了", payload);
        Assert.Contains("2026年11月3日 10:00", payload);
        Assert.Contains("建议喝370毫升", payload);
        Assert.Contains("\"miniprogram_state\":\"developer\"", payload);
        Assert.DoesNotContain(Secret, payload);
    }

    [Fact]
    public void Payload_TruncatesThingFields()
    {
        var config = Config();
        config = new WeChatConfig
        {
            AppId = config.AppId,
            AppSecret = config.AppSecret,
            TemplateId = config.TemplateId,
            Fields = new Dictionary<string, string> { ["thing1"] = new string('喝', 30) },
        };
        var payload = WeChatClient.BuildPayload(config, new WeChatReminder("openid-test", 1, 1, 1, Tuesday));
        Assert.Contains(new string('喝', 20), payload);
        Assert.DoesNotContain(new string('喝', 21), payload);
    }

    [Fact]
    public async Task Tick_SendsOneWeChatReminder_AndDoesNotCallPushWard()
    {
        var dir = Temp();
        var push = new Recorder();
        var wechat = new Recorder(WeChatOk);
        try
        {
            WeChatUsersSave(dir, "openid-test", 1);
            using var client = new WeChatClient(Config(), wechat);
            using var engine = Engine(dir, push, client, () => Tuesday);
            await engine.TickAsync();

            Assert.Empty(push.Requests);
            var send = Assert.Single(wechat.Requests, item => item.Path.Contains("/message/subscribe/send", StringComparison.Ordinal));
            Assert.Contains("建议喝370毫升", send.Body);
            Assert.DoesNotContain(Secret, send.Body);
            Assert.Equal(0, engine.WeChatStatus().Credits);

            await engine.TickAsync();
            Assert.Single(wechat.Requests, item => item.Path.Contains("/message/subscribe/send", StringComparison.Ordinal));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task QuotaError_ClearsCredits_WithoutAnotherSend()
    {
        var dir = Temp();
        var wechat = new Recorder(request => request.Path.Contains("/cgi-bin/token", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"access_token":"token-1","expires_in":7200}""")
            : (HttpStatusCode.OK, """{"errcode":43101,"errmsg":"user refuse"}"""));
        var now = Tuesday;
        try
        {
            WeChatUsersSave(dir, "openid-test", 2);
            using var client = new WeChatClient(Config(), wechat);
            using var engine = Engine(dir, new Recorder(), client, () => now);
            await engine.TickAsync();
            Assert.Equal(0, engine.WeChatStatus().Credits);
            now = now.AddMinutes(1);
            await engine.TickAsync();
            Assert.Single(wechat.Requests, item => item.Path.Contains("/message/subscribe/send", StringComparison.Ordinal));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task BindAndSubscription_StoreOpenIdAndIgnoreDuplicateCredit()
    {
        var dir = Temp();
        var wechat = new Recorder(_ => (HttpStatusCode.OK, """{"openid":"openid-test","session_key":"hidden"}"""));
        try
        {
            using var client = new WeChatClient(Config(), wechat);
            using var engine = Engine(dir, new Recorder(), client, () => Tuesday);
            var bound = await engine.BindWeChatAsync("login-code-1");
            Assert.True(bound.Ok);
            Assert.True(bound.Status.Bound);
            var file = File.ReadAllText(Path.Combine(dir, "wechat-user.json"));
            Assert.Contains("openid-test", file);
            Assert.DoesNotContain(Secret, file);
            Assert.DoesNotContain("hidden", file);

            var first = engine.AcceptWeChatSubscription("tap-0001", "accept");
            var again = engine.AcceptWeChatSubscription("tap-0001", "accept");
            Assert.Equal(1, first.Status.Credits);
            Assert.Equal(1, again.Status.Credits);
            Assert.False(engine.AcceptWeChatSubscription("no", "accept").Ok);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task Api_HidesTemplateUntilWeChatIsConfigured()
    {
        var dir = Temp();
        WebApplication? app = null;
        try
        {
            using var engine = new ReminderEngine(new ReminderEngineOptions
            {
                DataDirectory = dir,
                Clock = () => Tuesday,
                PollAnswers = false,
            });
            app = ServerHost.Build(ServerOptions.Create(dir, "desktop-api-token", 0, "127.0.0.1", true, false), engine);
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "desktop-api-token");
            var status = await client.GetFromJsonAsync<WeChatStatusResponse>("/api/wechat", ReminderApiClient.Json);
            Assert.NotNull(status);
            Assert.False(status!.Enabled);
            Assert.Equal("", status.TemplateId);
            var denied = await client.PostAsJsonAsync("/api/wechat/subscription", new { id = "tap-0001", status = "accept" });
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        }
        finally
        {
            if (app is not null) await app.StopAsync();
            TryDelete(dir);
        }
    }

    private static WeChatConfig Config() => new()
    {
        AppId = WeChatConfig.DefaultAppId,
        AppSecret = Secret,
        TemplateId = Template,
        MiniProgramState = "developer",
        Page = "pages/today/today",
        Fields = WeChatConfig.DefaultFields,
    };

    private static ReminderEngine Engine(string dir, Recorder push, WeChatClient wechat, Func<DateTime> clock)
        => new(new ReminderEngineOptions
        {
            DataDirectory = dir,
            Clock = clock,
            PushWardToken = () => "hlk_test_token",
            Http = new HttpClient(push),
            PollAnswers = false,
            WeChat = wechat,
        });

    private static void WeChatUsersSave(string dir, string openId, int credits)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "wechat-user.json"),
            $$"""{"openId":"{{openId}}","credits":{{credits}},"seen":[]}""");
    }

    private static string Temp() => Path.Combine(Path.GetTempPath(), "WaterReminderTests", Guid.NewGuid().ToString("N"));

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static (HttpStatusCode Status, string Body) WeChatOk(Recorded request)
        => request.Path.Contains("/cgi-bin/token", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"access_token":"token-1","expires_in":7200}""")
            : (HttpStatusCode.OK, """{"errcode":0,"errmsg":"ok"}""");

    private sealed record Recorded(string Path, string? Body);

    private sealed class Recorder : HttpMessageHandler
    {
        private readonly List<Recorded> _requests = [];
        private readonly Func<Recorded, (HttpStatusCode Status, string Body)> _respond;
        public Recorder(Func<Recorded, (HttpStatusCode, string)>? respond = null) => _respond = respond ?? (_ => (HttpStatusCode.Created, "{}"));
        public IReadOnlyList<Recorded> Requests => _requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new Recorded(request.RequestUri!.PathAndQuery, body);
            _requests.Add(recorded);
            var (status, response) = _respond(recorded);
            return new HttpResponseMessage(status) { Content = new StringContent(response) };
        }
    }
}
