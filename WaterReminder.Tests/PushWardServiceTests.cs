using System.Net;
using System.Net.Http;
using System.Text.Json;
using WaterReminder;
using WaterReminder.Services;
using Xunit;

namespace WaterReminder.Tests;

/// <summary>
/// Offline PushWard tests: Bearer header, url-less Answer action, publish success/fail,
/// Answers cursor bootstrap + deduplication, and reminder routing. No network, no real credentials.
/// </summary>
public sealed class PushWardServiceTests
{
    private const string Token = "hlk_test_placeholder_token";

    private static UserSettings Settings() => new()
    {
        MobilePushEnabled = true,
        PushWardApiToken = Token,
    };

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Body,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string? MediaType);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<RecordedRequest> _requests = new();

        public Func<RecordedRequest, (HttpStatusCode Status, string Body)> Respond { get; set; }
            = _ => (HttpStatusCode.Created, """{"id":"notification-1","answerable":true}""");

        public IReadOnlyList<RecordedRequest> Requests
        {
            get { lock (_gate) return _requests.ToArray(); }
        }

        public int Count(Func<RecordedRequest, bool> predicate)
        {
            lock (_gate) return _requests.Count(predicate);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new RecordedRequest(
                request.Method,
                request.RequestUri!,
                body,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Content?.Headers.ContentType?.MediaType);
            lock (_gate) _requests.Add(recorded);

            var (status, responseBody) = Respond(recorded);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }

    private static bool IsAnswersRequest(RecordedRequest request)
        => request.Method == HttpMethod.Get
           && request.Uri.AbsolutePath.EndsWith("/notifications/answers", StringComparison.Ordinal);

    // ---------- message builder ----------

    [Fact]
    public void Reminder_BuildsPushWardJson_WithUrlLessDrinkAction()
    {
        var settings = Settings();
        settings.DailyGoalMl = 2000;
        var message = PushWardMessageBuilder.BuildReminder(settings, 300, 400);

        Assert.Equal(Token, message.Token);
        Assert.Equal("该喝水了", message.Payload.Title);
        Assert.Equal("已喝 400/2000 ml，建议 300 ml", message.Payload.Body);
        Assert.Equal("waterreminder", message.Payload.Source);
        Assert.Equal("喝水提醒", message.Payload.SourceDisplayName);
        Assert.Equal("time-sensitive", message.Payload.Level);
        Assert.Equal("water-evening", message.Payload.CollapseId);

        var action = Assert.Single(message.Payload.Actions!);
        Assert.Equal("drink", action.Id);
        Assert.Equal("喝水了", action.Title);
        Assert.Equal("drop.fill", action.Icon);

        using var doc = JsonDocument.Parse(message.Json);
        var root = doc.RootElement;
        Assert.Equal("该喝水了", root.GetProperty("title").GetString());
        Assert.Equal("已喝 400/2000 ml，建议 300 ml", root.GetProperty("body").GetString());
        Assert.Equal("waterreminder", root.GetProperty("source").GetString());
        Assert.Equal("time-sensitive", root.GetProperty("level").GetString());
        Assert.False(root.TryGetProperty("url", out _));       // 顶层没有 url
        Assert.False(root.TryGetProperty("token", out _));     // 令牌只走请求头

        var jsonAction = root.GetProperty("actions")[0];
        Assert.Equal(new[] { "icon", "id", "title" },
            jsonAction.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray());
        Assert.False(jsonAction.TryGetProperty("url", out _));
        Assert.False(jsonAction.TryGetProperty("foreground", out _));
        Assert.False(jsonAction.TryGetProperty("method", out _));
        Assert.False(jsonAction.TryGetProperty("body", out _));
    }

    [Fact]
    public void Confirmation_HasNoActions_AndNoTopLevelUrl()
    {
        var message = PushWardMessageBuilder.BuildConfirmation(Settings(), 800);

        Assert.Equal("已记录", message.Payload.Title);
        Assert.Equal("已记 800 ml", message.Payload.Body);
        Assert.Equal("active", message.Payload.Level);
        Assert.False(message.HasActions);

        using var doc = JsonDocument.Parse(message.Json);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("actions", out _));
        Assert.False(root.TryGetProperty("url", out _));
        Assert.False(root.TryGetProperty("collapse_id", out _));
    }

    [Fact]
    public void TestMessage_UsesWaterTestCollapse_AndSameDrinkAction()
    {
        var settings = Settings();
        settings.DailyGoalMl = 2000;
        settings.HomeCupCapacityMl = 800;
        var message = PushWardMessageBuilder.BuildTest(settings, 400);
        Assert.Equal("该喝水了", message.Payload.Title);
        Assert.Equal("water-test", message.Payload.CollapseId);
        Assert.Equal("已喝 400/2000 ml，建议 800 ml", message.Payload.Body);

        var action = Assert.Single(message.Payload.Actions!);
        Assert.Equal(PushWardMessageBuilder.BuildDrinkAction(), action);
    }

    // ---------- publish ----------

    [Fact]
    public async Task PublishReminder_201_SendsBearerJson_AndNeverPutsTokenInBody()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        using var service = new PushWardService(Settings(), http);

        Assert.True(await service.PublishReminderAsync(300));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.pushward.app/notifications", request.Uri.ToString());
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal(Token, request.AuthorizationParameter);
        Assert.Equal("application/json", request.MediaType);
        Assert.Contains("\"title\":\"该喝水了\"", request.Body);
        Assert.Contains("\"id\":\"drink\"", request.Body);
        Assert.DoesNotContain("\"url\"", request.Body);       // Answer 动作不带 url
        Assert.DoesNotContain("foreground", request.Body);    // 也不带 foreground
        Assert.DoesNotContain(Token, request.Body);           // 令牌只在 Authorization 头里
    }

    [Fact]
    public async Task PublishTest_201_Succeeds()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        using var service = new PushWardService(Settings(), http);

        Assert.True(await service.PublishTestAsync());
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task NonSuccessStatus_ReturnsFalse(HttpStatusCode status)
    {
        var handler = new FakeHandler { Respond = _ => (status, """{"error":"nope"}""") };
        using var http = new HttpClient(handler);
        using var service = new PushWardService(Settings(), http);

        Assert.False(await service.PublishReminderAsync(300));
        Assert.False(await service.PublishConfirmationAsync(800));
        Assert.False(await service.PublishTestAsync());
    }

    [Fact]
    public async Task NetworkException_IsSwallowed()
    {
        var handler = new FakeHandler { Respond = _ => throw new HttpRequestException("simulated offline") };
        using var http = new HttpClient(handler);
        using var service = new PushWardService(Settings(), http);

        Assert.False(await service.PublishReminderAsync(300));
        Assert.False(await service.PublishConfirmationAsync(800));
        Assert.False(await service.PublishTestAsync());
    }

    [Fact]
    public async Task EmptyToken_IsNeverSent()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var settings = Settings();
        settings.PushWardApiToken = " ";
        using var service = new PushWardService(settings, http);

        Assert.False(service.IsConfigured);
        Assert.False(await service.PublishReminderAsync(300));
        Assert.False(await service.PublishTestAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void IsConfigured_RequiresTokenOnly()
    {
        var settings = Settings();
        Assert.True(PushWardService.IsConfiguredFor(settings));

        settings.PushWardApiToken = "";
        Assert.False(PushWardService.IsConfiguredFor(settings));

        settings.PushWardApiToken = "  " + Token + "  ";
        Assert.True(PushWardService.IsConfiguredFor(settings));
    }

    // ---------- answers parser ----------

    [Fact]
    public void ParseAnswers_ReadsCursorAndItems_AndIgnoresUnknownActions()
    {
        var batch = PushWardService.ParseAnswerBatch(
            """{"cursor":"c1","items":[{"notification_id":"n1","action_id":"drink"},{"notification_id":"n2","action_id":"dismiss"},{"action_id":"drink"}]}""");

        Assert.NotNull(batch);
        Assert.Equal("c1", batch!.Cursor);
        Assert.Equal(2, batch.Items.Count); // missing notification_id is ignored
        Assert.Equal("n1", batch.Items[0].NotificationId);
        Assert.Equal("drink", batch.Items[0].ActionId);
        Assert.Equal("dismiss", batch.Items[1].ActionId);
    }

    [Fact]
    public void ParseAnswers_AcceptsNumericCursorAndMissingItems()
    {
        var batch = PushWardService.ParseAnswerBatch("""{"cursor":42}""");
        Assert.NotNull(batch);
        Assert.Equal("42", batch!.Cursor);
        Assert.Empty(batch.Items);

        Assert.Null(PushWardService.ParseAnswerBatch("not json"));
        Assert.Null(PushWardService.ParseAnswerBatch(""));
        Assert.Null(PushWardService.ParseAnswerBatch("[]"));
    }

    // ---------- answers stream ----------

    [Fact]
    public async Task Poll_BootstrapCursorThenDrink_RecordsOnceAndConfirms()
    {
        var answers = 0;
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource();

        var handler = new FakeHandler();
        handler.Respond = request =>
        {
            if (IsAnswersRequest(request))
            {
                return Interlocked.Increment(ref answers) switch
                {
                    1 => (HttpStatusCode.OK, """{"items":[],"cursor":"c0"}"""),
                    2 => (HttpStatusCode.OK,
                        """{"items":[{"notification_id":"n1","action_id":"drink"}],"cursor":"c1"}"""),
                    _ => (HttpStatusCode.OK, """{"items":[],"cursor":"c1"}"""),
                };
            }

            confirmed.TrySetResult();
            return (HttpStatusCode.Created, """{"id":"confirmation-1"}""");
        };

        using var http = new HttpClient(handler);
        var calls = 0;
        using var service = new PushWardService(Settings(), http,
            recordDrink: () =>
            {
                Interlocked.Increment(ref calls);
                recorded.TrySetResult();
                return 800;
            },
            dispatch: action => action(),
            delay: (_, ct) => Task.Delay(10, ct));

        var poll = service.PollAnswersAsync(cts.Token);
        await recorded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await poll.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, calls);
        Assert.Equal(1, handler.Count(r => r.Method == HttpMethod.Post
                                           && r.Uri.AbsolutePath.EndsWith("/notifications", StringComparison.Ordinal)));

        var requests = handler.Requests.Where(IsAnswersRequest).ToList();
        Assert.True(requests.Count >= 2);
        Assert.Equal("", requests[0].Uri.Query);                        // bootstrap has no after
        Assert.Contains("after=c0", requests[1].Uri.Query);
        Assert.Contains("wait=25", requests[1].Uri.Query);
        Assert.Equal("Bearer", requests[1].AuthorizationScheme);
        Assert.Equal(Token, requests[1].AuthorizationParameter);
    }

    [Fact]
    public async Task Poll_DuplicateNotificationId_IsRecordedOnce()
    {
        var answers = 0;
        var delays = 0;
        var records = 0;
        var confirmations = 0;
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource();

        var handler = new FakeHandler();
        handler.Respond = request =>
        {
            if (IsAnswersRequest(request))
            {
                return Interlocked.Increment(ref answers) switch
                {
                    1 => (HttpStatusCode.OK, """{"items":[],"cursor":"c0"}"""),
                    2 => (HttpStatusCode.OK,
                        """{"items":[{"notification_id":"n1","action_id":"drink"}],"cursor":"c1"}"""),
                    3 => (HttpStatusCode.OK,
                        """{"items":[{"notification_id":"n1","action_id":"drink"}],"cursor":"c2"}"""),
                    _ => (HttpStatusCode.OK, """{"items":[],"cursor":"c2"}"""),
                };
            }

            Interlocked.Increment(ref confirmations);
            confirmed.TrySetResult();
            return (HttpStatusCode.Created, """{"id":"confirmation-1"}""");
        };

        using var http = new HttpClient(handler);
        using var service = new PushWardService(Settings(), http,
            recordDrink: () =>
            {
                Interlocked.Increment(ref records);
                return 800;
            },
            dispatch: action => action(),
            delay: (_, _) =>
            {
                // Two successful batches (drink + duplicate) have been handled by now.
                if (Interlocked.Increment(ref delays) >= 2) cts.Cancel();
                return Task.CompletedTask;
            });

        await service.PollAnswersAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5));
        await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(answers >= 3);
        Assert.Equal(1, records);
        Assert.Equal(1, confirmations);
    }

    [Fact]
    public async Task Poll_NonDrinkAction_DoesNothing()
    {
        var answers = 0;
        var records = 0;
        var cts = new CancellationTokenSource();

        var handler = new FakeHandler();
        handler.Respond = request =>
        {
            if (!IsAnswersRequest(request))
                return (HttpStatusCode.Created, """{"id":"unexpected"}""");

            return Interlocked.Increment(ref answers) switch
            {
                1 => (HttpStatusCode.OK, """{"items":[],"cursor":"c0"}"""),
                2 => (HttpStatusCode.OK,
                    """{"items":[{"notification_id":"n1","action_id":"dismiss"}],"cursor":"c1"}"""),
                _ => (HttpStatusCode.OK, """{"items":[],"cursor":"c1"}"""),
            };
        };

        using var http = new HttpClient(handler);
        using var service = new PushWardService(Settings(), http,
            recordDrink: () => { Interlocked.Increment(ref records); return 800; },
            dispatch: action => action(),
            delay: (_, _) =>
            {
                if (Volatile.Read(ref answers) >= 3) cts.Cancel();
                return Task.CompletedTask;
            });

        await service.PollAnswersAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, records);
        Assert.Equal(0, handler.Count(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public void ParseAnswerBatch_AcceptsNumericNotificationId()
    {
        var batch = PushWardService.ParseAnswerBatch(
            """{"items":[{"notification_id":61901,"status":"answered","action_id":"drink"}],"cursor":"108"}""");
        Assert.NotNull(batch);
        Assert.Equal("108", batch!.Cursor);
        Assert.Single(batch.Items);
        Assert.Equal("61901", batch.Items[0].NotificationId);
        Assert.Equal("drink", batch.Items[0].ActionId);
    }

    [Fact]
    public void NotificationId_StringAndNumber_DedupeAsTheSameId()
    {
        var numeric = PushWardService.ParseAnswerBatch(
            """{"items":[{"notification_id":61901,"action_id":"drink"}],"cursor":1}""");
        var text = PushWardService.ParseAnswerBatch(
            """{"items":[{"notification_id":"61901","action_id":"drink"}],"cursor":"1"}""");

        Assert.NotNull(numeric);
        Assert.NotNull(text);
        Assert.Equal(text!.Items[0].NotificationId, numeric!.Items[0].NotificationId);

        var seen = new SeenIdCache(10);
        Assert.True(seen.Add(numeric.Items[0].NotificationId));
        Assert.False(seen.Add(text.Items[0].NotificationId));
    }

}
