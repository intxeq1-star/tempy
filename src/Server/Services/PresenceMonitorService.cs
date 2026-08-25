namespace LanManagement.Server.Services;

public sealed class PresenceMonitorService(
    IDeviceService devices,
    IOptions<LanManagement.Server.Options.ServerOptions> options,
    ILogger<PresenceMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PresenceSweepSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var expired = await devices.MarkExpiredDevicesOfflineAsync(stoppingToken);
                if (expired > 0)
                {
                    logger.LogInformation("Presence sweep marked {Count} device(s) offline.", expired);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Presence sweep failed.");
            }
        }
    }
}
