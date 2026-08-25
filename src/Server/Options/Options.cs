using System.ComponentModel.DataAnnotations;

namespace LanManagement.Server.Options;

public sealed class ServerOptions
{
    [Required]
    public string ListenAddress { get; set; } = "192.168.1.100";
    [Range(1, 65535)]
    public int Port { get; set; } = 8765;
    [Required]
    public string AdvertisedHost { get; set; } = "192.168.1.100";
    public bool UseHttps { get; set; }
    public string CertificatePath { get; set; } = string.Empty;
    public string CertificatePassword { get; set; } = string.Empty;
    [Range(5, 300)]
    public int HeartbeatIntervalSeconds { get; set; } = 15;
    [Range(15, 1800)]
    public int HeartbeatTimeoutSeconds { get; set; } = 45;
    [Range(1, 60)]
    public int PresenceSweepSeconds { get; set; } = 5;
    [Range(1, 60)]
    public int DispatchIntervalSeconds { get; set; } = 2;
    [Range(1, 60)]
    public int SyncDispatchIntervalSeconds { get; set; } = 3;
    [Range(1024, 1048576)]
    public int MaximumMessageBytes { get; set; } = 262144;
    [Range(1024, 1048576)]
    public int MaximumCommandOutputBytes { get; set; } = 65536;
    [Range(1024, 262144)]
    public int MaximumCommandErrorBytes { get; set; } = 16384;
    [Range(1, 100)]
    public int MaximumDeliveryAttempts { get; set; } = 10;
    [Range(1, 3600)]
    public int InitialRetrySeconds { get; set; } = 5;
    [Range(1, 86400)]
    public int MaximumRetrySeconds { get; set; } = 300;
    [Range(1, 10080)]
    public int CommandExecutionTimeoutMinutes { get; set; } = 120;
    [Required]
    public string DefaultDnsServer { get; set; } = "192.168.1.100";

    public string PublicHttpScheme => UseHttps ? "https" : "http";
    public string AgentWebSocketScheme => UseHttps ? "wss" : "ws";
    public string AgentEndpoint => $"{AgentWebSocketScheme}://{AdvertisedHost}:{Port}/agent/ws";
}

public sealed class SecurityOptions
{
    public bool AllowNewDeviceEnrollment { get; set; }
    public bool AllowEnrollmentTokenForExistingDevices { get; set; }

    /// <summary>Injected by environment, Windows secret store, or protected configuration; never committed.</summary>
    public string AgentEnrollmentToken { get; set; } = string.Empty;
    public bool RequireHttpsForAdmin { get; set; }
    [Range(5, 1440)]
    public int CookieMinutes { get; set; } = 480;
}

public sealed class BootstrapAdminOptions
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
