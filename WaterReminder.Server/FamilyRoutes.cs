using WaterReminder;
namespace WaterReminder.Server;
internal static class FamilyRoutes
{
    public static void MapFamilyRoutes(this WebApplication app)
    {
        app.MapPost("/api/family/{id:guid}/reset-code", (HttpContext context, FamilyAccounts accounts, Guid id) =>
        {
            if (accounts.Authenticate(context.Request) != Guid.Empty) return Results.StatusCode(403);
            context.Response.Headers.CacheControl = "no-store";
            var result = accounts.ResetAccessCode(id);
            return result is null ? Results.NotFound(new { error = "成员不存在或已停用。" }) : Results.Ok(result);
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
}
