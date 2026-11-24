using System.Threading;

namespace WaterReminder.Client;

/// <summary>Ignores another click until the first action, including its dialog, has finished.</summary>
internal sealed class ClickGuard
{
    private int _busy;

    public bool Enter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

    public void Exit() => Interlocked.Exchange(ref _busy, 0);
}
