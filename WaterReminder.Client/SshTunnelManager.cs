using System.Diagnostics;
using System.Net.Sockets;
using WaterReminder.Services;

namespace WaterReminder.Client;

/// <summary>
/// Starts and watches an optional <c>ssh -N -L</c> process owned by this client.
/// Does not kill SSH processes it did not start.
/// </summary>
internal sealed class SshTunnelManager : IDisposable
{
    private readonly TunnelOptions _options;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;
    private int _ownedPid;
    private Task? _loop;
    private bool _connecting;

    public SshTunnelManager(TunnelOptions options) => _options = options;

    public bool Enabled => SshTunnelSpec.IsConfigured(_options);
    public bool Connecting
    {
        get { lock (_gate) return _connecting; }
    }

    public void Start()
    {
        if (!Enabled) return;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task EnsureReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (!Enabled) return;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await PortOpenAsync(_options.LocalPort, ct).ConfigureAwait(false))
                return;
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.Zero;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await PortOpenAsync(_options.LocalPort, ct).ConfigureAwait(false))
                {
                    SetConnecting(false);
                    backoff = TimeSpan.Zero;
                    if (!await WaitUntilUnhealthyAsync(ct).ConfigureAwait(false))
                        break;
                    backoff = SshTunnelSpec.NextBackoff(backoff);
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                    continue;
                }

                SetConnecting(true);
                if (!StartProcess())
                {
                    backoff = SshTunnelSpec.NextBackoff(backoff);
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                    continue;
                }

                // Give the forward a moment, then probe /health via TCP readiness.
                await Task.Delay(800, ct).ConfigureAwait(false);
                if (await PortOpenAsync(_options.LocalPort, ct).ConfigureAwait(false))
                {
                    SetConnecting(false);
                    backoff = TimeSpan.Zero;
                    if (!await WaitUntilUnhealthyAsync(ct).ConfigureAwait(false))
                        break;
                }

                StopOwnedProcess();
                backoff = SshTunnelSpec.NextBackoff(backoff);
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                StopOwnedProcess();
                backoff = SshTunnelSpec.NextBackoff(backoff);
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }

        SetConnecting(false);
        StopOwnedProcess();
    }

    private async Task<bool> WaitUntilUnhealthyAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            if (!ProcessAlive() || !await PortOpenAsync(_options.LocalPort, ct).ConfigureAwait(false))
            {
                failures++;
                if (failures >= 3) return true;
            }
            else
            {
                failures = 0;
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        return false;
    }

    private bool StartProcess()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false }) return true;
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = SshTunnelSpec.Executable(_options),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                foreach (var arg in SshTunnelSpec.BuildArguments(_options))
                    start.ArgumentList.Add(arg);

                var process = Process.Start(start);
                if (process is null) return false;
                _process = process;
                _ownedPid = process.Id;
                return true;
            }
            catch
            {
                _process = null;
                _ownedPid = 0;
                return false;
            }
        }
    }

    private bool ProcessAlive()
    {
        lock (_gate)
        {
            try { return _process is { HasExited: false }; }
            catch { return false; }
        }
    }

    private void StopOwnedProcess()
    {
        Process? process;
        int pid;
        lock (_gate)
        {
            process = _process;
            pid = _ownedPid;
            _process = null;
            _ownedPid = 0;
        }
        if (process is null) return;
        try
        {
            if (!process.HasExited && process.Id == pid)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch { }
        finally
        {
            try { process.Dispose(); } catch { }
        }
    }

    private void SetConnecting(bool value)
    {
        lock (_gate) _connecting = value;
    }

    public static async Task<bool> PortOpenAsync(int port, CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(1));
            await client.ConnectAsync("127.0.0.1", port, linked.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        StopOwnedProcess();
        _cts.Dispose();
    }
}
