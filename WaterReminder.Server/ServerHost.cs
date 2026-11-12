using System.Security.Cryptography;
using System.Text;
using WaterReminder;
using WaterReminder.Services;

namespace WaterReminder.Server;

public static class ServerHost
{
    public static WebApplication Build(ServerOptions options, ReminderEngine engine)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.PreferHostingUrls(false);
        builder.WebHost.UseUrls(options.ListenUrl);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddSingleton(engine);
        if (options.RunScheduler)
            builder.Services.AddHostedService<ReminderScheduler>();

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/health"))
            {
                await next();
                return;
            }

            if (!Authorized(context.Request, options.ApiToken))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                return;
            }

            await next();
        });

        app.MapGet("/health", () => Results.Ok(new
        {
            ok = true,
            timeZone = ShanghaiClock.Id,
            port = options.Port,
        }));

        app.MapGet("/api/status", (ReminderEngine e) => Results.Ok(e.Status()));
        app.MapGet("/api/settings", (ReminderEngine e) => Results.Ok(e.ClientSettings()));
        app.MapPut("/api/settings", async (ReminderEngine e, HttpRequest request) =>
        {
            string body;
            using (var reader = new StreamReader(request.Body))
                body = await reader.ReadToEndAsync();
            if (!SettingsEditor.TryParse(body, out var update, out var error) || update is null)
                return Results.BadRequest(new { error = error ?? SettingsEditor.Unreadable });
            if (!e.TryUpdateSettings(update, out error))
                return Results.BadRequest(new { error });
            return Results.Ok(e.ClientSettings());
        });

        app.MapPost("/api/drinks", async (ReminderEngine e, HttpRequest request) =>
        {
            DrinkRequest? body = null;
            if (request.ContentLength is not 0)
            {
                try { body = await request.ReadFromJsonAsync<DrinkRequest>(); }
                catch { body = null; }
            }

            var recorded = e.LogDrink(body?.AmountMl, body?.Source ?? "desktop");
            var status = e.Status();
            return Results.Ok(new DrinkResponse
            {
                RecordedMl = recorded,
                ConsumedMl = status.ConsumedMl,
                GoalMl = status.GoalMl,
                Title = "已记录",
                Message = $"已记 {recorded} ml",
            });
        });

        app.MapPost("/api/desktop-ack", async (ReminderEngine e, HttpRequest request) =>
        {
            AckRequest? body = null;
            try { body = await request.ReadFromJsonAsync<AckRequest>(); }
            catch { body = null; }
            return e.AcknowledgeDesktop(body?.Id)
                ? Results.Ok(new { ok = true })
                : Results.NotFound(new { ok = false });
        });

        return app;
    }

    public static bool Authorized(HttpRequest request, string expectedToken)
    {
        if (string.IsNullOrWhiteSpace(expectedToken)) return false;
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var provided = header[prefix.Length..].Trim();
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(expectedToken.Trim()));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
