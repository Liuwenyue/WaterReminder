namespace WaterReminder.Services;

/// <summary>
/// Optional SSH local-forward settings from client.json. Pure data + argument building (unit-tested).
/// Host / identity paths stay on the PC; the repository only ships placeholders.
/// </summary>
public sealed class TunnelOptions
{
    public bool Enabled { get; set; }
    public string SshPath { get; set; } = "ssh";
    public string Host { get; set; } = "";
    public string User { get; set; } = "";
    public string IdentityFile { get; set; } = "";
    public int RemotePort { get; set; } = 8787;
    public int LocalPort { get; set; } = 8787;
}

public static class SshTunnelSpec
{
    public static bool IsConfigured(TunnelOptions? options)
        => options is { Enabled: true }
           && !string.IsNullOrWhiteSpace(options.Host)
           && !string.IsNullOrWhiteSpace(options.User)
           && options.LocalPort is > 0 and < 65536
           && options.RemotePort is > 0 and < 65536;

    public static string Executable(TunnelOptions options)
        => string.IsNullOrWhiteSpace(options.SshPath) ? "ssh" : options.SshPath.Trim();

    /// <summary>Arguments for <c>ssh -N -L …</c>. Identity file is optional.</summary>
    public static IReadOnlyList<string> BuildArguments(TunnelOptions options)
    {
        var user = options.User.Trim();
        var host = options.Host.Trim();
        var args = new List<string>
        {
            "-N",
            "-L", $"{options.LocalPort}:127.0.0.1:{options.RemotePort}",
            "-o", "BatchMode=yes",
            "-o", "IdentitiesOnly=yes",
            "-o", "StrictHostKeyChecking=yes",
            "-o", "ExitOnForwardFailure=yes",
            "-o", "ServerAliveInterval=30",
            "-o", "ServerAliveCountMax=3",
        };
        if (!string.IsNullOrWhiteSpace(options.IdentityFile))
        {
            args.Add("-i");
            args.Add(options.IdentityFile.Trim());
        }
        args.Add($"{user}@{host}");
        return args;
    }

    /// <summary>Backoff after a failed tunnel or health check: 5s, 10s, 20s, 40s, capped at 60s.</summary>
    public static TimeSpan NextBackoff(TimeSpan previous)
    {
        if (previous <= TimeSpan.Zero) return TimeSpan.FromSeconds(5);
        var next = TimeSpan.FromSeconds(Math.Min(60, previous.TotalSeconds * 2));
        return next < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : next;
    }
}
