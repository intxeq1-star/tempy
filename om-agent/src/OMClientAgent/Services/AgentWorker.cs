using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OMClientAgent.Core;
using OMClientAgent.Core.Models;
using OMClientAgent.Infrastructure;

namespace OMClientAgent.Services;

public sealed class AgentWorker : BackgroundService
{
    private readonly AgentConfigService _config;
    private readonly MachineIdentityProvider _identity;
    private readonly LocalDatabase _db;
    private readonly CommunicationManager _communication;
    private readonly JobManager _jobs;
    private readonly HealthManager _health;
    private readonly SynchronizationManager _sync;
    private readonly OmEvents _events;
    private readonly ILogger<AgentWorker> _logger;
    private readonly List<Task> _loops = new();

    public AgentWorker(AgentConfigService config, MachineIdentityProvider identity, LocalDatabase db,
        CommunicationManager communication, JobManager jobs, HealthManager health, SynchronizationManager sync,
        OmEvents events, ILogger<AgentWorker> logger)
    {
        _config = config;
        _identity = identity;
        _db = db;
        _communication = communication;
        _jobs = jobs;
        _health = health;
        _sync = sync;
        _events = events;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OM Client Agent starting (version {Version}).", OmProtocol.Version);
        if (_config.Current.StartupDelayMilliseconds > 0)
            await Task.Delay(_config.Current.StartupDelayMilliseconds, stoppingToken).ConfigureAwait(false);

        await StartupSequenceAsync(stoppingToken).ConfigureAwait(false);

        _events.CommunicationEstablished += () => _sync.RunAsync(stoppingToken);
        _events.SyncRequested += () => _sync.RunAsync(stoppingToken);
        _events.AgentUpdate += msg => _sync.StageAgentUpdateAsync(msg, stoppingToken);

        SetLoop(CommunicationLoop(stoppingToken));
        SetLoop(JobLoop(stoppingToken));
        SetLoop(HeartbeatLoop(stoppingToken));
        SetLoop(SyncLoop(stoppingToken));

        _logger.LogInformation("OM Client Agent running. Primary server: {Url}", _config.Current.ServerUrl);

        try
        {
            await Task.WhenAll(_loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogCritical(ex, "OM Client Agent terminated unexpectedly."); }
        finally
        {
            _logger.LogInformation("OM Client Agent stopping.");
        }
    }

    private async Task StartupSequenceAsync(CancellationToken ct)
    {
        _logger.LogInformation("Step 1/9 - Load local configuration.");
        var identity = _identity.GetOrCreate();
        _config.Update(cfg => cfg.MachineId = identity.MachineId);
        _logger.LogInformation("Step 2/9 - Machine ID loaded: {MachineId} ({ComputerName})", identity.MachineId, identity.ComputerName);
        _logger.LogInformation("Step 3/9 - Local database ready at: {Db}", GetDbPath());
        _logger.LogInformation("Step 4/9 - Network detection: {Network}", System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable());
        _db.SaveConnectionState(ConnectionState.Degraded);
        _logger.LogInformation("Step 5/9 - Agent ready. Establishing outbound channel to server.");
        await Task.CompletedTask;
    }

    private string GetDbPath() => _config.Current.DataDirectory;

    private async Task CommunicationLoop(CancellationToken ct)
    {
        try { await _communication.RunAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogCritical(ex, "Communication loop crashed."); }
    }

    private async Task JobLoop(CancellationToken ct)
    {
        try { await _jobs.RunAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogCritical(ex, "Job loop crashed."); }
    }

    private async Task HeartbeatLoop(CancellationToken ct)
    {
        var heartbeatEvery = TimeSpan.FromSeconds(Math.Max(5, _config.Current.HeartbeatIntervalSeconds));
        var healthEvery = TimeSpan.FromSeconds(60);
        var nextHealth = DateTime.UtcNow + healthEvery;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(heartbeatEvery, ct).ConfigureAwait(false);
                await _health.SendHeartbeatAsync(ct).ConfigureAwait(false);
                if (DateTime.UtcNow >= nextHealth)
                {
                    await _health.SendHealthAsync(ct).ConfigureAwait(false);
                    nextHealth = DateTime.UtcNow + healthEvery;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Heartbeat cycle error."); }
        }
    }

    private async Task SyncLoop(CancellationToken ct)
    {
        var every = TimeSpan.FromSeconds(Math.Max(10, _config.Current.SyncIntervalSeconds));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(every, ct).ConfigureAwait(false);
                await _sync.RunAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Sync cycle error."); }
        }
    }

    private void SetLoop(Task t) => _loops.Add(t);
}
