using LanAgent.Core.Logging;
using LanAgent.Core.Management;

namespace LanAgent.Core.Commands.Handlers;

public sealed class RestartPcHandler : ICommandHandler
{
    private readonly IAgentLog _log;
    public RestartPcHandler(IAgentLog log) => _log = log;
    public string CommandType => Protocol.CommandType.RestartPc;

    public async Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        int delay = context.PayloadInt("delay_sec", 15);
        string message = context.PayloadString("message") ?? "Restart issued by LAN management";
        // Result is reported before the machine goes down (delay >= 5s gives the agent time to flush).
        var result = await PowerController.RestartAsync(delay, message, _log).ConfigureAwait(false);
        return result.ExitCode == 0
            ? HandlerResult.Ok(new Dictionary<string, object?> { ["scheduled"] = true, ["delay_sec"] = Math.Clamp(delay, 5, 600) })
            : HandlerResult.Fail($"shutdown.exe exit {result.ExitCode}: {result.StdErr}");
    }
}

public sealed class ShutdownPcHandler : ICommandHandler
{
    private readonly IAgentLog _log;
    public ShutdownPcHandler(IAgentLog log) => _log = log;
    public string CommandType => Protocol.CommandType.ShutdownPc;

    public async Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        int delay = context.PayloadInt("delay_sec", 15);
        string message = context.PayloadString("message") ?? "Shutdown issued by LAN management";
        var result = await PowerController.ShutdownAsync(delay, message, _log).ConfigureAwait(false);
        return result.ExitCode == 0
            ? HandlerResult.Ok(new Dictionary<string, object?> { ["scheduled"] = true, ["delay_sec"] = Math.Clamp(delay, 5, 600) })
            : HandlerResult.Fail($"shutdown.exe exit {result.ExitCode}: {result.StdErr}");
    }
}

public sealed class LockPcHandler : ICommandHandler
{
    private readonly IAgentLog _log;
    public LockPcHandler(IAgentLog log) => _log = log;
    public string CommandType => Protocol.CommandType.LockPc;

    public Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        bool locked = PowerController.Lock(_log);
        return Task.FromResult(locked
            ? HandlerResult.Ok(new Dictionary<string, object?> { ["locked"] = true, ["method"] = "WTSDisconnectSession" })
            : HandlerResult.Fail("no active console session to lock (or WTS call failed)"));
    }
}

public sealed class LogoffUserHandler : ICommandHandler
{
    private readonly IAgentLog _log;
    public LogoffUserHandler(IAgentLog log) => _log = log;
    public string CommandType => Protocol.CommandType.LogoffUser;

    public Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        int count = PowerController.LogoffUsers(_log);
        return Task.FromResult(HandlerResult.Ok(new Dictionary<string, object?> { ["sessions_logged_off"] = count }));
    }
}
