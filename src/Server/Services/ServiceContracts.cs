using System.Text.Json;
using LanManagement.Server.Domain;

namespace LanManagement.Server.Services;

public sealed record CommandTarget(TargetKind Kind, IReadOnlyCollection<string>? DeviceIds = null, Guid? GroupId = null);

public sealed record CreateCommandRequest(
    string CommandType,
    JsonElement Payload,
    CommandTarget Target,
    string? DisplayName = null);

public sealed record CreateCommandResult(Guid OperationId, IReadOnlyList<Guid> CommandIds, int TargetCount);

public sealed record CommandOperationProgress(
    Guid OperationId,
    string CommandType,
    string TargetDescription,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    IReadOnlyDictionary<string, int> Summary,
    IReadOnlyList<CommandJobView> Jobs);

public sealed record CommandJobView(
    Guid CommandId,
    string DeviceId,
    string Hostname,
    string Status,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int? ExitCode,
    string? Output,
    string? Error);

public sealed record DeviceRegistrationResult(
    int DesiredPolicyVersion,
    string DesiredPolicyJson,
    int PendingCommandCount,
    bool SyncRequired);

public sealed record DeviceDetail(
    string DeviceId,
    string Hostname,
    string LocalIp,
    string ObservedIp,
    string ConnectionState,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset? LastSeenAt,
    string AgentVersion,
    string OsVersion,
    int AppliedPolicyVersion,
    int DesiredPolicyVersion,
    string SyncState,
    string? ActualStateJson,
    int PendingCommandCount,
    string? LastCommandType,
    string? LastCommandResult,
    IReadOnlyList<DeviceApplicationView> Applications,
    IReadOnlyList<CommandJobView> RecentCommands,
    IReadOnlyList<DeviceLogView> Logs);

public sealed record DeviceApplicationView(
    string PackageId,
    string DisplayName,
    string? InstalledVersion,
    bool IsInstalled,
    DateTimeOffset ReportedAt);

public sealed record DeviceLogView(
    DateTimeOffset OccurredAt,
    string Actor,
    string Action,
    bool Succeeded,
    string DetailsJson);

public sealed record DashboardDevice(
    string DeviceId,
    string Hostname,
    bool IsOnline,
    string ConnectionState,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset? LastSeenAt,
    string LocalIp,
    string AgentVersion,
    int AppliedPolicyVersion,
    int DesiredPolicyVersion,
    string SyncState,
    int PendingCommandCount,
    string? LastCommandType,
    string? LastCommandResult,
    string DnsStatus);

public sealed record DashboardSnapshot(
    int Total,
    int Online,
    int Offline,
    int Syncing,
    int Failed,
    int DesiredPolicyVersion,
    IReadOnlyList<DashboardDevice> Devices);
