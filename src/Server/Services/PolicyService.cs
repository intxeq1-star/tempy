using System.Net;
using LanManagement.Server.Data;
using LanManagement.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace LanManagement.Server.Services;

public interface IPolicyService
{
    Task<Policy> GetCurrentAsync(CancellationToken cancellationToken);
    Task<Policy> UpdateAsync(DesiredPolicy desiredPolicy, string actor, string? remoteIp, string reason, CancellationToken cancellationToken);
    Task<Policy> SyncAllAsync(string actor, string? remoteIp, CancellationToken cancellationToken);
    Task QueueDeviceAsync(string deviceId, string reason, CancellationToken cancellationToken);
    Task HandleResultAsync(string deviceId, int policyVersion, string status, string actualStateJson, string? error, DateTimeOffset completedAt, CancellationToken cancellationToken);
}

public sealed class PolicyService(
    IDbContextFactory<ManagementDbContext> contextFactory,
    IClock clock,
    IDashboardEventBus dashboardEvents,
    ISyncWorkSignal syncSignal) : IPolicyService
{
    // This Server process is the single policy authority. Serialize revision creation so two rapid
    // administrator clicks cannot allocate the same monotonic PolicyVersion.
    private readonly SemaphoreSlim _policyMutation = new(1, 1);

    public async Task<Policy> GetCurrentAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Policies.AsNoTracking().Where(x => x.IsCurrent)
            .OrderByDescending(x => x.PolicyVersion).FirstAsync(cancellationToken);
    }

    public async Task<Policy> UpdateAsync(DesiredPolicy desiredPolicy, string actor, string? remoteIp, string reason, CancellationToken cancellationToken)
    {
        Validate(desiredPolicy);
        await _policyMutation.WaitAsync(cancellationToken);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var now = clock.UtcNow;
            var current = await db.Policies.Where(x => x.IsCurrent).OrderByDescending(x => x.PolicyVersion).FirstAsync(cancellationToken);
            current.IsCurrent = false;
            var highest = await db.Policies.Select(x => x.PolicyVersion).DefaultIfEmpty(0).MaxAsync(cancellationToken);
            var next = new Policy
            {
                PolicyVersion = highest + 1,
                IsCurrent = true,
                DesiredStateJson = DesiredPolicy.Serialize(desiredPolicy),
                CreatedBy = actor,
                CreatedAt = now,
                ChangeReason = string.IsNullOrWhiteSpace(reason) ? "Policy changed" : reason[..Math.Min(reason.Length, 256)]
            };
            db.Policies.Add(next);
            await AssignAllDevicesAsync(db, next.PolicyVersion, now, cancellationToken);
            db.AuditLogs.Add(AuditLogFactory.Create(now, actor, "POLICY_UPDATED", "Policy", next.PolicyVersion.ToString(), new
            {
                policy_version = next.PolicyVersion,
                desired_policy = desiredPolicy,
                reason = next.ChangeReason
            }, remoteIp));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            syncSignal.Pulse();
            await dashboardEvents.PublishAsync(new DashboardEvent("policy-updated", now), cancellationToken);
            return next;
        }
        finally
        {
            _policyMutation.Release();
        }
    }

    public async Task<Policy> SyncAllAsync(string actor, string? remoteIp, CancellationToken cancellationToken)
    {
        // A sync-all creates a new policy revision even when values did not change. This gives all
        // 89 device assignments a durable, auditable desired version rather than relying on a socket broadcast.
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var current = await db.Policies.AsNoTracking().Where(x => x.IsCurrent).OrderByDescending(x => x.PolicyVersion).FirstAsync(cancellationToken);
        var desired = DesiredPolicy.Deserialize(current.DesiredStateJson);
        return await UpdateAsync(desired, actor, remoteIp, "Administrator requested SYNC ALL", cancellationToken);
    }

    public async Task QueueDeviceAsync(string deviceId, string reason, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var policy = await db.Policies.AsNoTracking().Where(x => x.IsCurrent).OrderByDescending(x => x.PolicyVersion).FirstAsync(cancellationToken);
        var device = await db.Devices.SingleOrDefaultAsync(x => x.DeviceId == deviceId, cancellationToken);
        if (device is null)
        {
            return;
        }

        device.DesiredPolicyVersion = policy.PolicyVersion;
        if (device.SyncState != DeviceSyncState.Synced || device.AppliedPolicyVersion != policy.PolicyVersion)
        {
            device.SyncState = DeviceSyncState.OutOfSync;
        }

        var assignment = await db.DevicePolicyAssignments.SingleOrDefaultAsync(
            x => x.DeviceId == deviceId && x.PolicyVersion == policy.PolicyVersion, cancellationToken);
        if (assignment is null)
        {
            db.DevicePolicyAssignments.Add(new DevicePolicyAssignment
            {
                DeviceId = deviceId,
                PolicyVersion = policy.PolicyVersion,
                Status = DevicePolicyStatus.Pending,
                LastError = reason
            });
        }
        else if (assignment.Status == DevicePolicyStatus.Synced && device.SyncState != DeviceSyncState.Synced)
        {
            assignment.Status = DevicePolicyStatus.Pending;
            assignment.LastError = reason;
        }
        else if (assignment.Status == DevicePolicyStatus.Failed)
        {
            // A fresh agent request is an explicit reconciliation opportunity. Do not reset a
            // currently SYNCING assignment on every 15-second heartbeat.
            assignment.Status = DevicePolicyStatus.Pending;
            assignment.LastError = reason;
        }
        await db.SaveChangesAsync(cancellationToken);
        syncSignal.Pulse();
    }

    public async Task HandleResultAsync(string deviceId, int policyVersion, string status, string actualStateJson, string? error, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var device = await db.Devices.SingleOrDefaultAsync(x => x.DeviceId == deviceId, cancellationToken);
        if (device is null)
        {
            return;
        }

        var policy = await db.Policies.AsNoTracking().SingleOrDefaultAsync(x => x.PolicyVersion == policyVersion, cancellationToken);
        if (policy is null)
        {
            return;
        }

        var now = clock.UtcNow;
        var assignment = await db.DevicePolicyAssignments.SingleOrDefaultAsync(x => x.DeviceId == deviceId && x.PolicyVersion == policyVersion, cancellationToken);
        if (assignment is null)
        {
            assignment = new DevicePolicyAssignment { DeviceId = deviceId, PolicyVersion = policyVersion };
            db.DevicePolicyAssignments.Add(assignment);
        }

        var matches = string.Equals(status, "SUCCESS", StringComparison.Ordinal) && PolicyEvaluator.Matches(DesiredPolicy.Deserialize(policy.DesiredStateJson), actualStateJson);
        assignment.Status = matches ? DevicePolicyStatus.Synced : DevicePolicyStatus.Failed;
        assignment.AppliedAt = matches ? completedAt : null;
        assignment.LastError = matches ? null : Truncate(error ?? "Agent reported policy state that does not match desired policy.", 2048);
        device.ActualStateJson = actualStateJson;
        device.AppliedPolicyVersion = policyVersion;
        device.DesiredPolicyVersion = (await db.Policies.AsNoTracking().Where(x => x.IsCurrent).OrderByDescending(x => x.PolicyVersion).Select(x => x.PolicyVersion).FirstAsync(cancellationToken));
        device.SyncState = policyVersion == device.DesiredPolicyVersion && matches ? DeviceSyncState.Synced : DeviceSyncState.Failed;
        device.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await dashboardEvents.PublishAsync(new DashboardEvent("sync-result", now, deviceId), cancellationToken);
    }

    private static async Task AssignAllDevicesAsync(ManagementDbContext db, int policyVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var devices = await db.Devices.ToListAsync(cancellationToken);
        foreach (var device in devices)
        {
            device.DesiredPolicyVersion = policyVersion;
            device.SyncState = DeviceSyncState.OutOfSync;
            device.UpdatedAt = now;
            db.DevicePolicyAssignments.Add(new DevicePolicyAssignment
            {
                DeviceId = device.DeviceId,
                PolicyVersion = policyVersion,
                Status = DevicePolicyStatus.Pending
            });
        }
    }

    private static void Validate(DesiredPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(policy.Dns.Server) || !IPAddress.TryParse(policy.Dns.Server, out _))
        {
            throw new ArgumentException("DNS server must be a valid IP address.");
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
