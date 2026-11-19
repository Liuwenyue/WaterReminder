using System.Text.Json;

namespace WaterReminder.Services;

public sealed partial class ReminderEngine
{
    private sealed record SafeTestState(string? ActionId, DateTime? ExpiresAt, MemberPushFeedback Feedback);
    private string SafeTestPath => Path.Combine(_options.DataDirectory, "push-test.json");

    private SafeTestState LoadSafeTest()
    {
        try { return JsonSerializer.Deserialize<SafeTestState>(File.ReadAllText(SafeTestPath))!; }
        catch (Exception e) when (e is IOException or JsonException) { return new(null, null, new("not_tested", "尚未测试手机推送。")); }
    }

    private void SaveSafeTest(SafeTestState state)
    {
        Directory.CreateDirectory(_options.DataDirectory);
        File.WriteAllText(SafeTestPath + ".tmp", JsonSerializer.Serialize(state));
        File.Move(SafeTestPath + ".tmp", SafeTestPath, true);
    }

    public MemberPushFeedback SafePushFeedback()
    {
        lock (_gate) return LoadSafeTest().Feedback;
    }

    /// <summary>Connectivity test only. Its sole action acknowledges receipt and never edits water records.</summary>
    public async Task<MemberPushFeedback> SendSafeTestPushAsync(CancellationToken ct = default)
    {
        PushWardMessage message;
        DateTime now;
        string action;
        lock (_gate)
        {
            EnsureActiveLocked();
            now = RefreshLocked();
            if (_pushInFlight) return new("busy", "正在发送，请稍后重试。");
            if (!ReminderRouting.ShouldPublishPushWard(_settings, _push.IsConfigured))
            {
                var unavailable = new MemberPushFeedback("unconfigured", "请先开启手机推送并配置该成员自己的 PushWard 密钥。", now);
                SaveSafeTest(new(null, null, unavailable));
                return unavailable;
            }
            action = "test-ack-" + Guid.NewGuid().ToString("N");
            message = new(_settings.PushWardApiToken, new PushWardPayload("喝水提醒 · 连接测试",
                "请点击“确认收到”反馈设备连接。", PushWardMessageBuilder.Source, PushWardMessageBuilder.SourceDisplayName,
                PushWardMessageBuilder.ConfirmationLevel, null,
                [new(action, "确认收到", "checkmark.circle")]));
            SaveSafeTest(new(action, now.AddMinutes(15), new("sending", "正在交给推送渠道…", now)));
            _pushInFlight = true;
        }
        bool accepted;
        try
        {
            using var send = CancellationTokenSource.CreateLinkedTokenSource(ct, _retirement.Token);
            accepted = await _push.PublishTrackedAsync(message, now, send.Token).ConfigureAwait(false);
        }
        finally { lock (_gate) _pushInFlight = false; }
        lock (_gate)
        {
            var feedback = new MemberPushFeedback(accepted ? "channel_accepted" : "failed",
                accepted ? "渠道已接收；等待成员点击“确认收到”。这不代表设备已收到。" : "渠道未确认接收，请检查密钥或网络。", now, accepted ? _clock() : null);
            SaveSafeTest(new(action, now.AddMinutes(15), feedback));
            return feedback;
        }
    }

    private string? ApplySafeTestReply(DateTime now, PushWardAnswerItem answer, string? expectedAction)
    {
        var state = LoadSafeTest();
        if (state.ActionId != answer.ActionId || expectedAction != answer.ActionId || state.ExpiresAt < now)
            return "这条连接测试已过期，请重新测试。未修改饮水记录。";
        SaveSafeTest(state with { ActionId = null, Feedback = state.Feedback with
        { State = "confirmed", Message = "成员已点击确认收到。未修改饮水记录。", ConfirmedAt = now } });
        return "已确认收到连接测试。未修改饮水记录。";
    }
}
