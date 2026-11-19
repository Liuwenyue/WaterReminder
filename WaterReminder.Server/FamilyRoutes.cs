using WaterReminder;
namespace WaterReminder.Server;
internal static class FamilyRoutes
{
    private sealed record LoginRequest(string? Code, string? ShareToken);

    public static void MapFamilyRoutes(this WebApplication app, ServerOptions options)
    {
        app.MapPost("/api/wechat/login", async (HttpContext context, FamilyAccounts accounts, LoginRequest request) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var code = request.Code?.Trim() ?? "";
            if (code.Length is < 8 or > 128 || code.Any(char.IsWhiteSpace) || code.Any(char.IsControl))
                return Results.BadRequest(new { error = "微信登录信息无效，请重试。" });
            if (!options.WeChat.IsConfigured)
                return Results.StatusCode(503);
            string? openId;
            try { openId = await options.WeChat.ExchangeOpenIdAsync(code, context.RequestAborted); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            { return Results.StatusCode(503); }
            if (string.IsNullOrEmpty(openId)) return Results.Unauthorized();
            var result = accounts.LogInWeChat(openId, request.ShareToken, out var error);
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
            if (!accounts.OwnerCanAccess(id)) return Results.NotFound();
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
}
