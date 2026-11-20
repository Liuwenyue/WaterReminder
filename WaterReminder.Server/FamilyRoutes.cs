using WaterReminder;
namespace WaterReminder.Server;
internal static class FamilyRoutes
{
    private sealed record LoginRequest(string? Code, string? ShareToken, Guid? ExpectedMemberId, string? Name, bool OwnerOnly);
    private sealed record PairRequest(string? Code);
    private sealed record DeviceLoginRequest(string? DeviceId, Guid? ExpectedMemberId);

    public static void MapFamilyRoutes(this WebApplication app, ServerOptions options)
    {
        app.MapPost("/api/wechat/login", async (HttpContext context, FamilyAccounts accounts, LoginRequest request) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var (openId, failure) = await IdentifyAsync(options, request.Code, context.RequestAborted);
            if (failure is not null) return failure;
            var result = accounts.LogInWeChat(openId!, request.ShareToken, request.ExpectedMemberId,
                request.Name, request.OwnerOnly, out var error, out var registrationRequired);
            if (registrationRequired) return Results.Conflict(new { error, registrationRequired });
            return result is null ? Results.BadRequest(new { error }) : Results.Ok(result);
        });
        app.MapPost("/api/wechat/owner-pair", async (HttpContext context, FamilyAccounts accounts, PairRequest request) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var (openId, failure) = await IdentifyAsync(options, request.Code, context.RequestAborted);
            if (failure is not null) return failure;
            var pair = accounts.CreateOwnerPair(openId!);
            return pair is null
                ? Results.Conflict(new { error = "暂时无法生成配对码。若已绑定，请直接打开管理员账本。" })
                : Results.Ok(pair);
        });
        app.MapPost("/api/family/owner-pair", (HttpContext context, FamilyAccounts accounts, PairRequest request) =>
        {
            if (!ServerHost.Authorized(context.Request, options.ApiToken)) return Results.StatusCode(403);
            var error = accounts.ConfirmOwnerPair(request.Code?.Trim());
            return error is null ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error });
        });
        app.MapPost("/api/wechat/owner-merge", (HttpContext context, FamilyAccounts accounts) =>
        {
            if (accounts.Authenticate(context.Request) != Guid.Empty) return Results.StatusCode(403);
            return accounts.MergeOwnerPersonalRecords()
                ? Results.Ok(new { ok = true })
                : Results.Conflict(new { error = "请先确认管理员微信配对。" });
        });
        app.MapPost("/api/device/login", (HttpContext context, FamilyAccounts accounts, DeviceLoginRequest request) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var result = accounts.LogInDevice(request.DeviceId ?? "", request.ExpectedMemberId, out var error);
            if (result is null && error == FamilyAccounts.MissingDeviceAccount)
                return Results.NotFound(new { error, deviceAccountMissing = true });
            return result is null ? Results.BadRequest(new { error }) : Results.Ok(result);
        });
        app.MapPost("/api/family/share", (HttpContext context, FamilyAccounts accounts) =>
        {
            if (accounts.Authenticate(context.Request) != Guid.Empty) return Results.StatusCode(403);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { token = accounts.CreateWeChatShare() });
        });
        app.MapPost("/api/family/invitations", (HttpContext context, FamilyAccounts accounts, CreateMemberInvite request) =>
        {
            if (accounts.Authenticate(context.Request) != Guid.Empty) return Results.StatusCode(403);
            var result = accounts.CreateInvitation(request, out var error);
            return result is null ? Results.BadRequest(new { error }) : Results.Ok(result);
        });
        app.MapDelete("/api/family/{id:guid}/invitation", (HttpContext context, FamilyAccounts accounts, Guid id) =>
        {
            if (accounts.Authenticate(context.Request) != Guid.Empty) return Results.StatusCode(403);
            if (!accounts.OwnerCanAccess(id)) return Results.NotFound();
            return accounts.RevokeInvitation(id) ? Results.Ok(new { ok = true }) : Results.NotFound();
        });
        app.MapPost("/api/invitations/preview", (FamilyAccounts accounts, InvitationTokenRequest request) =>
        {
            var result = accounts.PreviewInvitation(request.InviteToken);
            return result is null ? Results.BadRequest(new { error = "邀请已失效、已使用或已撤销，请联系家人重新生成。" }) : Results.Ok(result);
        });
        app.MapPost("/api/invitations/redeem", (FamilyAccounts accounts, InvitationTokenRequest request) =>
        {
            var result = accounts.RedeemInvitation(request.InviteToken);
            return result is null ? Results.BadRequest(new { error = "邀请已失效、已使用或已撤销，请联系家人重新生成。" }) : Results.Ok(result);
        });
        app.MapGet("/api/family/progress", (HttpContext context, FamilyAccounts accounts) =>
        {
            if (accounts.Authenticate(context.Request) != Guid.Empty) return Results.StatusCode(403);
            return Results.Ok(accounts.Progress());
        });
        app.MapPost("/api/family/{id:guid}/push-test", async (HttpContext context, FamilyAccounts accounts, Guid id) =>
        {
            if (accounts.Authenticate(context.Request) != Guid.Empty) return Results.StatusCode(403);
            if (id != Guid.Empty && !accounts.OwnerCanAccess(id)) return Results.NotFound();
            if (accounts.Find(id) is not { } engine) return Results.NotFound();
            return Results.Ok(await engine.SendSafeTestPushAsync(context.RequestAborted));
        });
        app.MapGet("/api/family/{id:guid}/push-feedback", (HttpContext context, FamilyAccounts accounts, Guid id) =>
        {
            var caller = accounts.Authenticate(context.Request);
            if (caller != Guid.Empty && caller != id) return Results.StatusCode(403);
            if (caller == Guid.Empty && !accounts.OwnerCanAccess(id)) return Results.NotFound();
            return accounts.Find(id) is { } engine ? Results.Ok(engine.SafePushFeedback()) : Results.NotFound();
        });
    }

    private static async Task<(string? OpenId, IResult? Failure)> IdentifyAsync(
        ServerOptions options, string? loginCode, CancellationToken cancellationToken)
    {
        var code = loginCode?.Trim() ?? "";
        if (code.Length is < 8 or > 128 || code.Any(char.IsWhiteSpace) || code.Any(char.IsControl))
            return (null, Results.BadRequest(new { error = "微信登录信息无效，请重试。" }));
        if (!options.WeChat.CanIdentify)
            return (null, Results.Json(new { error = "微信登录暂不可用，请稍后重试。" }, statusCode: 503));
        try
        {
            var openId = await options.WeChat.ExchangeOpenIdAsync(code, cancellationToken);
            return string.IsNullOrEmpty(openId) ? (null, Results.Unauthorized()) : (openId, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return (null, Results.Json(new { error = "微信登录暂不可用，请稍后重试。" }, statusCode: 503));
        }
    }
}
