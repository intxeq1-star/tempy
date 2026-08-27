using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OMAgent;

public sealed class WorkerService : BackgroundService
{
    private readonly AppConfig _config;
    private readonly ScriptRunner _runner;
    private readonly ILogger<WorkerService> _logger;

    public WorkerService(AppConfig config, ScriptRunner runner, ILogger<WorkerService> logger)
    {
        _config = config;
        _runner = runner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var appControlCfg = AppControlService.LoadConfig();
        var blockedCount = appControlCfg.Rules.Count(r => r.Action.Equals("BLOCK", StringComparison.OrdinalIgnoreCase) && r.Enabled);

        var status = new WorkerStatus
        {
            State = "RUNNING",
            ProcessId = Environment.ProcessId,
            ServiceName = OmConstants.ServiceName,
            Version = _config.AgentVersion,
            MachineName = Environment.MachineName,
            StartedAtUtc = DateTime.UtcNow.ToString("o"),
            BlockedAppsCount = blockedCount
        };
        StatusStore.Write(status);
        Logger.Info($"Worker {_config.AgentVersion} started (PID {Environment.ProcessId}) on {Environment.MachineName}. Active app blocks: {blockedCount}");

        // Initial enforcement of application control blocks
        try
        {
            AppControlService.EnforceAll(appControlCfg);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Initial AppControl enforcement warning: {ex.Message}");
        }

        var runCount = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Re-enforce app control each cycle to kill any prohibited processes that were launched
                try
                {
                    var liveAppControl = AppControlService.LoadConfig();
                    AppControlService.EnforceAll(liveAppControl);
                    status.BlockedAppsCount = liveAppControl.Rules.Count(r => r.Action.Equals("BLOCK", StringComparison.OrdinalIgnoreCase) && r.Enabled);
                }
                catch { }

                var result = await _runner.RunAsync(_config, stoppingToken, pid =>
                {
                    status.LastScriptPid = pid;
                    StatusStore.Write(status);
                }).ConfigureAwait(false);
                runCount++;

                status.LastRunAtUtc = DateTime.UtcNow.ToString("o");
                status.ScriptUseCount = runCount;
                status.ContainerProcessId = result.Started ? $"powershell (PID {Environment.ProcessId})" : "";
                status.LastExitCode = result.ExitCode;
                status.LastScriptPid = result.Pid;
                status.LastOutput = Clip(result.Output, 4000);
                status.LastRunStatus = !result.Started ? "SKIPPED"
                                          : result.TimedOut ? "TIMEOUT"
                                          : result.ExitCode == 0 ? "OK"
                                          : "ERROR";
                if (result.Started) status.State = "RUNNING";
                StatusStore.Write(status);

                if (_config.RunIntervalSeconds <= 0)
                    break;

                await WaitIntervalAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker cycle failed.");
                Logger.Error("Worker cycle failed", ex);
                status.State = "ERROR";
                status.LastOutput = Clip(ex.Message, 4000);
                StatusStore.Write(status);
            }
        }

        status.State = "STOPPED";
        StatusStore.Write(status);
        Logger.Info("Worker stopped.");
    }

    private async Task WaitIntervalAsync(CancellationToken ct)
    {
        var total = TimeSpan.FromSeconds(Math.Max(1, _config.RunIntervalSeconds));
        var tick = TimeSpan.FromSeconds(Math.Max(3, Math.Min(_config.HeartbeatSeconds, total.TotalSeconds)));
        var elapsed = TimeSpan.Zero;
        while (elapsed < total && !ct.IsCancellationRequested)
        {
            try { await Task.Delay(tick, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            elapsed += tick;

            // Enforce process blocks during heartbeat intervals as well
            try
            {
                var liveAppControl = AppControlService.LoadConfig();
                AppControlService.EnforceAll(liveAppControl);
            }
            catch { }

            var st = StatusStore.Read();
            st.State = "RUNNING";
            st.LastHeartbeatUtc = DateTime.UtcNow.ToString("o");
            StatusStore.Write(st);
            Logger.Info("Heartbeat " + DateTime.UtcNow.ToString("HH:mm:ss"));
        }
    }

    private static string Clip(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + " …(truncated)";
}
