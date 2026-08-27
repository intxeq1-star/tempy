using System.Text.Json;

namespace OMAgent;

public sealed class WorkerStatus
{
    public string State { get; set; } = "STOPPED";
    public string ServiceName { get; set; } = OmConstants.ServiceName;
    public string Version { get; set; } = "1.0.0";
    public int ProcessId { get; set; }
    public string ContainerProcessId { get; set; } = "";
    public string MachineName { get; set; } = Environment.MachineName;
    public string StartedAtUtc { get; set; } = "";
    public string LastHeartbeatUtc { get; set; } = "";
    public string LastRunAtUtc { get; set; } = "";
    public string LastRunStatus { get; set; } = "";
    public int LastExitCode { get; set; }
    public string LastOutput { get; set; } = "";
    public int ScriptUseCount { get; set; }
    public int LastScriptPid { get; set; } = -1;
    public int BlockedAppsCount { get; set; } = 0;
}

public static class StatusStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Lock = new();

    public static void Write(WorkerStatus status)
    {
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(OmConstants.DataRoot);
                File.WriteAllText(OmConstants.StatusPath, JsonSerializer.Serialize(status, Json));
            }
            catch { }
        }
    }

    public static WorkerStatus Read()
    {
        try
        {
            if (File.Exists(OmConstants.StatusPath))
                return JsonSerializer.Deserialize<WorkerStatus>(File.ReadAllText(OmConstants.StatusPath)) ?? new WorkerStatus();
        }
        catch { }
        return new WorkerStatus();
    }
}
