using WaterReminder;
namespace WaterReminder.Server;
internal static class FamilyRoutes
{
    public static void MapFamilyRoutes(this WebApplication app, ServerOptions options)
    {
        app.MapPost("/api/family/invitations", (HttpContext context, FamilyAccounts accounts, CreateMemberInvite request) =>
        {
            if (accounts.Authenticate(context.Request) != Guid.Empty) return Results.StatusCode(403);
            var result = accounts.CreateInvitation(request, out var error);
            return result is null ? Results.BadRequest(new { error }) : Results.Ok(result);
        });
        app.MapDelete("/api/family/{id:guid}/invitation", (HttpContext context, FamilyAccounts accounts, Guid id) =>
        {
            if (accounts.Authenticate(context.Request) != Guid.Empty) return Results.StatusCode(403);
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
            if (accounts.Find(id) is not { } engine) return Results.NotFound();
            return Results.Ok(await engine.SendSafeTestPushAsync(context.RequestAborted));
        });
        app.MapGet("/api/family/{id:guid}/push-feedback", (HttpContext context, FamilyAccounts accounts, Guid id) =>
        {
            var caller = accounts.Authenticate(context.Request);
            if (caller != Guid.Empty && caller != id) return Results.StatusCode(403);
            return accounts.Find(id) is { } engine ? Results.Ok(engine.SafePushFeedback()) : Results.NotFound();
        });
    }
}
