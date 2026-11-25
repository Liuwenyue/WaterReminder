using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WaterReminder.Services;

/// <summary>
/// Minimal append-only file logger. One file per day under
/// %LocalAppData%\WaterReminder\logs\ (waterreminder-yyyy-MM-dd.log),
/// rolling to a .1/.2... suffix once a file exceeds 5 MB.
/// Logging must never throw: every write is guarded, so a logging failure
/// is silently dropped instead of crashing the caller.
/// As a last line of defense, every line is scrubbed before it is written:
/// Bearer tokens and wr_ access codes are replaced, so a caller that
/// accidentally logs a credential (e.g. inside an exception message) cannot
/// leak it into the log file.
/// </summary>
public sealed class FileLogger
{
    /// <summary>Default per-file size limit before rolling to a new file.</summary>
    public const long DefaultMaxFileBytes = 5L * 1024 * 1024;

    private const int MaxRollover = 99;

    // Bearer <token> as it appears in an Authorization header dump.
    private static readonly Regex BearerTokenPattern = new(
        @"Bearer\s+[A-Za-z0-9\-._~+/=]+", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Desktop access code: "wr_" + 32 random bytes as 64 hex chars.
    private static readonly Regex AccessCodePattern = new(
        @"wr_[0-9a-fA-F]{64}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Shared logger writing to the default log directory.</summary>
    public static FileLogger Default { get; } = new();

    /// <summary>Default log directory: %LocalAppData%\WaterReminder\logs\.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WaterReminder", "logs");

    private readonly string _directory;
    private readonly long _maxFileBytes;
    private readonly object _gate = new();
    private string _currentPath = string.Empty;
    private string _currentDate = string.Empty;
    private long _currentSize;

    public FileLogger() : this(null, DefaultMaxFileBytes) { }

    /// <param name="directory">Log directory; null selects <see cref="DefaultDirectory"/>.</param>
    /// <param name="maxFileBytes">Per-file size limit; mainly useful for tests.</param>
    public FileLogger(string? directory, long maxFileBytes = DefaultMaxFileBytes)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory;
        _maxFileBytes = Math.Max(1, maxFileBytes);
    }

    /// <summary>The directory this logger writes to.</summary>
    public string LogDirectory => _directory;

    public void Info(string message) => Write("INFO", message, null);

    public void Warning(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            var line = Format(level, message, exception);
            lock (_gate)
            {
                var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var path = ResolvePathLocked(today);
                if (path is null) return;
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(line);
                writer.Flush();
                _currentSize += Encoding.UTF8.GetByteCount(line);
            }
        }
        catch
        {
            // Logging must never throw: drop the entry.
        }
    }

    private string? ResolvePathLocked(string today)
    {
        if (_currentPath.Length > 0 && _currentDate == today && _currentSize < _maxFileBytes)
            return _currentPath;

        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch
        {
            return null;
        }

        var baseName = $"waterreminder-{today}";
        for (var i = 0; i <= MaxRollover; i++)
        {
            var name = i == 0 ? baseName + ".log" : $"{baseName}.{i}.log";
            var path = Path.Combine(_directory, name);
            try
            {
                if (!File.Exists(path))
                {
                    _currentPath = path;
                    _currentDate = today;
                    _currentSize = 0;
                    return path;
                }
                var size = new FileInfo(path).Length;
                if (size < _maxFileBytes)
                {
                    _currentPath = path;
                    _currentDate = today;
                    _currentSize = size;
                    return path;
                }
            }
            catch
            {
                return null;
            }
        }

        // Every rollover slot is full: keep appending to the last one rather
        // than losing logs. Size is reset so this path is reused directly.
        _currentPath = Path.Combine(_directory, $"{baseName}.{MaxRollover}.log");
        _currentDate = today;
        _currentSize = 0;
        return _currentPath;
    }

    private static string Format(string level, string message, Exception? exception)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var text = $"[{timestamp}] [{level}] {Scrub(message)}{Environment.NewLine}";
        if (exception is not null)
            text += Scrub(exception.ToString()) + Environment.NewLine;
        return text;
    }

    /// <summary>
    /// Removes credentials from a log line. Callers must still avoid logging
    /// secrets; this only catches accidents (a token inside an exception
    /// message, a pasted Authorization header).
    /// </summary>
    private static string Scrub(string text)
    {
        text = BearerTokenPattern.Replace(text, "Bearer [redacted]");
        text = AccessCodePattern.Replace(text, "[redacted-access-code]");
        return text;
    }
}
