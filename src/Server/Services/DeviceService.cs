using System.Text.Json;
using LanManagement.Server.Data;
using LanManagement.Server.Domain;
using LanManagement.Server.Protocol;
using Microsoft.EntityFrameworkCore;

namespace LanManagement.Server.Services;

public interface IDeviceService
{
    Task<DeviceRegistrationResult> RegisterAsync(RegisterDeviceMessage message, string observedIp, CancellationToken cancellationToken, string? enrolledTokenHash = null);
    Task<bool> RecordHeartbeatAsync(HeartbeatMessage message, CancellationToken cancellationToken);
    Task MarkOfflineAsync(string deviceId, string reason, CancellationToken cancellationToken);
    Task<int> MarkExpiredDevicesOfflineAsync(CancellationToken cancellationToken);
    Task<DeviceDetail?> GetDetailAsync(string deviceId, CancellationToken cancellationToken);
    Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken);
}

public sealed class DeviceService(
    IDbContextFactory<ManagementDbContext> contextFactory,
    IPolicyService policyService,
    ICommandService commandService,
    IClock clock,
    IOptions<LanManagement.Server.Options.ServerOptions> serverOptions,
    IDashboardEventBus dashboardEvents,
    ILogger<DeviceService> logger) : IDeviceService
{
    public async Task<DeviceRegistrationResult> RegisterAsync(RegisterDeviceMessage message, string observedIp, CancellationToken cancellationToken, string? enrolledTokenHash = null)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.UtcNow;
        var currentPolicy = await db.Policies.AsNoTracking()
            .Where(x => x.IsCurrent).OrderByDescending(x => x.PolicyVersion).FirstAsync(cancellationToken);
        var desired = DesiredPolicy.Deserialize(currentPolicy.DesiredStateJson);
        var actualStateJson = ToJsonOrNull(message.ActualState);
        var device = await db.Devices.SingleOrDefaultAsync(x => x.DeviceId == message.DeviceId, cancellationToken);
        if (device is null)
        {
            device = new Device
            {
                DeviceId = message.DeviceId,
                // New automatic enrollment binds a digest of the proof token to this record.
                // No plaintext token is ever persisted or returned to an Agent.
                AgentTokenHash = enrolledTokenHash,
                CreatedAt = now
            };
            db.Devices.Add(device);
        }

        device.Hostname = Clean(message.Hostname, 255);
        device.AgentVersion = Clean(message.AgentVersion, 64);
        device.OsVersion = Clean(message.OsVersion, 512);
        device.LocalIp = Clean(message.LocalIp, 64);
        device.ObservedIp = Clean(observedIp, 64);
        device.ConnectionState = DeviceConnectionState.Online;
        device.ConnectedAt = now;
        device.LastHeartbeatAt = now;
        device.LastSeenAt = now;
        device.AppliedPolicyVersion = message.ReportedPolicyVersion;
        device.DesiredPolicyVersion = currentPolicy.PolicyVersion;
        device.ActualStateJson = actualStateJson;
        device.SyncState = message.ReportedPolicyVersion == currentPolicy.PolicyVersion && PolicyEvaluator.Matches(desired, actualStateJson)
            ? DeviceSyncState.Synced
            : DeviceSyncState.OutOfSync;
        device.UpdatedAt = now;

        var version = await db.AgentVersions.FirstOrDefaultAsync(x => x.Version == device.AgentVersion, cancellationToken);
        if (version is null)
        {
            db.AgentVersions.Add(new AgentVersionRecord
            {
                Version = device.AgentVersion,
                FirstSeenAt = now,
                LastSeenAt = now,
                SeenCount = 1
            });
        }
        else
        {
            version.LastSeenAt = now;
            version.SeenCount++;
        }

        var assignment = await db.DevicePolicyAssignments.SingleOrDefaultAsync(
            x => x.DeviceId == device.DeviceId && x.PolicyVersion == currentPolicy.PolicyVersion, cancellationToken);
        if (assignment is null)
        {
            db.DevicePolicyAssignments.Add(new DevicePolicyAssignment
            {
                DeviceId = device.DeviceId,
                PolicyVersion = currentPolicy.PolicyVersion,
                Status = device.SyncState == DeviceSyncState.Synced ? DevicePolicyStatus.Synced : DevicePolicyStatus.Pending,
                AppliedAt = device.SyncState == DeviceSyncState.Synced ? now : null
            });
        }
        else if (device.SyncState == DeviceSyncState.Synced)
        {
            assignment.Status = DevicePolicyStatus.Synced;
            assignment.AppliedAt = now;
            assignment.LastError = null;
        }
        else if (assignment.Status == DevicePolicyStatus.Synced)
        {
            assignment.Status = DevicePolicyStatus.Pending;
            assignment.LastError = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        var pending = await db.CommandJobs.CountAsync(x => x.DeviceId == device.DeviceId &&
            (x.Status == CommandStatus.Pending || x.Status == CommandStatus.Sent), cancellationToken);
        await dashboardEvents.PublishAsync(new DashboardEvent("device-connected", now, device.DeviceId), cancellationToken);

        return new(currentPolicy.PolicyVersion, currentPolicy.DesiredStateJson, pending, device.SyncState != DeviceSyncState.Synced);
    }

    public async Task<bool> RecordHeartbeatAsync(HeartbeatMessage message, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var device = await db.Devices.SingleOrDefaultAsync(x => x.DeviceId == message.DeviceId, cancellationToken);
        if (device is null || device.IsDisabled)
        {
            return false;
        }

        var now = clock.UtcNow;
        var currentPolicy = await db.Policies.AsNoTracking().Where(x => x.IsCurrent)
            .OrderByDescending(x => x.PolicyVersion).FirstAsync(cancellationToken);
        var actualStateJson = message.ActualState.HasValue ? ToJsonOrNull(message.ActualState) : device.ActualStateJson;
        var reportedVersion = message.ReportedPolicyVersion ?? device.AppliedPolicyVersion;
        var desired = DesiredPolicy.Deserialize(currentPolicy.DesiredStateJson);

        device.ConnectionState = DeviceConnectionState.Online;
        device.LastHeartbeatAt = now;
        device.LastSeenAt = now;
        device.UpdatedAt = now;
        device.AgentVersion = string.IsNullOrWhiteSpace(message.AgentVersion) ? device.AgentVersion : Clean(message.AgentVersion, 64);
        device.LocalIp = string.IsNullOrWhiteSpace(message.LocalIp) ? device.LocalIp : Clean(message.LocalIp, 64);
        device.AppliedPolicyVersion = reportedVersion;
        device.DesiredPolicyVersion = currentPolicy.PolicyVersion;
        device.ActualStateJson = actualStateJson;
        device.SyncState = reportedVersion == currentPolicy.PolicyVersion && PolicyEvaluator.Matches(desired, actualStateJson)
            ? DeviceSyncState.Synced
            : DeviceSyncState.OutOfSync;
        var assignment = await db.DevicePolicyAssignments.SingleOrDefaultAsync(
            x => x.DeviceId == device.DeviceId && x.PolicyVersion == currentPolicy.PolicyVersion, cancellationToken);
        if (assignment is null)
        {
            db.DevicePolicyAssignments.Add(new DevicePolicyAssignment
            {
                DeviceId = device.DeviceId,
                PolicyVersion = currentPolicy.PolicyVersion,
                Status = device.SyncState == DeviceSyncState.Synced ? DevicePolicyStatus.Synced : DevicePolicyStatus.Pending,
                AppliedAt = device.SyncState == DeviceSyncState.Synced ? now : null
            });
        }
        else if (device.SyncState == DeviceSyncState.Synced)
        {
            assignment.Status = DevicePolicyStatus.Synced;
            assignment.AppliedAt = now;
            assignment.LastError = null;
        }
        else if (assignment.Status == DevicePolicyStatus.Synced)
        {
            assignment.Status = DevicePolicyStatus.Pending;
        }

        await db.SaveChangesAsync(cancellationToken);
        await dashboardEvents.PublishAsync(new DashboardEvent("heartbeat", now, device.DeviceId), cancellationToken);
        return true;
    }

    public async Task MarkOfflineAsync(string deviceId, string reason, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var device = await db.Devices.SingleOrDefaultAsync(x => x.DeviceId == deviceId, cancellationToken);
        if (device is null || device.ConnectionState == DeviceConnectionState.Offline)
        {
            return;
        }

        var now = clock.UtcNow;
        device.ConnectionState = DeviceConnectionState.Offline;
        device.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await commandService.ReturnUnacknowledgedToPendingAsync(deviceId, reason, cancellationToken);
        await dashboardEvents.PublishAsync(new DashboardEvent("device-offline", now, deviceId), cancellationToken);
        logger.LogInformation("Device {DeviceId} is offline ({Reason}).", deviceId, reason);
    }

    public async Task<int> MarkExpiredDevicesOfflineAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddSeconds(-serverOptions.Value.HeartbeatTimeoutSeconds);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var expired = await db.Devices.Where(x => x.ConnectionState == DeviceConnectionState.Online &&
            (!x.LastHeartbeatAt.HasValue || x.LastHeartbeatAt < cutoff)).Select(x => x.DeviceId).ToListAsync(cancellationToken);
        foreach (var deviceId in expired)
        {
            await MarkOfflineAsync(deviceId, "HEARTBEAT_TIMEOUT", cancellationToken);
        }
        return expired.Count;
    }

    public async Task<DeviceDetail?> GetDetailAsync(string deviceId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.DeviceId == deviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        var activeStatuses = new[] { CommandStatus.Pending, CommandStatus.Sent, CommandStatus.Received, CommandStatus.Running };
        var pending = await db.CommandJobs.CountAsync(x => x.DeviceId == deviceId && activeStatuses.Contains(x.Status), cancellationToken);
        var applicationRows = await db.DeviceApplications.AsNoTracking().Where(x => x.DeviceId == deviceId)
            .OrderBy(x => x.DisplayName).ToListAsync(cancellationToken);
        var recentRows = await db.CommandJobs.AsNoTracking().Where(x => x.DeviceId == deviceId)
            .OrderByDescending(x => x.CreatedAt).Take(50).ToListAsync(cancellationToken);
        var operationIds = recentRows.Select(x => x.OperationId.ToString()).ToArray();
        var commandIds = recentRows.Select(x => x.CommandId.ToString()).ToArray();
        var auditRows = await db.AuditLogs.AsNoTracking().Where(x =>
                (x.EntityType == "Device" && x.EntityId == deviceId) ||
                (x.EntityType == "CommandOperation" && x.EntityId != null && operationIds.Contains(x.EntityId)) ||
                (x.EntityType == "CommandJob" && x.EntityId != null && commandIds.Contains(x.EntityId)))
            .OrderByDescending(x => x.OccurredAt).Take(30).ToListAsync(cancellationToken);
        var hostNames = new Dictionary<string, string> { [deviceId] = device.Hostname };

        return new DeviceDetail(device.DeviceId, device.Hostname, device.LocalIp, device.ObservedIp,
            device.ConnectionState.ToString().ToUpperInvariant(), device.LastHeartbeatAt, device.LastSeenAt,
            device.AgentVersion, device.OsVersion, device.AppliedPolicyVersion, device.DesiredPolicyVersion,
            device.SyncState.ToDisplayValue(), device.ActualStateJson, pending,
            device.LastCommandType, device.LastCommandResult,
            applicationRows.Select(x => new DeviceApplicationView(x.PackageId, x.DisplayName, x.InstalledVersion, x.IsInstalled, x.ReportedAt)).ToList(),
            recentRows.Select(x => ToView(x, hostNames)).ToList(),
            auditRows.Select(x => new DeviceLogView(x.OccurredAt, x.Actor, x.Action, x.Succeeded, x.DetailsJson)).ToList());
    }

    public async Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.UtcNow;
        var cutoff = now.AddSeconds(-serverOptions.Value.HeartbeatTimeoutSeconds);
        var devices = await db.Devices.AsNoTracking().OrderBy(x => x.Hostname).ThenBy(x => x.DeviceId).ToListAsync(cancellationToken);
        var unfinishedStatuses = new[] { CommandStatus.Pending, CommandStatus.Sent, CommandStatus.Received, CommandStatus.Running };
        var pendingCounts = await db.CommandJobs.AsNoTracking().Where(x => unfinishedStatuses.Contains(x.Status))
            .GroupBy(x => x.DeviceId).Select(x => new { DeviceId = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.DeviceId, x => x.Count, cancellationToken);
        var deviceRows = devices.Select(device =>
        {
            var isOnline = device.ConnectionState == DeviceConnectionState.Online && device.LastHeartbeatAt >= cutoff;
            var actual = GetDnsStatus(device.ActualStateJson);
            return new DashboardDevice(device.DeviceId, device.Hostname, isOnline,
                isOnline ? "ONLINE" : "OFFLINE", device.LastHeartbeatAt, device.LastSeenAt,
                device.LocalIp, device.AgentVersion, device.AppliedPolicyVersion, device.DesiredPolicyVersion,
                device.SyncState.ToDisplayValue(), pendingCounts.GetValueOrDefault(device.DeviceId),
                device.LastCommandType, device.LastCommandResult, actual);
        }).ToList();
        var currentPolicy = await db.Policies.AsNoTracking().Where(x => x.IsCurrent).OrderByDescending(x => x.PolicyVersion).FirstAsync(cancellationToken);

        return new DashboardSnapshot(deviceRows.Count, deviceRows.Count(x => x.IsOnline), deviceRows.Count(x => !x.IsOnline),
            deviceRows.Count(x => x.SyncState == "SYNCING"), deviceRows.Count(x => x.SyncState == "FAILED"),
            currentPolicy.PolicyVersion, deviceRows);
    }

    private static CommandJobView ToView(CommandJob job, IReadOnlyDictionary<string, string> hostnames) => new(
        job.CommandId, job.DeviceId, hostnames.GetValueOrDefault(job.DeviceId, job.DeviceId), job.Status.ToProtocolValue(), job.AttemptCount,
        job.CreatedAt, job.LastAttemptAt, job.ReceivedAt, job.StartedAt, job.CompletedAt, job.ExitCode, job.Output, job.Error);

    private static string Clean(string? value, int max) => (value ?? string.Empty).Trim()[..Math.Min((value ?? string.Empty).Trim().Length, max)];

    private static string? ToJsonOrNull(JsonElement? value)
    {
        if (!value.HasValue || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        return value.Value.GetRawText();
    }

    private static string GetDnsStatus(string? actualStateJson)
    {
        if (string.IsNullOrWhiteSpace(actualStateJson))
        {
            return "UNKNOWN";
        }

        try
        {
            using var document = JsonDocument.Parse(actualStateJson);
            if (document.RootElement.TryGetProperty("dns", out var dns) && dns.ValueKind == JsonValueKind.Object &&
                dns.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
            {
                var value = status.GetString()?.ToUpperInvariant();
                return value is "CORRECT" or "INCORRECT" ? value : "UNKNOWN";
            }
        }
        catch (JsonException)
        {
            // Unknown is safer than trusting malformed agent state.
        }
        return "UNKNOWN";
    }
}
