using System.Net.Http;
using WaterReminder.Services;
using Xunit;

namespace WaterReminder.Tests;

public sealed class LoggingTests
{
    [Fact]
    public void WritesTimestampedLineToDatedFile()
    {
        var dir = TempDir();
        try
        {
            var logger = new FileLogger(dir);
            logger.Info("hello log");

            var path = Path.Combine(dir, $"waterreminder-{DateTime.Now:yyyy-MM-dd}.log");
            var text = File.ReadAllText(path);
            Assert.Contains("[INFO] hello log", text);
            Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]", text);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ErrorIncludesExceptionTypeMessageAndStack()
    {
        var dir = TempDir();
        try
        {
            var logger = new FileLogger(dir);
            Exception ex;
            try { throw new InvalidOperationException("boom"); }
            catch (Exception caught) { ex = caught; }
            logger.Error("something failed", ex);

            var path = Path.Combine(dir, $"waterreminder-{DateTime.Now:yyyy-MM-dd}.log");
            var text = File.ReadAllText(path);
            Assert.Contains("InvalidOperationException", text);
            Assert.Contains("boom", text);
            Assert.Contains("ErrorIncludesExceptionTypeMessageAndStack", text);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void NeverThrowsWhenDirectoryIsUnusable()
    {
        var file = Path.Combine(Path.GetTempPath(), "waterreminder-logger-test-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "not a directory");
        try
        {
            var logger = new FileLogger(Path.Combine(file, "logs"));
            var thrown = Record.Exception(() => logger.Error("no crash", new InvalidOperationException("x")));
            Assert.Null(thrown);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void RollsOverWhenFileExceedsLimit()
    {
        var dir = TempDir();
        try
        {
            var logger = new FileLogger(dir, maxFileBytes: 100);
            logger.Info(new string('a', 200));
            logger.Info("second line");

            var date = DateTime.Now.ToString("yyyy-MM-dd");
            Assert.True(File.Exists(Path.Combine(dir, $"waterreminder-{date}.log")));
            Assert.True(File.Exists(Path.Combine(dir, $"waterreminder-{date}.1.log")));
            Assert.Contains("second line", File.ReadAllText(Path.Combine(dir, $"waterreminder-{date}.1.log")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ApiClientFailureCarriesDiagnostic()
    {
        using var http = new HttpClient(new ThrowingHandler());
        using var client = new ReminderApiClient("http://localhost:1", "test-token", http);

        var result = await client.GetStatusAsync();

        Assert.False(result.Ok);
        Assert.Equal(0, result.StatusCode);
        Assert.Contains("HttpRequestException", result.Diagnostic);
        Assert.Contains("simulated offline", result.Diagnostic);
        Assert.Null(result.Error); // user-visible text unchanged
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "waterreminder-logger-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("simulated offline");
    }
}
