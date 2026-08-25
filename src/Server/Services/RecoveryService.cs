namespace LanManagement.Server.Services;

/// <summary>On boot, reconnecting agents become authoritative again; unacknowledged sends are safely redelivered by ID.</summary>
public sealed class RecoveryService(
    ICommandService commands,
    ILogger<RecoveryService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var recovered = await commands.RecoverAfterServerStartAsync(cancellationToken);
        if (recovered > 0)
        {
            logger.LogInformation("Recovered {Count} unacknowledged command(s) after server startup.", recovered);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
