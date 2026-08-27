using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using OMClientAgent.Core;
using OMClientAgent.Core.Models;
using OMClientAgent.Infrastructure;

namespace OMClientAgent.Services;

public interface IResultReporter
{
    Task SendJobResultAsync(JobResultMessage result, CancellationToken ct);
    Task SendJobAcknowledgedAsync(JobAckMessage ack, CancellationToken ct);
    Task SendJobStartedAsync(JobStartedMessage started, CancellationToken ct);
    Task SendStateReportAsync(MachineActualState state, CancellationToken ct);
    Task SendHealthAsync(HealthReportMsg health, CancellationToken ct);
    Task SendHeartbeatAsync(AgentHeartbeat heartbeat, CancellationToken ct);
    Task RequestSyncAsync(CancellationToken ct);
}

public sealed class CommunicationManager : IResultReporter, IAsyncDisposable
{
    private readonly AgentConfigService _configService;
    private readonly AuthenticationManager _auth;
    private readonly LocalDatabase _db;
    private readonly TlsTrust _tls;
    private readonly OmEvents _events;
    private readonly ILogger<CommunicationManager> _logger;
    private readonly object _lock = new();
    private HubConnection? _hub;
    private bool _httpsFallbackActive;
    private int _reconnectAttempt;

    public CommunicationManager(AgentConfigService configService, AuthenticationManager auth, LocalDatabase db, TlsTrust tls, OmEvents events, ILogger<CommunicationManager> logger)
    {
        _configService = configService;
        _auth = auth;
        _db = db;
        _tls = tls;
        _events = events;
        _logger = logger;
    }

    public ConnectionState State { get; private set; } = ConnectionState.Offline;
    public bool HttpFallbackActive => _httpsFallbackActive;
    public string? ActiveServerUrl { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        var cfg = _configService.Current;
        _logger.LogInformation("Starting OM communication loop. Primary: {Url}", cfg.ServerUrl);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var connected = await TryConnectAsync(ct).ConfigureAwait(false);
                _reconnectAttempt = connected ? 0 : _reconnectAttempt + 1;

                if (!connected)
                {
                    SetState(ConnectionState.Offline);
                    SetHttpFallback(true);
                    var delay = Backoff.DelaySeconds(_reconnectAttempt, Math.Max(60, cfg.MaxBackoffSeconds));
                    _logger.LogInformation("Not connected; HTTPS fallback active. Retrying in {Delay}.", delay);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                else
                {
                    SetHttpFallback(false);
                    SetState(ConnectionState.Connected);
                    _events.RaiseConnectionStateChanged(ConnectionState.Connected);
                    await _events.RaiseCommunicationEstablished().ConfigureAwait(false);
                    await WaitForDisconnectAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in communication loop; reconnecting.");
                SetState(ConnectionState.Offline);
                await Task.Delay(Backoff.DelaySeconds(_reconnectAttempt++, Math.Max(60, cfg.MaxBackoffSeconds)), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        var cfg = _configService.Current;

        if (!await _auth.EnsureEnrolledAsync(ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Not enrolled yet; falling back to HTTPS registration polling.");
            SetHttpFallback(true);
            return false;
        }

        _logger.LogInformation("Connecting to SignalR hub (trying primary then IP fallback).");
        foreach (var baseUrl in OmUrls.Candidates(cfg))
        {
            var hubUrl = BuildHubUrl(baseUrl);
            try
            {
                var builder = new HubConnectionBuilder()
                    .WithUrl(hubUrl, options =>
                    {
                        options.HttpMessageHandlerFactory = _ => _tls.CreateHandler();
                    });

                var hub = builder.Build();
                RegisterHandlers(hub);
                hub.Reconnecting += OnReconnecting;
                hub.Reconnected += OnReconnected;

                await hub.StartAsync(ct).ConfigureAwait(false);

                lock (_lock) _hub = hub;
                ActiveServerUrl = baseUrl;
                _db.SetSetting("LastConnectedAtUtc", DateTime.UtcNow.ToString("o"));

                await SendHeartbeatAsync(BuildHeartbeat(mode: "signalr"), ct).ConfigureAwait(false);
                await RequestSyncAsync(ct).ConfigureAwait(false);

                _logger.LogInformation("SignalR connection established to {HubUrl}.", hubUrl);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("SignalR attempt to {Base} failed: {Message}", baseUrl, ex.Message);
            }
        }
        return false;
    }

    private string BuildHubUrl(string baseUrl)
    {
        var baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        var path = _configService.Current.SignalRHubPath.TrimStart('/');
        var q = $"?role=agent&machine={WebUtility.UrlEncode(_configService.Current.MachineId)}&key={WebUtility.UrlEncode(_configService.Current.AuthKey ?? string.Empty)}";
        return new Uri(baseUri, path).ToString() + q;
    }

    private void RegisterHandlers(HubConnection hub)
    {
        hub.On<JobMessage>(OmHubMethods.ReceiveJob, msg =>
        {
            var job = msg.ToJob(_configService.Current.MachineId);
            return _events.RaiseJobReceived(job);
        });

        hub.On<PolicyMessage>(OmHubMethods.ReceivePolicyUpdate, msg =>
        {
            var policy = WirePolicy.From(msg);
            return _events.RaisePolicyReceived(policy);
        });

        hub.On<ConfigMessage>(OmHubMethods.ReceiveConfigurationUpdate, incoming =>
        {
            _configService.ApplyServerConfig(new AgentConfiguration { HeartbeatIntervalSeconds = incoming.HeartbeatSeconds, DesiredDns = incoming.DesiredDns, DesiredDnsPort = incoming.DesiredDnsPort });
            return Task.CompletedTask;
        });

        hub.On<AgentUpdateMessage>(OmHubMethods.ReceiveAgentUpdate, msg => _events.RaiseAgentUpdate(msg));
        hub.On(OmHubMethods.SendSyncRequest, () => _events.RaiseSyncRequested());
    }

    private Task OnReconnecting(Exception? ex)
    {
        SetState(ConnectionState.Degraded);
        SetHttpFallback(true);
        _logger.LogWarning(ex, "SignalR reconnecting. Falling back to HTTPS synchronization.");
        return Task.CompletedTask;
    }

    private Task OnReconnected(string? connectionId)
    {
        SetState(ConnectionState.Connected);
        SetHttpFallback(false);
        _logger.LogInformation("SignalR reconnected ({ConnectionId}).", connectionId);
        return Task.CompletedTask;
    }

    private Task WaitForDisconnectAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = _hub;
        if (hub is null) return Task.CompletedTask;
        hub.Closed += _ =>
        {
            SetState(ConnectionState.Offline);
            SetHttpFallback(true);
            tcs.TrySetResult();
            return Task.CompletedTask;
        };
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    public async Task SendJobResultAsync(JobResultMessage result, CancellationToken ct)
    {
        if (await TrySignalRSendAsync(OmHubMethods.SendJobResult, result, ct).ConfigureAwait(false)) return;
        await PostFallbackAsync(OmApi.Result, result, ct).ConfigureAwait(false);
    }

    public async Task SendJobAcknowledgedAsync(JobAckMessage ack, CancellationToken ct)
    {
        await TrySignalRSendAsync(OmHubMethods.SendJobAcknowledgement, ack, ct).ConfigureAwait(false);
    }

    public async Task SendJobStartedAsync(JobStartedMessage started, CancellationToken ct)
    {
        await TrySignalRSendAsync(OmHubMethods.SendJobStarted, started, ct).ConfigureAwait(false);
    }

    public async Task SendStateReportAsync(MachineActualState state, CancellationToken ct)
    {
        if (await TrySignalRSendAsync(OmHubMethods.SendStateReport, state, ct).ConfigureAwait(false)) return;
        await PostFallbackAsync(OmApi.State, state, ct).ConfigureAwait(false);
    }

    public async Task SendHealthAsync(HealthReportMsg health, CancellationToken ct)
    {
        await TrySignalRSendAsync(OmHubMethods.SendHealthReport, health, ct).ConfigureAwait(false);
    }

    public async Task SendHeartbeatAsync(AgentHeartbeat heartbeat, CancellationToken ct)
    {
        if (await TrySignalRSendAsync(OmHubMethods.SendHeartbeat, heartbeat, ct).ConfigureAwait(false)) return;
        await PostFallbackAsync(OmApi.Heartbeat, heartbeat, ct).ConfigureAwait(false);
    }

    public async Task RequestSyncAsync(CancellationToken ct)
    {
        var ok = await TrySignalRSendAsync(OmHubMethods.RequestSync, ct).ConfigureAwait(false);
        if (!ok)
            await _events.RaiseSyncRequested().ConfigureAwait(false);
    }

    private AgentHeartbeat BuildHeartbeat(string mode)
    {
        var m = _auth.Machine;
        return new AgentHeartbeat
        {
            MachineId = m.MachineId,
            ComputerName = m.ComputerName,
            AgentVersion = m.AgentVersion,
            IpAddress = m.AdvertisedIp,
            Mode = mode,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }

    private async Task<bool> TrySignalRSendAsync(string method, CancellationToken ct)
    {
        HubConnection? hub;
        lock (_lock) hub = _hub;
        if (hub is null || hub.State != HubConnectionState.Connected) return false;
        try { await hub.SendAsync(method, ct).ConfigureAwait(false); return true; }
        catch (Exception ex) { _logger.LogDebug("SignalR send of {Method} failed: {Message}", method, ex.Message); return false; }
    }

    private async Task<bool> TrySignalRSendAsync(string method, object payload, CancellationToken ct)
    {
        HubConnection? hub;
        lock (_lock) hub = _hub;
        if (hub is null || hub.State != HubConnectionState.Connected) return false;
        try { await hub.SendAsync(method, payload, ct).ConfigureAwait(false); return true; }
        catch (Exception ex) { _logger.LogDebug("SignalR send of {Method} failed; using HTTPS fallback: {Message}", method, ex.Message); return false; }
    }

    private async Task PostFallbackAsync(string path, object payload, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(payload, OmJson.Options), Encoding.UTF8, "application/json");
        foreach (var baseUrl in OmUrls.Candidates(_configService.Current))
        {
            try
            {
                using var http = _tls.CreateHttpClient();
                var uri = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path.TrimStart('/'));
                using var response = await http.PostAsync(uri, content, ct).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    _logger.LogDebug("HTTPS fallback to {Path} returned 401 (auth key rejected).", path);
                else if (!response.IsSuccessStatusCode)
                    _logger.LogDebug("HTTPS fallback to {Path} returned {Status}.", path, response.StatusCode);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("HTTPS fallback to {Path} via {Base} failed: {Message}", path, baseUrl, ex.Message);
            }
        }
    }

    private void SetState(ConnectionState state)
    {
        if (State == state) return;
        State = state;
        _db.SaveConnectionState(state);
        _events.RaiseConnectionStateChanged(state);
    }

    private void SetHttpFallback(bool active)
    {
        _httpsFallbackActive = active;
        _db.SetSetting("HttpFallbackActive", active ? "true" : "false");
    }

    public async ValueTask DisposeAsync()
    {
        HubConnection? hub;
        lock (_lock) { hub = _hub; _hub = null; }
        if (hub is not null) await hub.DisposeAsync().ConfigureAwait(false);
    }
}
