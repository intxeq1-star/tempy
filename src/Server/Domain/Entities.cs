using System.ComponentModel.DataAnnotations;

namespace LanManagement.Server.Domain;

public sealed class Device
{
    [Key]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [MaxLength(255)]
    public string Hostname { get; set; } = string.Empty;

    [MaxLength(64)]
    public string AgentVersion { get; set; } = string.Empty;

    [MaxLength(512)]
    public string OsVersion { get; set; } = string.Empty;

    [MaxLength(64)]
    public string LocalIp { get; set; } = string.Empty;

    [MaxLength(64)]
    public string ObservedIp { get; set; } = string.Empty;

    public DeviceConnectionState ConnectionState { get; set; } = DeviceConnectionState.Offline;
    public DateTimeOffset? ConnectedAt { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public int AppliedPolicyVersion { get; set; }
    public int DesiredPolicyVersion { get; set; }
    public DeviceSyncState SyncState { get; set; } = DeviceSyncState.Unknown;
    public string? ActualStateJson { get; set; }

    public Guid? LastCommandId { get; set; }
    public string? LastCommandType { get; set; }
    public string? LastCommandResult { get; set; }

    // A SHA-256 digest of a pre-provisioned per-device token; never a plaintext token.
    [MaxLength(128)]
    public string? AgentTokenHash { get; set; }

    public bool IsDisabled { get; set; }

    public ICollection<DeviceGroupMembership> GroupMemberships { get; set; } = new List<DeviceGroupMembership>();
    public ICollection<DevicePolicyAssignment> PolicyAssignments { get; set; } = new List<DevicePolicyAssignment>();
}

public sealed class DeviceGroup
{
    public Guid DeviceGroupId { get; set; } = Guid.NewGuid();

    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<DeviceGroupMembership> Members { get; set; } = new List<DeviceGroupMembership>();
}

public sealed class DeviceGroupMembership
{
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;
    public Guid DeviceGroupId { get; set; }
    public DateTimeOffset AddedAt { get; set; }

    public Device Device { get; set; } = null!;
    public DeviceGroup DeviceGroup { get; set; } = null!;
}

public sealed class Policy
{
    public int PolicyVersion { get; set; }
    public bool IsCurrent { get; set; }
    public string DesiredStateJson { get; set; } = "{}";
    [MaxLength(128)]
    public string CreatedBy { get; set; } = "system";
    public DateTimeOffset CreatedAt { get; set; }
    [MaxLength(256)]
    public string? ChangeReason { get; set; }
    public ICollection<DevicePolicyAssignment> DeviceAssignments { get; set; } = new List<DevicePolicyAssignment>();
}

public sealed class DevicePolicyAssignment
{
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;
    public int PolicyVersion { get; set; }
    public DevicePolicyStatus Status { get; set; } = DevicePolicyStatus.Pending;
    public DateTimeOffset? LastRequestedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }

    public Device Device { get; set; } = null!;
    public Policy Policy { get; set; } = null!;
}

public sealed class CommandOperation
{
    public Guid OperationId { get; set; } = Guid.NewGuid();
    [MaxLength(64)]
    public string CommandType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public TargetKind TargetKind { get; set; }
    [MaxLength(512)]
    public string TargetDescription { get; set; } = string.Empty;
    [MaxLength(128)]
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    [MaxLength(256)]
    public string? DisplayName { get; set; }
    public ICollection<CommandJob> Jobs { get; set; } = new List<CommandJob>();
}

public sealed class CommandJob
{
    [Key]
    public Guid CommandId { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;
    [MaxLength(64)]
    public string CommandType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    [MaxLength(128)]
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public CommandStatus Status { get; set; } = CommandStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public string? ResultDataJson { get; set; }
    public string? LastDeliveryError { get; set; }

    public CommandOperation Operation { get; set; } = null!;
    public ICollection<CommandResult> Results { get; set; } = new List<CommandResult>();
}

public sealed class CommandResult
{
    public Guid CommandResultId { get; set; } = Guid.NewGuid();
    public Guid CommandId { get; set; }
    public Guid MessageId { get; set; }
    public CommandStatus Status { get; set; }
    public DateTimeOffset ReceivedByServerAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public string? ResultDataJson { get; set; }

    public CommandJob Command { get; set; } = null!;
}

public sealed class ApplicationCatalogItem
{
    public Guid ApplicationId { get; set; } = Guid.NewGuid();
    [MaxLength(256)]
    public string PackageId { get; set; } = string.Empty;
    [MaxLength(256)]
    public string DisplayName { get; set; } = string.Empty;
    [MaxLength(64)]
    public string Source { get; set; } = "winget";
    [MaxLength(128)]
    public string? DesiredVersion { get; set; }
    public string? InstallArgumentsJson { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DeviceApplication
{
    public Guid DeviceApplicationId { get; set; } = Guid.NewGuid();
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;
    [MaxLength(256)]
    public string PackageId { get; set; } = string.Empty;
    [MaxLength(256)]
    public string DisplayName { get; set; } = string.Empty;
    [MaxLength(128)]
    public string? InstalledVersion { get; set; }
    public bool IsInstalled { get; set; }
    public DateTimeOffset ReportedAt { get; set; }
}

public sealed class AuditLog
{
    public Guid AuditLogId { get; set; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; set; }
    [MaxLength(128)]
    public string Actor { get; set; } = "system";
    [MaxLength(128)]
    public string Action { get; set; } = string.Empty;
    [MaxLength(64)]
    public string EntityType { get; set; } = string.Empty;
    [MaxLength(128)]
    public string? EntityId { get; set; }
    public string DetailsJson { get; set; } = "{}";
    [MaxLength(64)]
    public string? RemoteIp { get; set; }
    public bool Succeeded { get; set; }
}

public sealed class AgentVersionRecord
{
    public Guid AgentVersionRecordId { get; set; } = Guid.NewGuid();
    [MaxLength(64)]
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public int SeenCount { get; set; }
}

public sealed class ServerSetting
{
    [Key]
    [MaxLength(128)]
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AdminUser
{
    public Guid AdminUserId { get; set; } = Guid.NewGuid();
    [MaxLength(128)]
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public AdminRole Role { get; set; } = AdminRole.Administrator;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}

/// <summary>Persists agent message IDs so retransmitted results and receipts are idempotent.</summary>
public sealed class ProcessedAgentMessage
{
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;
    public Guid MessageId { get; set; }
    [MaxLength(64)]
    public string MessageType { get; set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; set; }
}
