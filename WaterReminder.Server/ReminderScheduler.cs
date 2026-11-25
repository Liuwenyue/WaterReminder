using Microsoft.Extensions.Logging;

namespace WaterReminder.Server;

/// <summary>30-second reminder loop. Failures stay inside the process so a bad tick cannot exit the service.</summary>
internal sealed class ReminderScheduler : BackgroundService
{
    private readonly FamilyAccounts _accounts;
    private readonly ILogger<ReminderScheduler> _logger;

    public ReminderScheduler(FamilyAccounts accounts, ILogger<ReminderScheduler> logger)
    {
        _accounts = accounts;
        _logger = logger;
    }

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
            catch (Exception ex)
            {
                // A failed publish or a bad settings file must not take the process down.
                // Type + message only: tick exceptions come from storage/network code
                // whose messages hold file names at most, never tokens or headers.
                _logger.LogError("Reminder tick failed: {ErrorType}: {Message}", ex.GetType().Name, ex.Message);
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
