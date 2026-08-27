using System.Text.Json;
using System.Text.Json.Serialization;

namespace OMClientAgent.Core.Models;

public static class OmJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter<JobStatus>(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false),
            new JsonStringEnumConverter<JobExecutionMode>(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false),
            new JsonStringEnumConverter<JobType>(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false),
            new JsonStringEnumConverter<ApplicationAction>(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false)
        }
    };
}

public sealed class RegisterRequest
{
    public string MachineId { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = OmProtocol.Version;
    public string? IpAddress { get; set; }
}

public sealed class RegisterResponse
{
    public string? MachineId { get; set; }
    public string? AuthKey { get; set; }
    public long ServerTime { get; set; }
}

public sealed class AgentHeartbeat
{
    public string MachineId { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = OmProtocol.Version;
    public string? IpAddress { get; set; }
    public string Mode { get; set; } = "https";
    public long Timestamp { get; set; }
}

public sealed class ServerConfig
{
    public string? ServerAddress { get; set; }
    public int Port { get; set; } = 8443;
    public int HeartbeatSeconds { get; set; } = OmProtocol.DefaultHeartbeatSeconds;
    public string? DesiredDns { get; set; }
    public int DesiredDnsPort { get; set; } = 53;
}

public sealed class PackageMetadata
{
    public string? PackageId { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Architecture { get; set; }
    public string? DownloadUrl { get; set; }
    public string? Sha256 { get; set; }
    public string? InstallArgs { get; set; }
    public string? UninstallArgs { get; set; }
    public string? DetectionMethodJson { get; set; }
    public string? Source { get; set; }
    public string? AddedAtUtc { get; set; }
    public string? AddedBy { get; set; }
}

public sealed class JobMessage
{
    public string JobId { get; set; } = string.Empty;
    public string Type { get; set; } = "command";
    public string ExecutionMode { get; set; } = "ADMIN";
    public string? Command { get; set; }
    public int TimeoutSeconds { get; set; } = 180;
    public int Priority { get; set; }
    public int ProtocolVersion { get; set; } = OmProtocol.WireVersion;

    public Job ToJob(string thisMachineId)
    {
        return new Job
        {
            JobId = JobId,
            TargetMachineId = thisMachineId,
            Type = string.Equals(Type, "software", StringComparison.OrdinalIgnoreCase) ? JobType.Software : JobType.Command,
            ExecutionMode = string.Equals(ExecutionMode, "USER", StringComparison.OrdinalIgnoreCase) ? JobExecutionMode.User : JobExecutionMode.Admin,
            Command = Command,
            TimeoutSeconds = TimeoutSeconds > 0 ? TimeoutSeconds : 180,
            Priority = (JobPriority)Math.Clamp(Priority, 0, 3),
            ProtocolVersion = OmProtocol.Version,
            CreatedAtUtc = DateTime.UtcNow,
            Status = JobStatus.Pending
        };
    }
}

public sealed class PolicyMessage
{
    public string PolicyId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Category { get; set; } = "appcontrol";
    public string Action { get; set; } = "BLOCK";
    public string? Application { get; set; }
    public string? ValueJson { get; set; }
}

public sealed class ConfigMessage
{
    public string? ServerAddress { get; set; }
    public int Port { get; set; } = 8443;
    public int HeartbeatSeconds { get; set; } = OmProtocol.DefaultHeartbeatSeconds;
    public string? DesiredDns { get; set; }
    public int DesiredDnsPort { get; set; } = 53;
}

public sealed class AgentUpdateMessage
{
    public string? Version { get; set; }
    public string? DownloadUrl { get; set; }
    public string? Sha256 { get; set; }
    public bool Required { get; set; }
}

public static class WirePolicy
{
    public static AppPolicy From(PolicyMessage m)
    {
        var action = string.Equals(m.Action, "UNBLOCK", StringComparison.OrdinalIgnoreCase) ? ApplicationAction.Unblock : ApplicationAction.Block;
        return new AppPolicy
        {
            PolicyId = m.PolicyId,
            Version = m.Version,
            Application = m.Application ?? string.Empty,
            Action = action,
            Target = m.Category,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}

public sealed class JobAckMessage
{
    public string JobId { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public string Status { get; set; } = "ACKNOWLEDGED";
}

public sealed class JobStartedMessage
{
    public string JobId { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public long Timestamp { get; set; }
}

public sealed class JobResultMessage
{
    public string JobId { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public int ExitCode { get; set; }
    public string? Output { get; set; }
    public long StartedAt { get; set; }
    public long CompletedAt { get; set; }
}

public sealed class MachineActualState
{
    public string MachineId { get; set; } = string.Empty;
    public string PolicyVersionActual { get; set; } = "";
    public string? DnsActual { get; set; }
    public string InstalledSoftwareJson { get; set; } = "[]";
    public string HealthJson { get; set; } = "{}";
}

public sealed class HealthReportMsg
{
    public string MachineId { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = OmProtocol.Version;
    public string HealthJson { get; set; } = "{}";
}

public sealed class SyncRequest
{
    public string MachineId { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = OmProtocol.Version;
    public string? IpAddress { get; set; }
    public long LastRevision { get; set; }
    public List<JobResultMessage> LocalResults { get; set; } = new();
    public MachineActualState ActualState { get; set; } = new();
}

public sealed class SyncResponse
{
    public long ServerTime { get; set; }
    public string? MachineId { get; set; }
    public long Revision { get; set; }
    public string? AuthKey { get; set; }
    public List<JobMessage>? Jobs { get; set; }
    public List<PolicyMessage>? Policies { get; set; }
    public ServerConfig? Config { get; set; }
    public AgentUpdateMessage? AgentUpdate { get; set; }
    public string? Message { get; set; }
}
