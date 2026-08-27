namespace OMClientAgent.Core;

public static class OmPaths
{
    public const string DefaultDataDirectory = @"C:\ProgramData\OM\Client";
    public static string DataDirectory => DefaultDataDirectory;
    public static string DatabaseFile => Path.Combine(DataDirectory, "data.db");
    public static string LogsDirectory => Path.Combine(DataDirectory, "Logs");
    public static string PackagesDirectory => Path.Combine(DataDirectory, "Packages");
    public static string AgentConfigFile => Path.Combine(DataDirectory, "agent.json");
    public static string DiagnosticsFile => Path.Combine(DataDirectory, "diagnostics.json");
    public static string AppControlConfigFile => Path.Combine(DataDirectory, "appcontrol.json");
}

public static class OmApi
{
    public const string Register = "/api/agent/register";
    public const string Heartbeat = "/api/agent/heartbeat";
    public const string Sync      = "/api/agent/sync";
    public const string Result    = "/api/agent/result";
    public const string State     = "/api/agent/state";
    public const string Config    = "/api/agent/config";
    public const string Package   = "/api/agent/package";
}

public static class OmHubMethods
{
    public const string ReceiveJob                = "ReceiveJob";
    public const string ReceivePolicyUpdate       = "ReceivePolicyUpdate";
    public const string ReceiveConfigurationUpdate= "ReceiveConfigurationUpdate";
    public const string SendSyncRequest           = "SendSyncRequest";
    public const string ReceiveAgentUpdate        = "ReceiveAgentUpdate";

    public const string SendHeartbeat             = "SendHeartbeat";
    public const string SendJobAcknowledgement    = "SendJobAcknowledgement";
    public const string SendJobStarted            = "SendJobStarted";
    public const string SendJobResult             = "SendJobResult";
    public const string SendStateReport           = "SendStateReport";
    public const string RequestSync               = "RequestSync";
    public const string SendHealthReport          = "SendHealthReport";
}

public static class OmProtocol
{
    public const string Version = "1.0.0";
    public const int WireVersion = 1;
    public const int DefaultHeartbeatSeconds = 30;
    public const int DefaultOfflineGraceSeconds = 100;
}

public static class AppRoles
{
    public const string Agent = "om-agent";
}

public static class OmUrls
{
    public static IEnumerable<string> Candidates(Core.Models.AgentConfiguration cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.ServerUrl))
            yield return Normalize(cfg.ServerUrl);
        if (cfg.FallbackServerUrls is not null)
            foreach (var f in cfg.FallbackServerUrls)
                if (!string.IsNullOrWhiteSpace(f))
                    yield return Normalize(f);
    }

    public static string Normalize(string url)
    {
        var u = url.Trim().TrimEnd('/');
        return u.TrimEnd('/');
    }
}
