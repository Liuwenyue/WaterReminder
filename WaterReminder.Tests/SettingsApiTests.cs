using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using WaterReminder.Server;
using WaterReminder.Services;
using Xunit;

namespace WaterReminder.Tests;

/// <summary>Desktop settings round-trip: validate, persist, and apply without restarting the process.</summary>
public sealed class SettingsApiTests
{
    private static readonly DateTime Tuesday = new(2026, 11, 3, 10, 0, 0);

    [Fact]
    public void Validate_RejectsGoalCupTimesAndEmptyWeek()
    {
        var update = Sample();
        update.DailyGoalMl = 10;
        Assert.Equal("每日目标需为 100–10000 ml 的整数。", SettingsEditor.Validate(update).GoalError);

        update = Sample();
        update.WorkEnd = "08:30";
        Assert.Equal("下班时间必须晚于上班时间。", SettingsEditor.Validate(update).TimeError);

        update = Sample();
        update.QuietStart = "17:00";
        Assert.Equal("免打扰开始必须晚于下班时间。", SettingsEditor.Validate(update).QuietError);

        update = Sample();
        update.MinReminderGapMinutes = 1;
        Assert.Equal("最小间隔需为 5–180 分钟。", SettingsEditor.Validate(update).GapError);

        update = Sample();
        update.ActiveDays = [false, false, false, false, false, false, false];
        Assert.Equal("至少选择一天。", SettingsEditor.Validate(update).DaysError);
    }

    [Fact]
    public void TryParse_RejectsSecretFieldsWithoutEchoingThem()
    {
        const string secret = "hlk_should_not_appear";
        var json = $$"""{"dailyGoalMl":3000,"pushWardApiToken":"{{secret}}","workStart":"08:30"}""";
        Assert.False(SettingsEditor.TryParse(json, out _, out var error));
        Assert.Equal(SettingsEditor.SecretRejected, error);
        Assert.DoesNotContain(secret, error);

        Assert.False(SettingsEditor.TryParse("""{"apiToken":"desktop-api-token","dailyGoalMl":3000}""", out _, out error));
        Assert.Equal(SettingsEditor.SecretRejected, error);
        Assert.DoesNotContain("desktop-api-token", error);
    }

    [Fact]
    public void TryApply_DoesNotReplacePhoneToken()
    {
        var target = new UserSettings { PushWardApiToken = "hlk_file_token" };
        var update = Sample();
        update.DailyGoalMl = 2400;
        update.MinReminderGapMinutes = 45;
        Assert.True(SettingsEditor.TryApply(target, update, out var error));
        Assert.Null(error);
        Assert.Equal("hlk_file_token", target.PushWardApiToken);
        Assert.Equal(2400, target.DailyGoalMl);
        Assert.Equal(45, target.MinReminderGapMinutes);
        Assert.Equal(370, target.WorkCupCapacityMl);
    }

    [Fact]
    public async Task PutSettings_PersistsAndAppliesWithoutRestart()
    {
        var dir = TempDir();
        WebApplication? app = null;
        try
        {
            new StorageService(dir).SaveSettings(new UserSettings { PushWardApiToken = "hlk_file_token" });
            var engine = new ReminderEngine(new ReminderEngineOptions
            {
                DataDirectory = dir,
                Clock = () => Tuesday,
                PushWardToken = () => null,
                PollAnswers = false,
            });
            app = ServerHost.Build(
                ServerOptions.Create(dir, "desktop-api-token", 0, "127.0.0.1", requireApiToken: true, runScheduler: false),
                engine);
            await app.StartAsync();
            var baseUrl = app.Urls.Single();

            using var anonymous = new HttpClient { BaseAddress = new Uri(baseUrl) };
            using var denied = await anonymous.PutAsJsonAsync("api/settings", Sample(), ReminderApiClient.Json);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

            using var client = new ReminderApiClient(baseUrl, "desktop-api-token");
            var loaded = await client.GetSettingsAsync();
            Assert.True(loaded.Ok);
            Assert.Equal(3000, loaded.Value!.DailyGoalMl);
            Assert.Equal(20, loaded.Value.MinReminderGapMinutes);
            var before = await client.GetStatusAsync();
            Assert.True(before.Value!.ShowDesktop);
            Assert.True(before.Value.SuggestedMl > 0);

            var update = SettingsEditor.FromResponse(loaded.Value);
            update.DailyGoalMl = 2400;
            update.WorkCupMl = 400;
            update.HomeCupMl = 700;
            update.WorkStart = "11:00";
            update.MinReminderGapMinutes = 30;
            update.NoDrinkBeforeEndMinutes = 20;
            update.MobilePushEnabled = false;

            var saved = await client.SaveSettingsAsync(update);
            Assert.True(saved.Ok);
            Assert.Equal(2400, saved.Value!.DailyGoalMl);
            Assert.Equal(400, saved.Value.WorkCupMl);
            Assert.Equal(700, saved.Value.HomeCupMl);
            Assert.Equal("11:00", saved.Value.WorkStart);
            Assert.Equal(30, saved.Value.MinReminderGapMinutes);
            Assert.Equal(20, saved.Value.NoDrinkBeforeEndMinutes);
            Assert.False(saved.Value.MobilePushEnabled);

            var again = await client.GetSettingsAsync();
            Assert.Equal(2400, again.Value!.DailyGoalMl);
            Assert.Equal("11:00", again.Value.WorkStart);

            var status = await client.GetStatusAsync();
            Assert.Equal(2400, status.Value!.GoalMl);
            Assert.Equal(400, status.Value.WorkCupMl);
            Assert.Equal("BeforeWork", status.Value.Phase);
            Assert.False(status.Value.ShowDesktop);
            Assert.False(status.Value.PushWindow);
            Assert.Equal(30, status.Value.MinReminderGapMinutes);

            var file = await File.ReadAllTextAsync(Path.Combine(dir, "settings.json"));
            Assert.Contains("2400", file);
            Assert.Contains("hlk_file_token", file);
            Assert.DoesNotContain("desktop-api-token", file);
            AssertNoSecrets(JsonSerializer.Serialize(saved.Value, ReminderApiClient.Json), "hlk_file_token");
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
    public async Task PutSettings_RejectsSecretsAndInvalidValuesWithoutWritingThem()
    {
        var dir = TempDir();
        WebApplication? app = null;
        try
        {
            var engine = new ReminderEngine(new ReminderEngineOptions
            {
                DataDirectory = dir,
                Clock = () => Tuesday,
                PushWardToken = () => "hlk_from_env",
                PollAnswers = false,
            });
            app = ServerHost.Build(
                ServerOptions.Create(dir, "desktop-api-token", 0, "127.0.0.1", requireApiToken: true, runScheduler: false),
                engine);
            await app.StartAsync();

            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "desktop-api-token");

            using var secret = new StringContent(
                """{"dailyGoalMl":3000,"pushWardApiToken":"hlk_from_env","apiToken":"desktop-api-token"}""",
                Encoding.UTF8,
                "application/json");
            using var secretResponse = await http.PutAsync("api/settings", secret);
            Assert.Equal(HttpStatusCode.BadRequest, secretResponse.StatusCode);
            var secretBody = await secretResponse.Content.ReadAsStringAsync();
            Assert.Contains(SettingsEditor.SecretRejected, secretBody);
            Assert.DoesNotContain("hlk_from_env", secretBody);
            Assert.DoesNotContain("desktop-api-token", secretBody);

            var bad = Sample();
            bad.DailyGoalMl = 10;
            using var badResponse = await http.PutAsJsonAsync("api/settings", bad, ReminderApiClient.Json);
            Assert.Equal(HttpStatusCode.BadRequest, badResponse.StatusCode);

            using var client = new ReminderApiClient(app.Urls.Single(), "desktop-api-token");
            var still = await client.GetSettingsAsync();
            Assert.Equal(3000, still.Value!.DailyGoalMl);

            var file = File.Exists(Path.Combine(dir, "settings.json"))
                ? await File.ReadAllTextAsync(Path.Combine(dir, "settings.json"))
                : "";
            Assert.DoesNotContain("hlk_from_env", file);
            Assert.DoesNotContain("desktop-api-token", file);
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
    public void WiderGap_MovesTheNextReminderOnTheSameEngine()
    {
        var dir = TempDir();
        try
        {
            using var engine = new ReminderEngine(new ReminderEngineOptions
            {
                DataDirectory = dir,
                Clock = () => Tuesday,
                PollAnswers = false,
            });
            var before = engine.Status();
            Assert.True(before.SuggestedMl > 0);

            var update = SettingsEditor.FromResponse(engine.ClientSettings());
            update.MinReminderGapMinutes = 120;
            Assert.True(engine.TryUpdateSettings(update, out var error));
            Assert.Null(error);

            var after = engine.Status();
            Assert.Equal(120, after.MinReminderGapMinutes);
            Assert.Equal(0, after.SuggestedMl);
            Assert.Equal("2026-11-03T10:30:00", after.NextReminder);
            Assert.Equal(120, engine.ClientSettings().MinReminderGapMinutes);
        }
        finally { TryDelete(dir); }
    }

    private static SettingsUpdate Sample() => new()
    {
        DailyGoalMl = 3000,
        WorkCupMl = 370,
        HomeCupMl = 800,
        WorkStart = "08:30",
        WorkEnd = "17:30",
        LunchEnabled = true,
        LunchStart = "11:50",
        LunchEnd = "13:30",
        NoDrinkBeforeEndMinutes = 30,
        QuietStart = "23:00",
        MinReminderGapMinutes = 20,
        MobilePushEnabled = true,
        ActiveDays = [true, true, true, true, true, false, false],
    };

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

    private static string TempDir()
        => Path.Combine(Path.GetTempPath(), "WaterReminderTests", Guid.NewGuid().ToString("N"));

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}
