using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WaterReminder;
using WaterReminder.Services;

namespace WaterReminder.Server;

public static class ServerHost
{
    private static readonly object CallerKey = new();
    private static readonly object EngineKey = new();

    public static WebApplication Build(ServerOptions options, ReminderEngine engine)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.PreferHostingUrls(false);
        builder.WebHost.UseUrls(options.ListenUrl);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddSingleton(_ => new FamilyAccounts(options, engine));
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

            var accounts = context.RequestServices.GetRequiredService<FamilyAccounts>();
            var caller = accounts.Authenticate(context.Request);
            if (caller is null || accounts.Find(caller.Value) is not { } current)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                return;
            }

            context.Items[CallerKey] = caller.Value;
            context.Items[EngineKey] = current;
            context.Response.Headers.CacheControl = "no-store";
            try { await next(); }
            catch (ObjectDisposedException error) when (caller != Guid.Empty &&
                error.ObjectName == nameof(ReminderEngine) && !context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
            }
        });

        app.MapGet("/health", () => Results.Ok(new
        {
            ok = true,
            timeZone = ShanghaiClock.Id,
            port = options.Port,
        }));

        app.MapGet("/api/status", (HttpContext context) => Results.Ok(CurrentEngine(context).Status()));
        app.MapGet("/api/settings", (HttpContext context) => Results.Ok(CurrentEngine(context).ClientSettings()));
        app.MapPut("/api/settings", async (HttpRequest request) =>
        {
            var e = CurrentEngine(request.HttpContext);
            string body;
            using (var reader = new StreamReader(request.Body))
                body = await reader.ReadToEndAsync();
            if (!SettingsEditor.TryParse(body, out var update, out var error) || update is null)
                return Results.BadRequest(new { error = error ?? SettingsEditor.Unreadable });
            if (!e.TryUpdateSettings(update, out error))
                return Results.BadRequest(new { error });
            return Results.Ok(e.ClientSettings());
        });

        app.MapPost("/api/drinks", async (HttpRequest request) =>
        {
            var e = CurrentEngine(request.HttpContext);
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

        app.MapPost("/api/desktop-ack", async (HttpRequest request) =>
        {
            var e = CurrentEngine(request.HttpContext);
            AckRequest? body = null;
            try { body = await request.ReadFromJsonAsync<AckRequest>(); }
            catch { body = null; }
            return e.AcknowledgeDesktop(body?.Id)
                ? Results.Ok(new { ok = true })
                : Results.NotFound(new { ok = false });
        });

        app.MapGet("/api/records", (HttpContext context, string? date) =>
        {
            var e = CurrentEngine(context);
            if (string.IsNullOrWhiteSpace(date))
                return Results.Ok(e.TodayRecords());
            var day = e.RecordsFor(date);
            return day is null ? Results.BadRequest(new { error = "日期格式应为 yyyy-MM-dd。" }) : Results.Ok(day);
        });
        app.MapGet("/api/history", (HttpContext context, int? days) =>
            Results.Ok(CurrentEngine(context).History(days ?? 30)));
        app.MapGet("/api/family", (HttpContext context, FamilyAccounts accounts) =>
            Results.Ok(accounts.Overview(Caller(context))));
        app.MapPost("/api/family", async (HttpContext context, FamilyAccounts accounts) =>
        {
            if (Caller(context) != Guid.Empty) return Results.StatusCode(403);
            AddFamilyMember? body;
            try { body = await context.Request.ReadFromJsonAsync<AddFamilyMember>(); }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { body = null; }
            if (body is null) return Results.BadRequest(new { error = "无法读取成员信息。" });
            var invitation = accounts.Add(body, out var errorText);
            return invitation is null ? Results.BadRequest(new { error = errorText }) : Results.Ok(invitation);
        });
        app.MapDelete("/api/family/{id:guid}", (HttpContext context, FamilyAccounts accounts, Guid id) =>
        {
            if (Caller(context) != Guid.Empty) return Results.StatusCode(403);
            return accounts.Archive(id) ? Results.Ok(new { ok = true }) : Results.NotFound();
        });
        app.MapGet("/api/family/{id:guid}/settings", (HttpContext context, FamilyAccounts accounts, Guid id) =>
        {
            if (Caller(context) != Guid.Empty) return Results.StatusCode(403);
            return accounts.MemberSettings(id) is { } settings ? Results.Ok(settings) : Results.NotFound();
        });
        app.MapPut("/api/family/{id:guid}/settings", async (HttpContext context, FamilyAccounts accounts, Guid id) =>
        {
            if (Caller(context) != Guid.Empty) return Results.StatusCode(403);
            using var reader = new StreamReader(context.Request.Body);
            if (!TryReadFamilySettings(await reader.ReadToEndAsync(), out var update, out var error))
                return Results.BadRequest(new { error });
            var saved = accounts.UpdateMemberSettings(id, update!, out error);
            return saved is not null ? Results.Ok(saved) : error is null ? Results.NotFound() : Results.BadRequest(new { error });
        });
        app.MapGet("/api/family/{id:guid}/history", (HttpContext context, FamilyAccounts accounts, Guid id, int? days) =>
        {
            if (Caller(context) != Guid.Empty) return Results.StatusCode(403);
            return accounts.Find(id, includeArchived: true) is { } member
                ? Results.Ok(member.History(days ?? 30)) : Results.NotFound();
        });
        app.MapGet("/api/family/{id:guid}/records", (HttpContext context, FamilyAccounts accounts, Guid id, string date) =>
        {
            if (Caller(context) != Guid.Empty) return Results.StatusCode(403);
            if (accounts.Find(id, includeArchived: true) is not { } member) return Results.NotFound();
            var records = member.RecordsFor(date);
            if (records is null) return Results.BadRequest(new { error = "日期格式应为 yyyy-MM-dd。" });
            records.Editable = false;
            return Results.Ok(records);
        });
        app.MapPut("/api/records/total", async (HttpRequest request) =>
        {
            var e = CurrentEngine(request.HttpContext);
            SetTotalRequest? body = null;
            try { body = await request.ReadFromJsonAsync<SetTotalRequest>(); }
            catch { body = null; }
            if (body is null)
                return Results.BadRequest(new { error = "设置内容无法识别。" });
            return e.TrySetTodayTotal(body.TotalMl, out var result, out var error)
                ? Results.Ok(result)
                : Results.BadRequest(new { error });
        });
        app.MapPut("/api/records/{id:guid}", async (Guid id, HttpRequest request) =>
        {
            var e = CurrentEngine(request.HttpContext);
            EditRecordRequest? body = null;
            try { body = await request.ReadFromJsonAsync<EditRecordRequest>(); }
            catch { body = null; }
            if (body is null)
                return Results.BadRequest(new { error = "设置内容无法识别。" });
            if (!e.TryEditRecord(id, body.AmountMl, out var error))
                return error == "找不到这条记录。" ? Results.NotFound(new { error }) : Results.BadRequest(new { error });
            return Results.Ok(e.TodayRecords());
        });
        app.MapDelete("/api/records/{id:guid}", (Guid id, HttpContext context) =>
        {
            var e = CurrentEngine(context);
            return e.TryDeleteRecord(id) ? Results.Ok(e.TodayRecords()) : Results.NotFound(new { error = "找不到这条记录。" });
        });
        app.MapDelete("/api/records", (HttpContext context) =>
        {
            var e = CurrentEngine(context);
            e.TryClearToday();
            return Results.Ok(e.TodayRecords());
        });

        return app;
    }

    private static Guid Caller(HttpContext context) => (Guid)context.Items[CallerKey]!;

    private static bool TryReadFamilySettings(string body, out FamilySettingsUpdate? update, out string? error)
    {
        update = null;
        error = SettingsEditor.Unreadable;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            JsonElement settings = default;
            string? token = null;
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in doc.RootElement.EnumerateObject())
            {
                if (!fields.Add(field.Name)) return false;
                if (field.Name.Equals("settings", StringComparison.OrdinalIgnoreCase)) settings = field.Value;
                else if (field.Name.Equals("pushWardToken", StringComparison.OrdinalIgnoreCase))
                {
                    if (field.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return false;
                    token = field.Value.GetString();
                }
                else return false;
            }
            if (settings.ValueKind != JsonValueKind.Object) return false;
            if (!SettingsEditor.TryParse(settings.GetRawText(), out var parsed, out error) || parsed is null) return false;
            update = new(parsed, token);
            error = null;
            return true;
        }
        catch (JsonException) { return false; }
    }
    private static ReminderEngine CurrentEngine(HttpContext context) => (ReminderEngine)context.Items[EngineKey]!;

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
