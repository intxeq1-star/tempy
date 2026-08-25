using LanManagement.Server.Data;
using LanManagement.Server.Domain;
using LanManagement.Server.Protocol;
using LanManagement.Server.Transport;
using Microsoft.EntityFrameworkCore;

namespace LanManagement.Server.Services;

/// <summary>Delivers durable per-device policy assignments when and only when an authenticated session is online.</summary>
public sealed class SyncDispatcherService(
    IDbContextFactory<ManagementDbContext> contextFactory,
    IAgentConnectionManager connections,
    ISyncWorkSignal syncSignal,
    IClock clock,
    IDashboardEventBus dashboardEvents,
    IOptions<LanManagement.Server.Options.ServerOptions> options,
    ILogger<SyncDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchOnceAsync(stoppingToken);
                await syncSignal.WaitAsync(TimeSpan.FromSeconds(options.Value.SyncDispatchIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Policy synchronization dispatch failed.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    public async Task DispatchOnceAsync(CancellationToken cancellationToken)
    {
        var retryBefore = clock.UtcNow.AddSeconds(-options.Value.InitialRetrySeconds);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await (from assignment in db.DevicePolicyAssignments
                                join device in db.Devices on assignment.DeviceId equals device.DeviceId
                                join policy in db.Policies on assignment.PolicyVersion equals policy.PolicyVersion
                                where policy.IsCurrent && device.ConnectionState == DeviceConnectionState.Online &&
                                      (assignment.Status == DevicePolicyStatus.Pending ||
                                       ((assignment.Status == DevicePolicyStatus.Syncing || assignment.Status == DevicePolicyStatus.Failed) &&
                                        (!assignment.LastRequestedAt.HasValue || assignment.LastRequestedAt < retryBefore)))
                                orderby assignment.LastRequestedAt
                                select new { assignment.DeviceId, assignment.PolicyVersion, policy.DesiredStateJson })
            .Take(100).ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            if (!connections.TryGet(candidate.DeviceId, out var connection) || connection is null)
            {
                continue;
            }
            await DispatchDeviceAsync(candidate.DeviceId, candidate.PolicyVersion, candidate.DesiredStateJson, connection, cancellationToken);
        }
    }

    private async Task DispatchDeviceAsync(string deviceId, int policyVersion, string desiredPolicyJson, IAgentConnection connection, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var assignment = await db.DevicePolicyAssignments.SingleOrDefaultAsync(x => x.DeviceId == deviceId && x.PolicyVersion == policyVersion, cancellationToken);
            var device = await db.Devices.SingleOrDefaultAsync(x => x.DeviceId == deviceId, cancellationToken);
            if (assignment is null || device is null || device.ConnectionState != DeviceConnectionState.Online)
            {
                return;
            }

            assignment.Status = DevicePolicyStatus.Syncing;
            assignment.LastRequestedAt = now;
            assignment.AttemptCount++;
            device.SyncState = DeviceSyncState.Syncing;
            device.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        var request = new ServerSyncRequestMessage
        {
            SentAt = now,
            DeviceId = deviceId,
            PolicyVersion = policyVersion,
            DesiredPolicy = ProtocolJson.ToElement(desiredPolicyJson),
            Reason = "RECONCILIATION"
        };

        try
        {
            await connection.SendAsync(request, cancellationToken);
            await dashboardEvents.PublishAsync(new DashboardEvent("sync-requested", now, deviceId), cancellationToken);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or IOException)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            await MarkDeliveryFailureAsync(deviceId, policyVersion, exception.Message, cancellationToken);
        }
    }

    private async Task MarkDeliveryFailureAsync(string deviceId, int policyVersion, string error, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var assignment = await db.DevicePolicyAssignments.SingleOrDefaultAsync(x => x.DeviceId == deviceId && x.PolicyVersion == policyVersion, cancellationToken);
        var device = await db.Devices.SingleOrDefaultAsync(x => x.DeviceId == deviceId, cancellationToken);
        if (assignment is not null)
        {
            assignment.Status = DevicePolicyStatus.Pending;
            assignment.LastError = error[..Math.Min(error.Length, 2048)];
        }
        if (device is not null && device.SyncState == DeviceSyncState.Syncing)
        {
            device.SyncState = DeviceSyncState.OutOfSync;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
