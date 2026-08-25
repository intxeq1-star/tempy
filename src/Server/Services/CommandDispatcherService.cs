using System.Net.WebSockets;
using LanManagement.Server.Data;
using LanManagement.Server.Domain;
using LanManagement.Server.Protocol;
using LanManagement.Server.Transport;
using Microsoft.EntityFrameworkCore;

namespace LanManagement.Server.Services;

/// <summary>
/// Delivery mechanism only: it never creates commands. A job remains in SQLite before, during,
/// and after send attempts, and is redelivered with the same command_id after failure/reconnect.
/// </summary>
public sealed class CommandDispatcherService(
    IDbContextFactory<ManagementDbContext> contextFactory,
    IAgentConnectionManager connections,
    ICommandWorkSignal commandSignal,
    IClock clock,
    IOptions<LanManagement.Server.Options.ServerOptions> options,
    IDashboardEventBus dashboardEvents,
    ILogger<CommandDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchOnceAsync(stoppingToken);
                await commandSignal.WaitAsync(TimeSpan.FromSeconds(options.Value.DispatchIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Command dispatch loop failed.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    public async Task DispatchOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await (from command in db.CommandJobs
                                join device in db.Devices on command.DeviceId equals device.DeviceId
                                where device.ConnectionState == DeviceConnectionState.Online &&
                                      (command.Status == CommandStatus.Pending || command.Status == CommandStatus.Sent) &&
                                      (command.NextAttemptAt == null || command.NextAttemptAt <= now)
                                orderby command.NextAttemptAt, command.CreatedAt
                                select command.CommandId)
            .Take(100).ToListAsync(cancellationToken);

        foreach (var commandId in candidates)
        {
            await AttemptDeliveryAsync(commandId, cancellationToken);
        }
    }

    private async Task AttemptDeliveryAsync(Guid commandId, CancellationToken cancellationToken)
    {
        // Check the live authenticated session before changing durable status. Offline jobs stay PENDING;
        // no timer repeatedly "sends" to a machine that is not connected.
        string? deviceId;
        await using (var lookup = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            deviceId = await lookup.CommandJobs.AsNoTracking().Where(x => x.CommandId == commandId).Select(x => x.DeviceId).SingleOrDefaultAsync(cancellationToken);
        }
        if (string.IsNullOrEmpty(deviceId) || !connections.TryGet(deviceId, out var connection) || connection is null)
        {
            return;
        }

        var delivery = await PrepareDeliveryAsync(commandId, cancellationToken);
        if (delivery is null)
        {
            return;
        }

        try
        {
            await connection.SendAsync(new OutboundCommandMessage
            {
                SentAt = delivery.SentAt,
                CommandId = delivery.CommandId,
                DeviceId = delivery.DeviceId,
                CommandType = delivery.CommandType,
                Payload = ProtocolJson.ToElement(delivery.PayloadJson),
                CreatedAt = delivery.CreatedAt,
                Attempt = delivery.AttemptCount
            }, cancellationToken);
            await dashboardEvents.PublishAsync(new DashboardEvent("command-sent", delivery.SentAt, delivery.DeviceId, delivery.OperationId), cancellationToken);
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException)
        {
            logger.LogInformation(exception, "Command {CommandId} could not be sent to {DeviceId}; it will remain durable for retry.", delivery.CommandId, delivery.DeviceId);
            await ReturnFailedSendToPendingAsync(delivery, exception.Message, cancellationToken);
        }
    }

    private async Task<PreparedDelivery?> PrepareDeliveryAsync(Guid commandId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var command = await db.CommandJobs.SingleOrDefaultAsync(x => x.CommandId == commandId, cancellationToken);
        if (command is null || command.Status is not (CommandStatus.Pending or CommandStatus.Sent))
        {
            return null;
        }

        var deviceOnline = await db.Devices.AsNoTracking().AnyAsync(x => x.DeviceId == command.DeviceId && x.ConnectionState == DeviceConnectionState.Online, cancellationToken);
        if (!deviceOnline)
        {
            return null;
        }

        var now = clock.UtcNow;
        if (command.AttemptCount >= options.Value.MaximumDeliveryAttempts)
        {
            command.Status = CommandStatus.Timeout;
            command.CompletedAt = now;
            command.Error = "No COMMAND_RECEIVED acknowledgement arrived before the maximum delivery attempt count.";
            await db.SaveChangesAsync(cancellationToken);
            await dashboardEvents.PublishAsync(new DashboardEvent("command-timeout", now, command.DeviceId, command.OperationId), cancellationToken);
            return null;
        }

        command.Status = CommandStatus.Sent;
        command.AttemptCount++;
        command.LastAttemptAt = now;
        command.SentAt ??= now;
        command.NextAttemptAt = now.AddSeconds(BackoffSeconds(command.AttemptCount));
        command.LastDeliveryError = null;
        await db.SaveChangesAsync(cancellationToken);
        return new PreparedDelivery(command.CommandId, command.OperationId, command.DeviceId, command.CommandType,
            command.PayloadJson, command.CreatedAt, command.AttemptCount, now);
    }

    private async Task ReturnFailedSendToPendingAsync(PreparedDelivery delivery, string error, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var command = await db.CommandJobs.SingleOrDefaultAsync(x => x.CommandId == delivery.CommandId, cancellationToken);
        if (command is null || command.Status != CommandStatus.Sent || command.AttemptCount != delivery.AttemptCount)
        {
            return;
        }

        command.Status = CommandStatus.Pending;
        command.NextAttemptAt = clock.UtcNow.AddSeconds(BackoffSeconds(command.AttemptCount));
        command.LastDeliveryError = error[..Math.Min(error.Length, 2048)];
        await db.SaveChangesAsync(cancellationToken);
    }

    private int BackoffSeconds(int attempt)
    {
        var exponent = Math.Min(Math.Max(attempt - 1, 0), 10);
        var seconds = (long)options.Value.InitialRetrySeconds * (1L << exponent);
        return (int)Math.Min(seconds, options.Value.MaximumRetrySeconds);
    }

    private sealed record PreparedDelivery(
        Guid CommandId,
        Guid OperationId,
        string DeviceId,
        string CommandType,
        string PayloadJson,
        DateTimeOffset CreatedAt,
        int AttemptCount,
        DateTimeOffset SentAt);
}

public sealed class CommandTimeoutMonitorService(
    ICommandService commands,
    ILogger<CommandTimeoutMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var timedOut = await commands.MarkExpiredExecutionsAsync(stoppingToken);
                if (timedOut > 0)
                {
                    logger.LogWarning("Marked {Count} command(s) TIMEOUT while awaiting final agent results.", timedOut);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Command execution timeout sweep failed.");
            }
        }
    }
}
