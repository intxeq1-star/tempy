namespace OMClientAgent.Core.Models;

public sealed class AgentConfiguration
{
    public string ServerUrl { get; set; } = "https://om-server.local:8443";
    public List<string> FallbackServerUrls { get; set; } = new();
    public string SignalRHubPath { get; set; } = "/omHub";
    public string AuthKey { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public int HeartbeatIntervalSeconds { get; set; } = OmProtocol.DefaultHeartbeatSeconds;
    public int OfflineGraceSeconds { get; set; } = OmProtocol.DefaultOfflineGraceSeconds;
    public int JobPollIntervalSeconds { get; set; } = 10;
    public int MaxBackoffSeconds { get; set; } = 120;
    public int DefaultJobTimeoutSeconds { get; set; } = 600;
    public int SyncIntervalSeconds { get; set; } = 60;
    public bool VerifyServerCertificate { get; set; } = true;
    public string DataDirectory { get; set; } = @"C:\ProgramData\OM\Client";
    public string ServiceName { get; set; } = "OMClientAgent";
    public int StartupDelayMilliseconds { get; set; } = 3000;
    public string CaCertificateFilePath { get; set; } = string.Empty;
    public string? DesiredDns { get; set; }
    public int DesiredDnsPort { get; set; } = 53;

    public AgentConfiguration Clone() => (AgentConfiguration)MemberwiseClone();
}

public sealed class MachineIdentity
{
    public string MachineId { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public string? OsVersion { get; set; }
    public string? Architecture { get; set; }
    public string AgentVersion { get; set; } = OmProtocol.Version;
    public string? AdvertisedIp { get; set; }
    public DateTime EnrolledAtUtc { get; set; }
    public string? DomainOrWorkgroup { get; set; }
    public string? DeviceDescription { get; set; }
}

public sealed class Job
{
    public string JobId { get; set; } = string.Empty;
    public string TargetMachineId { get; set; } = string.Empty;
    public JobType Type { get; set; }
    public JobExecutionMode ExecutionMode { get; set; } = JobExecutionMode.Admin;
    public JobPriority Priority { get; set; } = JobPriority.Normal;
    public string? Command { get; set; }
    public string? Action { get; set; }
    public string? Arguments { get; set; }
    public string? PackageId { get; set; }
    public string? PolicyId { get; set; }
    public string? Sha256 { get; set; }
    public string? DownloadUrl { get; set; }
    public int TimeoutSeconds { get; set; } = 600;
    public DateTime CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public string ProtocolVersion { get; set; } = OmProtocol.Version;

    public JobStatus Status { get; set; } = JobStatus.Pending;
    public int ExitCode { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? StandardOutput { get; set; }
    public string? StandardError { get; set; }

    public Job Clone() => (Job)MemberwiseClone();
}

public sealed class AppPolicy
{
    public string PolicyId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Application { get; set; } = string.Empty;
    public ApplicationAction Action { get; set; }
    public string Target { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public sealed class SoftwarePackage
{
    public string PackageId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Architecture { get; set; } = "x64";
    public string DownloadUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string InstallerArguments { get; set; } = string.Empty;
    public string UninstallArguments { get; set; } = string.Empty;
    public string DetectionMethod { get; set; } = string.Empty;
}

public sealed class InstalledSoftware
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? Provider { get; set; }
}

public sealed class HealthReport
{
    public string MachineId { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = OmProtocol.Version;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string ConnectionState { get; set; } = Models.ConnectionState.Unknown.ToString();
    public int PendingJobs { get; set; }
    public long CpuPercent { get; set; }
    public long MemoryMb { get; set; }
    public long DiskFreeMb { get; set; }
    public string? LastError { get; set; }
}

public sealed class SyncCursor
{
    public long LastServerRevision { get; set; }
    public long LastUploadedResultRevision { get; set; }
    public string? LastSyncAtUtc { get; set; }
}
