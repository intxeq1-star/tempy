namespace LanAgent.Core.Configuration;

/// <summary>
/// Agent configuration. All server coordinates and tunables live here (bound from the "Agent"
/// configuration section) — nothing is hard-coded around the codebase.
/// Defaults implement PROTOCOL_CONTRACT §1.
/// </summary>
public sealed class AgentOptions
{
    // ---- Server / transport -------------------------------------------------
    public string ServerIp { get; set; } = "192.168.1.100";
    public int ServerPort { get; set; } = 8765;
    public bool UseTls { get; set; } = false;
    public string WebSocketPath { get; set; } = "/agent/ws";
    public string EnrollmentKey { get; set; } = "CHANGE_ME_AT_INSTALL";

    // ---- Timing --------------------------------------------------------------
    public int HeartbeatIntervalSeconds { get; set; } = 15;
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int AuthTimeoutSeconds { get; set; } = 15;
    public int SyncResponseTimeoutSeconds { get; set; } = 30;
    public int SyncIntervalSeconds { get; set; } = 300;
    public int BackoffInitialSeconds { get; set; } = 2;
    public int BackoffMaxSeconds { get; set; } = 60;
    public double BackoffMultiplier { get; set; } = 2.0;

    // ---- Commands ------------------------------------------------------------
    public int CommandTimeoutSeconds { get; set; } = 600;
    public int MaxCommandConcurrency { get; set; } = 1;
    public int AdminCommandMaxTimeoutSeconds { get; set; } = 900;
    public bool AdminCommandAllowlistEnabled { get; set; } = false;
    public List<string> AdminCommandAllowlist { get; set; } = new();
    public int MaxMessageSizeBytes { get; set; } = 1024 * 1024;
    public int MaxStdOutKilobytes { get; set; } = 256;

    // ---- DNS -----------------------------------------------------------------
    public DnsOptions Dns { get; set; } = new();

    // ---- Paths / state ---------------------------------------------------------
    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LanAgent");

    // ---- Agent update -----------------------------------------------------------
    /// <summary>Optional RSA public key (XML or base64 SPKI blob) used to verify UPDATE_AGENT signatures.</summary>
    public string AgentUpdatePublicKey { get; set; } = "";

    public string ServiceName => "LanAgent";

    public string ServerWebSocketUrl
    {
        get
        {
            string scheme = UseTls ? "wss" : "ws";
            string path = WebSocketPath.StartsWith('/') ? WebSocketPath : "/" + WebSocketPath;
            return $"{scheme}://{ServerIp}:{ServerPort}{path}";
        }
    }

    public string ServerHttpBaseUrl
    {
        get
        {
            string scheme = UseTls ? "https" : "http";
            return $"{scheme}://{ServerIp}:{ServerPort}";
        }
    }

    public string DefaultManagementAddress => $"http://{ServerIp}:{ServerPort}";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServerIp)) throw new InvalidOperationException("Agent.ServerIp is required.");
        if (ServerPort is < 1 or > 65535) throw new InvalidOperationException("Agent.ServerPort must be 1-65535.");
        if (string.IsNullOrWhiteSpace(EnrollmentKey) || EnrollmentKey == "CHANGE_ME_AT_INSTALL")
            throw new InvalidOperationException("Agent.EnrollmentKey must be provisioned by the installer.");
        if (HeartbeatIntervalSeconds is < 5 or > 300) throw new InvalidOperationException("Agent.HeartbeatIntervalSeconds must be 5-300.");
        if (BackoffInitialSeconds < 1) throw new InvalidOperationException("Agent.BackoffInitialSeconds must be >= 1.");
        if (BackoffMaxSeconds < BackoffInitialSeconds) throw new InvalidOperationException("Agent.BackoffMaxSeconds must be >= BackoffInitialSeconds.");
        if (MaxCommandConcurrency < 1) throw new InvalidOperationException("Agent.MaxCommandConcurrency must be >= 1.");
        if (Dns.Servers.Count == 0) Dns.Servers.Add("192.168.1.100");
    }
}

public sealed class DnsOptions
{
    public List<string> Servers { get; set; } = new() { "192.168.1.100" };
    public bool ApplyToWireless { get; set; } = false;
}
