using LanAgent.Core;
using LanAgent.Core.Logging;
using Microsoft.Extensions.Hosting;

namespace LanAgent.Service;

/// <summary>Hosted service: owns the agent lifecycle. SCM stop (or Ctrl+C in console) stops it gracefully.</summary>
public sealed class AgentWorker : BackgroundService
{
    private readonly AgentEngine _engine;
    private readonly IAgentLog _log;

    public AgentWorker(AgentEngine engine, IAgentLog log)
    {
        _engine = engine;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _engine.Start();
        }
        catch (Exception ex)
        {
            _log.Error("service", "agent failed to start", ex.Message, ex);
            throw;
        }

        // Keep the worker alive until shutdown is requested; the agent runs on its own task tree.
        return Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { });
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.Info("service", "service stop requested", new { graceful = true });
        try
        {
            await _engine.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("service", "error during graceful stop", ex.Message, ex);
        }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
