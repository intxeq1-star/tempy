using System.Text.Json;
using LanAgent.Core.Management;

namespace LanAgent.Core.Commands.Handlers;

public sealed class GetSystemInfoHandler : ICommandHandler
{
    public string CommandType => Protocol.CommandType.GetSystemInfo;

    public Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        var info = SystemInfoCollector.CollectFull();
        return Task.FromResult(HandlerResult.Ok(info));
    }
}

public sealed class GetInstalledAppsHandler : ICommandHandler
{
    public string CommandType => Protocol.CommandType.GetInstalledApps;

    public Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        string? filter = context.PayloadString("package_id");
        var apps = InstalledAppsReader.Read(filter);
        var result = new Dictionary<string, object?>
        {
            ["apps"] = apps.Select(a => new Dictionary<string, object?>
            {
                ["name"] = a.Name,
                ["version"] = a.Version,
                ["publisher"] = a.Publisher,
                ["source"] = a.Source
            }).ToList(),
            ["count"] = apps.Count
        };
        return Task.FromResult(HandlerResult.Ok(result));
    }
}

public sealed class RunAdminCommandHandler : ICommandHandler
{
    private readonly Configuration.AgentOptions _options;
    private readonly Logging.IAgentLog _log;

    public RunAdminCommandHandler(Configuration.AgentOptions options, Logging.IAgentLog log)
    {
        _options = options;
        _log = log;
    }

    public string CommandType => Protocol.CommandType.RunAdminCommand;

    public Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        string? rawCommand = context.PayloadString("command");
        string? executable = context.PayloadString("executable");
        string? arguments = context.PayloadString("arguments");
        string? workingDir = context.PayloadString("working_dir");
        int timeoutSec = context.PayloadInt("timeout_sec", 120);

        if (string.IsNullOrWhiteSpace(rawCommand) && string.IsNullOrWhiteSpace(executable))
            return Task.FromResult(HandlerResult.Fail("payload must contain 'command' or 'executable'"));

        timeoutSec = Math.Clamp(timeoutSec, 5, _options.AdminCommandMaxTimeoutSeconds);

        string fileName;
        string args;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            fileName = executable!;
            args = arguments ?? "";
            if (!IsAllowed(fileName))
                return Task.FromResult(HandlerResult.Fail($"executable '{executable}' is not in the admin command allowlist"));
        }
        else
        {
            // Raw command lines run through the platform shell in the service's administrative context.
            var tokens = ProcessRunner.SplitCommandLine(rawCommand!);
            if (tokens.Count == 0) return Task.FromResult(HandlerResult.Fail("empty command"));
            if (!IsAllowed(tokens[0]))
                return Task.FromResult(HandlerResult.Fail($"executable '{tokens[0]}' is not in the admin command allowlist"));

            if (OperatingSystem.IsWindows())
            {
                fileName = "cmd.exe";
                args = "/c " + rawCommand;
            }
            else
            {
                fileName = "/bin/sh";
                args = "-c " + Quote(rawCommand!);
            }
        }

        _log.Info("admin_command", "executing", new { command_id = context.CommandId, timeout_sec = timeoutSec, executable = fileName });

        return RunAsync(fileName, args, timeoutSec, workingDir);
    }

    private async Task<HandlerResult> RunAsync(string fileName, string args, int timeoutSec, string? workingDir)
    {
        var result = await ProcessRunner.RunAsync(
            fileName,
            args,
            timeoutMs: timeoutSec * 1000,
            workingDir: workingDir,
            maxOutputBytes: _options.MaxStdOutKilobytes * 1024,
            outerCt: CancellationToken.None).ConfigureAwait(false);

        var payload = new Dictionary<string, object?>
        {
            ["stdout"] = result.StdOut,
            ["stderr"] = result.StdErr,
            ["exit_code"] = result.ExitCode,
            ["started_at"] = result.StartedAtIso,
            ["completed_at"] = result.CompletedAtIso,
            ["duration_ms"] = result.DurationMs,
            ["timed_out"] = result.TimedOut
        };

        if (result.TimedOut)
            return HandlerResult.Fail($"command timed out after {timeoutSec}s and was killed", payload, result.ExitCode);
        return HandlerResult.Ok(payload, result.ExitCode);
    }

    private bool IsAllowed(string executable)
    {
        if (!_options.AdminCommandAllowlistEnabled) return true;
        string name = System.IO.Path.GetFileName(executable).ToLowerInvariant();
        return _options.AdminCommandAllowlist.Any(allowed =>
            System.IO.Path.GetFileName(allowed).ToLowerInvariant() == name);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}

public sealed class RestartAgentHandler : ICommandHandler
{
    private readonly Configuration.AgentOptions _options;
    private readonly Logging.IAgentLog _log;

    public RestartAgentHandler(Configuration.AgentOptions options, Logging.IAgentLog log)
    {
        _options = options;
        _log = log;
    }

    public string CommandType => Protocol.CommandType.RestartAgent;

    public Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        // The result is reported BEFORE the restart: it is persisted and flushed within milliseconds,
        // while the restart helper waits 6s before stopping the service.
        int delay = 6;
        string script = OperatingSystem.IsWindows()
            ? $"cmd.exe"
            : "/bin/sh";
        string args = OperatingSystem.IsWindows()
            ? $"/c \"timeout /t {delay} /nobreak >nul & net stop {_options.ServiceName} & net start {_options.ServiceName}\""
            : $"-c \"sleep {delay} && echo restart-requested\"";

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = script,
                Arguments = args,
                UseShellExecute = true,
                CreateNoWindow = true
            };
            System.Diagnostics.Process.Start(psi);
            _log.Info("commands", "agent restart scheduled", new { delay_sec = delay });
            return Task.FromResult(HandlerResult.Ok(new Dictionary<string, object?>
            {
                ["restart_initiated"] = true,
                ["delay_sec"] = delay
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HandlerResult.Fail($"could not schedule restart: {ex.Message}"));
        }
    }
}
