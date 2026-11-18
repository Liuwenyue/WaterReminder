namespace WaterReminder.Server;

/// <summary>30-second reminder loop. Failures stay inside the process so a bad tick cannot exit the service.</summary>
internal sealed class ReminderScheduler : BackgroundService
{
    private readonly FamilyAccounts _accounts;

    public ReminderScheduler(FamilyAccounts accounts) => _accounts = accounts;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.WhenAll(_accounts.ActiveEngines().Select(engine => engine.TickAsync(stoppingToken)))
                    .ConfigureAwait(false);
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
