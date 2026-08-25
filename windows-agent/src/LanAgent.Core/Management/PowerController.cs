using LanAgent.Core.Logging;

namespace LanAgent.Core.Management;

/// <summary>Power / session actions: restart, shutdown, lock (disconnect console session), logoff.</summary>
public static class PowerController
{
    /// <summary>Schedules a restart. The result of the originating command is reported BEFORE this takes effect
    /// (callers must keep delay >= 5s so the result can flush).</summary>
    public static async Task<ProcessResult> RestartAsync(int delaySeconds, string message, IAgentLog log)
    {
        int delay = Math.Clamp(delaySeconds, 5, 600);
        string safeMessage = message.Replace("\"", "");
        string args = $"/r /t {delay} /d p:4:1 /c \"{safeMessage}\"";
        var result = await ProcessRunner.RunAsync("shutdown.exe", args, timeoutMs: 30_000).ConfigureAwait(false);
        log.Info("power", "restart scheduled", new { delay_sec = delay, exit_code = result.ExitCode });
        return result;
    }

    public static async Task<ProcessResult> ShutdownAsync(int delaySeconds, string message, IAgentLog log)
    {
        int delay = Math.Clamp(delaySeconds, 5, 600);
        string safeMessage = message.Replace("\"", "");
        string args = $"/s /t {delay} /d p:4:1 /c \"{safeMessage}\"";
        var result = await ProcessRunner.RunAsync("shutdown.exe", args, timeoutMs: 30_000).ConfigureAwait(false);
        log.Info("power", "shutdown scheduled", new { delay_sec = delay, exit_code = result.ExitCode });
        return result;
    }

    /// <summary>Locks the workstation by disconnecting the active console session (documented WTS API).</summary>
    public static bool Lock(IAgentLog log)
    {
        bool ok = WindowsApi.LockWorkStation();
        log.Info("power", "lock requested", new { ok, method = "WTSDisconnectSession" });
        return ok;
    }

    /// <summary>Logs off all active interactive sessions (documented WTS API).</summary>
    public static int LogoffUsers(IAgentLog log)
    {
        int count = WindowsApi.LogoffInteractiveSessions();
        log.Info("power", "logoff executed", new { sessions = count });
        return count;
    }
}
