using WaterReminder;
using WaterReminder.Services;
using Xunit;

namespace WaterReminder.Tests;

public sealed class SshTunnelAndHistoryTests
{
    [Fact]
    public void TunnelArgs_UseLocalForwardAndHardenedSshOptions()
    {
        var options = new TunnelOptions
        {
            Enabled = true,
            Host = "YOUR_VM_HOST",
            User = "YOUR_SSH_USER",
            IdentityFile = @"C:\Users\YOU\.ssh\id_ed25519",
            LocalPort = 8787,
            RemotePort = 8787,
        };
        Assert.True(SshTunnelSpec.IsConfigured(options));
        var args = SshTunnelSpec.BuildArguments(options);
        Assert.Contains("-N", args);
        Assert.Contains("-L", args);
        Assert.Contains("8787:127.0.0.1:8787", args);
        Assert.Contains("BatchMode=yes", args);
        Assert.Contains("IdentitiesOnly=yes", args);
        Assert.Contains("StrictHostKeyChecking=yes", args);
        Assert.Contains("ExitOnForwardFailure=yes", args);
        Assert.Contains("ServerAliveInterval=30", args);
        Assert.Contains("ServerAliveCountMax=3", args);
        Assert.Contains("-i", args);
        Assert.Contains(@"C:\Users\YOU\.ssh\id_ed25519", args);
        Assert.Equal("YOUR_SSH_USER@YOUR_VM_HOST", args[^1]);
        Assert.DoesNotContain("hlk_", string.Join(' ', args));
    }

    [Fact]
    public void TunnelBackoff_CapsAtSixtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), SshTunnelSpec.NextBackoff(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(10), SshTunnelSpec.NextBackoff(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(40), SshTunnelSpec.NextBackoff(TimeSpan.FromSeconds(20)));
        Assert.Equal(TimeSpan.FromSeconds(60), SshTunnelSpec.NextBackoff(TimeSpan.FromSeconds(40)));
        Assert.Equal(TimeSpan.FromSeconds(60), SshTunnelSpec.NextBackoff(TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void IncompleteTunnel_IsNotConfigured()
    {
        Assert.False(SshTunnelSpec.IsConfigured(new TunnelOptions { Enabled = true, Host = "x" }));
        Assert.False(SshTunnelSpec.IsConfigured(new TunnelOptions { Enabled = false, Host = "x", User = "y" }));
    }

    [Fact]
    public void ReadyTimeout_DefaultsToFifteenSeconds_AndRejectsInvalid()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), SshTunnelSpec.DefaultReadyTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(250), SshTunnelSpec.ReadyPollInterval);
        Assert.Equal(15, new TunnelOptions().ReadyTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(15), SshTunnelSpec.ReadyTimeout(new TunnelOptions()));
        Assert.Equal(TimeSpan.FromSeconds(30), SshTunnelSpec.ReadyTimeout(new TunnelOptions { ReadyTimeoutSeconds = 30 }));
        Assert.Equal(TimeSpan.FromSeconds(15), SshTunnelSpec.ReadyTimeout(new TunnelOptions { ReadyTimeoutSeconds = 0 }));
        Assert.Equal(TimeSpan.FromSeconds(15), SshTunnelSpec.ReadyTimeout(new TunnelOptions { ReadyTimeoutSeconds = -1 }));
        Assert.Equal(TimeSpan.FromSeconds(15), SshTunnelSpec.ReadyTimeout(new TunnelOptions { ReadyTimeoutSeconds = double.NaN }));
    }

    [Fact]
    public async Task WaitUntilReady_SucceedsWhenPortOpensAfterSeveralPolls()
    {
        var probes = 0;
        var result = await SshTunnelSpec.WaitUntilReadyAsync(
            TimeSpan.FromSeconds(2),
            _ =>
            {
                probes++;
                return Task.FromResult(probes >= 5);
            },
            () => true,
            TimeSpan.FromMilliseconds(20));
        Assert.Equal(TunnelReadyWaitResult.Ready, result);
        Assert.True(probes >= 5);
    }

    [Fact]
    public async Task WaitUntilReady_FailsImmediatelyWhenSshProcessExits()
    {
        var probes = 0;
        var alive = true;
        var result = await SshTunnelSpec.WaitUntilReadyAsync(
            TimeSpan.FromSeconds(5),
            _ =>
            {
                probes++;
                // After the first unsuccessful probe, pretend ssh died.
                alive = false;
                return Task.FromResult(false);
            },
            () => alive,
            TimeSpan.FromMilliseconds(20));
        Assert.Equal(TunnelReadyWaitResult.ProcessExited, result);
        Assert.True(probes >= 1);
    }

    [Fact]
    public async Task WaitUntilReady_TimesOutWhenPortNeverOpens()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await SshTunnelSpec.WaitUntilReadyAsync(
            TimeSpan.FromMilliseconds(120),
            _ => Task.FromResult(false),
            () => true,
            TimeSpan.FromMilliseconds(30));
        sw.Stop();
        Assert.Equal(TunnelReadyWaitResult.TimedOut, result);
        Assert.InRange(sw.ElapsedMilliseconds, 80, 2000);
    }

    [Fact]
    public void History_ReturnsPastDaysReadOnly_AndDateQueryWorks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WaterReminderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var storage = new StorageService(dir);
            storage.SaveSettings(new UserSettings { DailyGoalMl = 3000 });
            storage.SaveDailyRecord(new DailyRecord
            {
                Date = "2026-11-01",
                Events = [new WaterEvent { At = new DateTime(2026, 11, 1, 10, 0, 0), AmountMl = 370, Source = "cup" }],
            });
            storage.SaveDailyRecord(new DailyRecord
            {
                Date = "2026-11-03",
                Events =
                [
                    new WaterEvent { At = new DateTime(2026, 11, 3, 9, 0, 0), AmountMl = 370, Source = "cup" },
                    new WaterEvent { At = new DateTime(2026, 11, 3, 11, 0, 0), AmountMl = 370, Source = "cup" },
                ],
            });

            using var engine = new ReminderEngine(new ReminderEngineOptions
            {
                DataDirectory = dir,
                Clock = () => new DateTime(2026, 11, 3, 12, 0, 0),
                PollAnswers = false,
            });

            var history = engine.History(30);
            Assert.Equal(2, history.Days.Count);
            Assert.Equal("2026-11-03", history.Days[0].Date);
            Assert.Equal(740, history.Days[0].ConsumedMl);
            Assert.Equal(2, history.Days[0].EventCount);

            var past = engine.RecordsFor("2026-11-01");
            Assert.NotNull(past);
            Assert.False(past!.Editable);
            Assert.Equal(370, past.ConsumedMl);
            Assert.Single(past.Events);

            var today = engine.RecordsFor("2026-11-03");
            Assert.NotNull(today);
            Assert.True(today!.Editable);
            Assert.Equal(740, today.ConsumedMl);

            Assert.Null(engine.RecordsFor("11/01/2026"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void CompactPauseStatus_ShowsUntilTime()
    {
        var s = new UserSettings
        {
            RemindersPaused = true,
            PauseUntil = new DateTime(2026, 11, 3, 10, 5, 0),
        };
        var plan = new ReminderPlan(0, TimeSpan.Zero, null, 0, 0);
        var now = new DateTime(2026, 11, 3, 9, 40, 0);
        Assert.Equal("暂停至 10:05", WaterCalculator.StatusLine(s, new DailyRecord(), plan, now, compact: true));
        Assert.Equal("提醒已暂停至 10:05", WaterCalculator.StatusLine(s, new DailyRecord(), plan, now, compact: false));

        s.PauseUntil = new DateTime(2026, 11, 4, 0, 0, 0);
        Assert.Equal("今日暂停", WaterCalculator.StatusLine(s, new DailyRecord(), plan, now, compact: true));
    }
}
