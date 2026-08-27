using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OMClientAgent.Core;
using OMClientAgent.Core.Models;
using OMClientAgent.Infrastructure;

namespace OMClientAgent.Services;

public sealed class SynchronizationManager
{
    private readonly LocalDatabase _db;
    private readonly AgentConfigService _config;
    private readonly TlsTrust _tls;
    private readonly IResultReporter _reporter;
    private readonly PolicyManager _policies;
    private readonly SoftwareManager _software;
    private readonly DnsManager _dnsManager;
    private readonly ILogger<SynchronizationManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SynchronizationManager(LocalDatabase db, AgentConfigService config, TlsTrust tls, IResultReporter reporter,
        PolicyManager policies, SoftwareManager software, DnsManager dnsManager, ILogger<SynchronizationManager> logger)
    {
        _db = db;
        _config = config;
        _tls = tls;
        _reporter = reporter;
        _policies = policies;
        _software = software;
        _dnsManager = dnsManager;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            _logger.LogDebug("Synchronization already in progress; skipping this trigger.");
            return;
        }
        try
        {
            var request = BuildSyncRequest();
            var response = await PostSyncAsync(request, ct).ConfigureAwait(false);
            if (response is null) return;

            await ApplyResponseAsync(response, ct).ConfigureAwait(false);
            await ReconcilePoliciesAsync(ct).ConfigureAwait(false);

            _db.SetSetting("LastSyncAtUtc", DateTime.UtcNow.ToString("o"));
            _logger.LogInformation("Synchronization round complete (revision {Revision}).", response.Revision);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Synchronization round failed (will retry).");
        }
        finally { _gate.Release(); }
    }

    private SyncRequest BuildSyncRequest()
    {
        var cfg = _config.Current;
        var lastSync = ParseUtc(_db.GetSetting("LastSyncAtUtc")) ?? DateTime.MinValue;

        var results = _db.GetJobsCompletedSince(lastSync)
            .Select(j => new JobResultMessage
            {
                JobId = j.JobId,
                MachineId = j.TargetMachineId,
                Status = j.Status,
                ExitCode = j.ExitCode,
                Output = CombineOutput(j.StandardOutput, j.StandardError),
                StartedAt = ToUnix(j.StartedAtUtc ?? j.CreatedAtUtc),
                CompletedAt = ToUnix(j.CompletedAtUtc ?? DateTime.UtcNow)
            }).ToList();

        var state = BuildActualState();

        return new SyncRequest
        {
            MachineId = cfg.MachineId,
            ComputerName = Environment.MachineName,
            AgentVersion = OmProtocol.Version,
            IpAddress = GetPrimaryIp(),
            LastRevision = _db.Cursor.LastServerRevision,
            LocalResults = results,
            ActualState = state
        };
    }

    private MachineActualState BuildActualState()
    {
        var cfg = _config.Current;
        var latest = _policies.GetLatestPolicies();
        var version = latest.Count > 0 ? "v" + latest.Max(p => p.Version) : "v0";

        List<string> sw = new();
        try { sw = _software.ListInstalledAsync().GetAwaiter().GetResult().Select(s => NameWithVersion(s)).ToList(); }
        catch { }

        var health = new { cpu = 0, diskFreeGb = DiskFreeGb(), service = HttpServiceState() };

        return new MachineActualState
        {
            MachineId = cfg.MachineId,
            PolicyVersionActual = version,
            DnsActual = _dnsManager?.GetCurrentDns() ?? _db.GetSetting("ActualDns"),
            InstalledSoftwareJson = JsonSerializer.Serialize(sw, OmJson.Options),
            HealthJson = JsonSerializer.Serialize(health, OmJson.Options)
        };
    }

    private async Task<SyncResponse?> PostSyncAsync(SyncRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.Current.AuthKey)) return null;
        foreach (var baseUrl in OmUrls.Candidates(_config.Current))
        {
            try
            {
                using var http = _tls.CreateHttpClient();
                using var response = await http.PostAsJsonAsync(new Uri(new Uri(baseUrl), OmApi.Sync), request, OmJson.Options, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("POST /api/agent/sync via {Base} returned {Status}.", baseUrl, response.StatusCode);
                    continue;
                }
                return await response.Content.ReadFromJsonAsync<SyncResponse>(OmJson.Options, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("POST /api/agent/sync via {Base} failed: {Message}", baseUrl, ex.Message);
            }
        }
        return null;
    }

    private async Task ApplyResponseAsync(SyncResponse resp, CancellationToken ct)
    {
        if (resp.Revision > _db.Cursor.LastServerRevision)
            _db.SaveSyncCursor(new SyncCursor { LastServerRevision = resp.Revision, LastUploadedResultRevision = _db.Cursor.LastUploadedResultRevision });

        if (resp.Jobs is { Count: > 0 })
        {
            foreach (var msg in resp.Jobs)
            {
                if (string.IsNullOrWhiteSpace(msg.JobId)) continue;
                if (_db.HasCompletedJob(msg.JobId)) continue;
                if (_db.GetJob(msg.JobId) is not null) continue;
                _db.UpsertJob(msg.ToJob(_config.Current.MachineId));
            }
            _logger.LogInformation("Pulled {Count} pending job(s).", resp.Jobs.Count);
        }

        if (resp.Policies is { Count: > 0 })
        {
            foreach (var msg in resp.Policies)
                await _policies.HandlePolicyAsync(WirePolicy.From(msg), ct).ConfigureAwait(false);
            _logger.LogInformation("Received {Count} policy update(s).", resp.Policies.Count);
        }

        if (resp.Config is not null)
            _config.ApplyServerConfig(new AgentConfiguration
            {
                HeartbeatIntervalSeconds = resp.Config.HeartbeatSeconds > 0 ? resp.Config.HeartbeatSeconds : OmProtocol.DefaultHeartbeatSeconds,
                DesiredDns = resp.Config.DesiredDns,
                DesiredDnsPort = resp.Config.DesiredDnsPort
            });

        if (resp.AgentUpdate is not null)
            await StageAgentUpdateAsync(resp.AgentUpdate, ct).ConfigureAwait(false);
    }

    public async Task StageAgentUpdateAsync(AgentUpdateMessage update, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Server offered agent version {Version} (required={Required}).", update.Version, update.Required);
            if (string.IsNullOrWhiteSpace(update.DownloadUrl) || string.IsNullOrWhiteSpace(update.Sha256)) return;
            var dir = _config.Current.DataDirectory;
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, "agent-update.exe");
            await _software.DownloadAndVerifyAsync(update.DownloadUrl, dest, update.Sha256, ct).ConfigureAwait(false);
            _db.SetSetting("StagedUpdate", dest);
            _db.SetSetting("StagedUpdateVersion", update.Version ?? string.Empty);
            _logger.LogInformation("Staged agent update v{Version} for install on next restart.", update.Version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not stage agent update.");
        }
    }

    private async Task ReconcilePoliciesAsync(CancellationToken ct)
        => await _policies.ReconcileAsync(ct).ConfigureAwait(false);

    private static string CombineOutput(string? stdout, string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stdout) && string.IsNullOrWhiteSpace(stderr)) return string.Empty;
        return (stdout ?? "") + (string.IsNullOrWhiteSpace(stderr) ? "" : Environment.NewLine + stderr);
    }

    private static string NameWithVersion(InstalledSoftware s) => string.IsNullOrWhiteSpace(s.Version) ? s.Name : $"{s.Name} {s.Version}";
    private static long ToUnix(DateTime utc) => (long)(utc - DateTime.UnixEpoch).TotalSeconds;

    private double DiskFreeGb()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(_config.Current.DataDirectory) ?? Path.GetPathRoot(Environment.CurrentDirectory) ?? @"C:\");
            return Math.Round(drive.AvailableFreeSpace / (1024.0 * 1024 * 1024), 1);
        }
        catch { return 0; }
    }

    private string HttpServiceState() => _db.GetSetting("HttpFallbackActive") == "true" ? "degraded" : "running";
    private static DateTime? ParseUtc(string? value) => DateTime.TryParse(value, out var d) ? d : (DateTime?)null;

    private static string? GetPrimaryIp()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                    ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue;
                foreach (var addr in props.UnicastAddresses)
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return addr.Address.ToString();
            }
        }
        catch { }
        return null;
    }
}
