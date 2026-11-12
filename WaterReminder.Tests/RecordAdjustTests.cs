using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using WaterReminder.Server;
using WaterReminder.Services;
using Xunit;

namespace WaterReminder.Tests;

public sealed class RecordAdjustTests
{
    private static readonly DateTime Tuesday = new(2026, 11, 3, 10, 0, 0);

    [Fact]
    public void SetTotal_LowersWithAdjustEvent_AndSameTotalIsNoOp()
    {
        var dir = TempDir();
        try
        {
            using var engine = Engine(dir);
            Assert.Equal(370, engine.LogDrink(370, "cup"));

            Assert.True(engine.TrySetTodayTotal(0, out var zero, out var error));
            Assert.Null(error);
            Assert.Equal(-370, zero!.DeltaMl);
            Assert.Equal(0, zero.ConsumedMl);
            Assert.True(zero.Changed);
            var adjust = Assert.Single(engine.TodayRecords().Events, x => x.Source == "adjust");
            Assert.Equal(-370, adjust.AmountMl);

            Assert.True(engine.TrySetTodayTotal(100, out var raised, out error));
            Assert.Null(error);
            Assert.Equal(100, raised!.DeltaMl);
            Assert.Equal(100, raised.ConsumedMl);

            var before = engine.TodayRecords().Events.Count;
            Assert.True(engine.TrySetTodayTotal(100, out var same, out error));
            Assert.Null(error);
            Assert.False(same!.Changed);
            Assert.Equal(0, same.DeltaMl);
            Assert.Equal(before, engine.TodayRecords().Events.Count);
            Assert.Equal(100, engine.Status().ConsumedMl);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void SetTotal_RejectsInvalidTotalsWithoutWriting()
    {
        var dir = TempDir();
        try
        {
            using var engine = Engine(dir);
            engine.LogDrink(370, "cup");
            Assert.False(engine.TrySetTodayTotal(-5, out _, out var error));
            Assert.False(string.IsNullOrEmpty(error));
            Assert.False(engine.TrySetTodayTotal(20001, out _, out error));
            Assert.False(string.IsNullOrEmpty(error));
            Assert.Equal(370, engine.TodayRecords().ConsumedMl);
            Assert.DoesNotContain(engine.TodayRecords().Events, x => x.Source == "adjust");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task RecordsApi_EditsDeletesAndClears_WithoutLeakingSecrets()
    {
        var dir = TempDir();
        WebApplication? app = null;
        try
        {
            var engine = new ReminderEngine(new ReminderEngineOptions
            {
                DataDirectory = dir,
                Clock = () => Tuesday,
                PushWardToken = () => "hlk_file_token",
                PollAnswers = false,
            });
            app = ServerHost.Build(
                ServerOptions.Create(dir, "desktop-api-token", 0, "127.0.0.1", requireApiToken: true, runScheduler: false),
                engine);
            await app.StartAsync();
            var baseUrl = app.Urls.Single();

            using var anonymous = new HttpClient { BaseAddress = new Uri(baseUrl) };
            using var denied = await anonymous.PutAsJsonAsync("api/records/total", new SetTotalRequest { TotalMl = 0 }, ReminderApiClient.Json);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

            using var client = new ReminderApiClient(baseUrl, "desktop-api-token");
            var drink = await client.LogAmountAsync(800);
            Assert.True(drink.Ok);
            Assert.Equal(800, drink.Value!.ConsumedMl);

            var lowered = await client.SetTodayTotalAsync(300);
            Assert.True(lowered.Ok);
            Assert.Equal(-500, lowered.Value!.DeltaMl);
            Assert.Equal(300, lowered.Value.ConsumedMl);
            Assert.True(lowered.Value.Changed);

            var records = await client.GetRecordsAsync();
            Assert.True(records.Ok);
            Assert.Equal(300, records.Value!.ConsumedMl);
            var adjust = Assert.Single(records.Value.Events, x => x.Source == "adjust");
            Assert.Equal(-500, adjust.AmountMl);
            var body = System.Text.Json.JsonSerializer.Serialize(records.Value, ReminderApiClient.Json);
            Assert.DoesNotContain("hlk_file_token", body);
            Assert.DoesNotContain("desktop-api-token", body);

            var same = await client.SetTodayTotalAsync(300);
            Assert.True(same.Ok);
            Assert.False(same.Value!.Changed);
            Assert.Equal(2, (await client.GetRecordsAsync()).Value!.Events.Count);

            var invalid = await client.SetTodayTotalAsync(20001);
            Assert.False(invalid.Ok);
            Assert.False(string.IsNullOrEmpty(invalid.Error));
            Assert.Equal(300, (await client.GetRecordsAsync()).Value!.ConsumedMl);

            var edited = await client.EditRecordAsync(adjust.Id, -200);
            Assert.True(edited.Ok);
            Assert.Equal(600, edited.Value!.ConsumedMl);

            var cup = edited.Value.Events.Single(x => x.Source == "desktop");
            var deleted = await client.DeleteRecordAsync(cup.Id);
            Assert.True(deleted.Ok);
            Assert.Equal(0, deleted.Value!.ConsumedMl);
            Assert.Equal(-200, Assert.Single(deleted.Value.Events).AmountMl);

            var cleared = await client.ClearTodayAsync();
            Assert.True(cleared.Ok);
            Assert.Empty(cleared.Value!.Events);
            Assert.Equal(0, cleared.Value.ConsumedMl);
            Assert.Equal(0, engine.Status().ConsumedMl);
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

    private static ReminderEngine Engine(string dir) => new(new ReminderEngineOptions
    {
        DataDirectory = dir,
        Clock = () => Tuesday,
        PollAnswers = false,
    });

    private static string TempDir()
        => Path.Combine(Path.GetTempPath(), "WaterReminderTests", Guid.NewGuid().ToString("N"));

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}
