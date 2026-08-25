using LanAgent.Core.Configuration;
using LanAgent.Core.Logging;
using LanAgent.Core.Sync;
using LanAgent.Core.Update;

namespace LanAgent.Core.Commands.Handlers;

public sealed class SyncPolicyHandler : ICommandHandler
{
    private readonly SynchronizationEngine _sync;
    public SyncPolicyHandler(SynchronizationEngine sync) => _sync = sync;
    public string CommandType => Protocol.CommandType.SyncPolicy;

    public async Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        bool force = context.PayloadBool("force", true);
        var outcome = await _sync.SynchronizeAsync("admin", force).ConfigureAwait(false);
        return new HandlerResult(
            outcome.Status == "FAILED" ? 1 : 0,
            new Dictionary<string, object?>
            {
                ["policy_version"] = outcome.PolicyVersion,
                ["status"] = outcome.Status,
                ["applied"] = outcome.Items.Select(i => new Dictionary<string, object?>
                {
                    ["item"] = i.Item, ["status"] = i.Status, ["detail"] = i.Detail
                }).ToList(),
                ["error"] = outcome.Error
            },
            outcome.Error);
    }
}

public sealed class UpdateAgentHandler : ICommandHandler
{
    private readonly AgentOptions _options;
    private readonly IAgentLog _log;

    public UpdateAgentHandler(AgentOptions options, IAgentLog log)
    {
        _options = options;
        _log = log;
    }

    public string CommandType => Protocol.CommandType.UpdateAgent;

    public Task<HandlerResult> ExecuteAsync(CommandContext context)
        => AgentUpdater.ExecuteAsync(context, _options, _log, _options.ServerHttpBaseUrl);
}
