namespace LanManagement.Server.Domain;

public enum DeviceConnectionState
{
    Offline,
    Online
}

public enum DeviceSyncState
{
    Unknown,
    Synced,
    OutOfSync,
    Syncing,
    Failed
}

public enum CommandStatus
{
    Pending,
    Sent,
    Received,
    Running,
    Success,
    Failed,
    Timeout,
    Cancelled
}

public enum DevicePolicyStatus
{
    Pending,
    Syncing,
    Synced,
    Failed
}

public enum AdminRole
{
    Viewer,
    Operator,
    Administrator
}

public enum TargetKind
{
    Device,
    Devices,
    Group,
    All
}

public static class DeviceSyncStateExtensions
{
    public static string ToDisplayValue(this DeviceSyncState state) => state switch
    {
        DeviceSyncState.Unknown => "UNKNOWN",
        DeviceSyncState.Synced => "SYNCED",
        DeviceSyncState.OutOfSync => "OUT_OF_SYNC",
        DeviceSyncState.Syncing => "SYNCING",
        DeviceSyncState.Failed => "FAILED",
        _ => "UNKNOWN"
    };
}

public static class CommandStatusExtensions
{
    public static string ToProtocolValue(this CommandStatus status) => status switch
    {
        CommandStatus.Pending => "PENDING",
        CommandStatus.Sent => "SENT",
        CommandStatus.Received => "RECEIVED",
        CommandStatus.Running => "RUNNING",
        CommandStatus.Success => "SUCCESS",
        CommandStatus.Failed => "FAILED",
        CommandStatus.Timeout => "TIMEOUT",
        CommandStatus.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static bool TryParseProtocolValue(string? value, out CommandStatus status)
    {
        status = value?.Trim().ToUpperInvariant() switch
        {
            "PENDING" => CommandStatus.Pending,
            "SENT" => CommandStatus.Sent,
            "RECEIVED" => CommandStatus.Received,
            "RUNNING" => CommandStatus.Running,
            "SUCCESS" => CommandStatus.Success,
            "FAILED" => CommandStatus.Failed,
            "TIMEOUT" => CommandStatus.Timeout,
            "CANCELLED" => CommandStatus.Cancelled,
            _ => default
        };

        return value is not null && new[]
        {
            "PENDING", "SENT", "RECEIVED", "RUNNING", "SUCCESS", "FAILED", "TIMEOUT", "CANCELLED"
        }.Contains(value.Trim().ToUpperInvariant());
    }

    public static bool IsTerminal(this CommandStatus status) => status is
        CommandStatus.Success or CommandStatus.Failed or CommandStatus.Timeout or CommandStatus.Cancelled;
}

public static class CommandTypes
{
    public const string GetSystemInfo = "GET_SYSTEM_INFO";
    public const string GetInstalledApps = "GET_INSTALLED_APPS";
    public const string InstallApp = "INSTALL_APP";
    public const string UninstallApp = "UNINSTALL_APP";
    public const string UpdateApp = "UPDATE_APP";
    public const string CheckApp = "CHECK_APP";
    public const string ApplyDns = "APPLY_DNS";
    public const string CheckDns = "CHECK_DNS";
    public const string ApplyBrowserPolicy = "APPLY_BROWSER_POLICY";
    public const string CheckBrowserPolicy = "CHECK_BROWSER_POLICY";
    public const string ApplyAppPolicy = "APPLY_APP_POLICY";
    public const string CheckAppPolicy = "CHECK_APP_POLICY";
    public const string SyncPolicy = "SYNC_POLICY";
    public const string RestartAgent = "RESTART_AGENT";
    public const string RestartPc = "RESTART_PC";
    public const string ShutdownPc = "SHUTDOWN_PC";
    public const string LockPc = "LOCK_PC";
    public const string LogoffUser = "LOGOFF_USER";
    public const string RunAdminCommand = "RUN_ADMIN_COMMAND";

    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        GetSystemInfo, GetInstalledApps, InstallApp, UninstallApp, UpdateApp, CheckApp,
        ApplyDns, CheckDns, ApplyBrowserPolicy, CheckBrowserPolicy, ApplyAppPolicy,
        CheckAppPolicy, SyncPolicy, RestartAgent, RestartPc, ShutdownPc, LockPc,
        LogoffUser, RunAdminCommand
    };

    public static IReadOnlyCollection<string> All => Supported;

    public static bool IsSupported(string? commandType) => commandType is not null && Supported.Contains(commandType);
}
