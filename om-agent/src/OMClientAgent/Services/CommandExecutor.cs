using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using OMClientAgent.Core;
using OMClientAgent.Core.Models;
using OMClientAgent.Infrastructure;

namespace OMClientAgent.Services;

public sealed class CommandResult
{
    public JobStatus Status { get; set; } = JobStatus.Success;
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public bool TimedOut { get; set; }
    public bool NeedsInteractiveUser { get; set; }
}

public sealed class CommandExecutor
{
    private readonly UserSessionManager _sessions;
    private readonly AgentConfigService _config;
    private readonly ILogger<CommandExecutor> _logger;

    public CommandExecutor(UserSessionManager sessions, AgentConfigService config, ILogger<CommandExecutor> logger)
    {
        _sessions = sessions;
        _config = config;
        _logger = logger;
    }

    public async Task<CommandResult> ExecuteAsync(Job job, CancellationToken ct)
    {
        var timeout = job.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(job.TimeoutSeconds) : TimeSpan.FromSeconds(_config.Current.DefaultJobTimeoutSeconds);

        if (job.Type == JobType.Restart)
            return Restart(timeout);
        if (job.Type == JobType.CollectInfo)
            return await CollectInfoAsync(timeout, ct).ConfigureAwait(false);

        var command = job.Command ?? job.Action ?? string.Empty;
        var arguments = job.Arguments ?? string.Empty;

        if (job.ExecutionMode == JobExecutionMode.User)
        {
            if (!_sessions.HasInteractiveUser())
            {
                _logger.LogInformation("Job {JobId} is USER mode but no interactive user is logged on; holding as WAITING_FOR_USER.", job.JobId);
                return new CommandResult { Status = JobStatus.WaitingForUser, NeedsInteractiveUser = true };
            }
            var (ran, exit, output) = await _sessions.TryRunInteractiveAsync($"{command} {arguments}", timeout, ct).ConfigureAwait(false);
            return new CommandResult
            {
                Status = ran ? (exit == 0 ? JobStatus.Success : JobStatus.Failed) : JobStatus.Failed,
                ExitCode = exit,
                StandardOutput = output,
                StandardError = string.Empty
            };
        }

        return await RunProcessAsync(command, arguments, timeout, ct).ConfigureAwait(false);
    }

    private async Task<CommandResult> RunProcessAsync(string file, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
        };
        return await Run(psi, timeout, ct).ConfigureAwait(false);
    }

    private async Task<CommandResult> Run(ProcessStartInfo psi, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var p = new Process { StartInfo = psi };
            p.Start();
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            bool timedOut = false;
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { timedOut = true; try { p.Kill(entireProcessTree: true); } catch { } }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            var result = new CommandResult
            {
                ExitCode = timedOut ? -1 : p.ExitCode,
                StandardOutput = Truncate(stdout),
                StandardError = Truncate(stderr),
                TimedOut = timedOut,
                Status = timedOut ? JobStatus.Timeout : (p.ExitCode == 0 ? JobStatus.Success : JobStatus.Failed)
            };
            _logger.LogDebug("Job finished exit={ExitCode} timedOut={TimedOut}", result.ExitCode, result.TimedOut);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Command execution failed: {Message}", ex.Message);
            return new CommandResult { Status = JobStatus.Failed, ExitCode = 1, StandardError = ex.Message };
        }
    }

    private CommandResult Restart(TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "shutdown.exe",
            Arguments = "/r /t 5 /c \"OM Agent initiated restart.\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return Run(psi, timeout, CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task<CommandResult> CollectInfoAsync(TimeSpan timeout, CancellationToken ct)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"MachineName: {Environment.MachineName}");
        builder.AppendLine($"OS: {Environment.OSVersion}");
        builder.AppendLine($"AgentVersion: {OmProtocol.Version}");
        try
        {
            var ip = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            builder.AppendLine($"Domain: {ip.DomainName}");
        }
        catch { }

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -Command \"Get-ComputerInfo | Out-String\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var r = await Run(psi, timeout, ct).ConfigureAwait(false);
        r.StandardOutput = builder + r.StandardOutput;
        if (r.Status != JobStatus.Timeout) r.Status = JobStatus.Success;
        return r;
    }

    private static string Truncate(string value, int max = 64 * 1024) =>
        value is null ? string.Empty : value.Length <= max ? value : value[..max];
}
