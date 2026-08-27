using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OMClientAgent.Core.Models;

namespace OMClientAgent.Infrastructure;

public sealed class AgentConfigService
{
    private readonly ILogger<AgentConfigService> _logger;
    private readonly object _lock = new();
    private AgentConfiguration _current;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public AgentConfigService(IConfiguration configuration, ILogger<AgentConfigService> logger)
    {
        _logger = logger;
        _current = configuration.GetSection("Agent").Get<AgentConfiguration>() ?? new AgentConfiguration();
        if (Path.IsPathRooted(_current.DataDirectory))
        {
            try { Directory.CreateDirectory(_current.DataDirectory); } catch { }
            if (!string.IsNullOrWhiteSpace(_current.MachineId))
                PersistOverrides();
        }
    }

    public AgentConfiguration Current => _current;

    public void Update(Action<AgentConfiguration> mutate)
    {
        lock (_lock)
        {
            mutate(_current);
            PersistOverrides();
        }
    }

    public void ApplyServerConfig(AgentConfiguration incoming)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(incoming.ServerUrl)) _current.ServerUrl = incoming.ServerUrl;
            if (incoming.FallbackServerUrls is { Count: > 0 }) _current.FallbackServerUrls = incoming.FallbackServerUrls;
            if (!string.IsNullOrWhiteSpace(incoming.SignalRHubPath)) _current.SignalRHubPath = incoming.SignalRHubPath;
            if (incoming.HeartbeatIntervalSeconds > 0) _current.HeartbeatIntervalSeconds = incoming.HeartbeatIntervalSeconds;
            if (incoming.JobPollIntervalSeconds > 0) _current.JobPollIntervalSeconds = incoming.JobPollIntervalSeconds;
            if (incoming.DefaultJobTimeoutSeconds > 0) _current.DefaultJobTimeoutSeconds = incoming.DefaultJobTimeoutSeconds;
            PersistOverrides();
            _logger.LogInformation("Applied server configuration update.");
        }
    }

    private void PersistOverrides()
    {
        try
        {
            var dir = _current.DataDirectory;
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            var file = Path.Combine(string.IsNullOrWhiteSpace(dir) ? "." : dir, "agent.json");
            File.WriteAllText(file, JsonSerializer.Serialize(new
            {
                _current.ServerUrl,
                _current.FallbackServerUrls,
                _current.SignalRHubPath,
                _current.HeartbeatIntervalSeconds,
                _current.JobPollIntervalSeconds,
                _current.MachineId
            }, Json));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist agent overrides.");
        }
    }
}
