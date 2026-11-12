using WaterReminder.Services;

namespace WaterReminder.Server;

/// <summary>30-second reminder loop. Failures stay inside the process so a bad tick cannot exit the service.</summary>
public sealed class ReminderScheduler : BackgroundService
{
    private readonly ReminderEngine _engine;

    public ReminderScheduler(ReminderEngine engine) => _engine = engine;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _engine.TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // A failed publish or a bad settings file must not take the process down.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
