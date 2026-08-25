using LanAgent.Core.Commands;
using LanAgent.Core.Commands.Handlers;
using LanAgent.Core.Configuration;
using LanAgent.Core.Connection;
using LanAgent.Core.Identity;
using LanAgent.Core.Logging;
using LanAgent.Core.Management;
using LanAgent.Core.Persistence;
using LanAgent.Core.Protocol;
using LanAgent.Core.Sync;
using LanAgent.Core.Transport;
using LanAgent.Core.Util;

namespace LanAgent.Core;

/// <summary>
/// Composition root of the agent: wires identity, state, management integrations, command engine,
/// synchronization and THE connection manager, and dispatches every inbound protocol message.
/// Implemented as ISessionCoordinator + ISessionFacts + IHeartbeatInfo for the connection manager.
/// </summary>
public sealed class AgentEngine : ISessionCoordinator, ISessionFacts, IHeartbeatInfo
{
    private readonly AgentOptions _options;
    private readonly IAgentLog _log;
    private readonly SqliteStateStore _store;
    private readonly DeviceIdentityService _identity;
    private readonly ConnectionManager _connection;
    private readonly CommandService _commands;
    private readonly SynchronizationEngine _sync;

    private CancellationTokenSource? _cts;
    private readonly List<Task> _backgroundTasks = new();
    private readonly object _debounceLock = new();
    private CancellationTokenSource? _policyChangeDebounce;
    private DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private volatile bool _firstSession = true;
    private volatile bool _started;
    private readonly string _hostname;
    private string? _localIp;

    public AgentEngine(AgentOptions options, IAgentLog log)
    {
        _options = options;
        _log = log;
        _hostname = Environment.MachineName;
        _localIp = SystemInfoCollector.GetLocalIPv4().FirstOrDefault();

        Directory.CreateDirectory(options.DataDirectory);
        Directory.CreateDirectory(Path.Combine(options.DataDirectory, "logs"));

        _store = new SqliteStateStore(Path.Combine(options.DataDirectory, "state.db"), log);
        _identity = new DeviceIdentityService(_store, options, log)
        {
            EnrollmentKey = options.EnrollmentKey
        };

        // Management integrations
        var winget = new WingetClient(log);
        IDnsManager dns = new DnsManager(log);
        IBrowserPolicyManager browser = new BrowserPolicyManager(log);
        IAppPolicyManager appPolicy = new AppPolicyManager(log);
        var applier = new DesiredStateApplier(dns, browser, appPolicy, winget, log);

        // Connection (single owner of the server socket)
        _connection = new ConnectionManager(
            options,
            _identity,
            transportFactory: () => new WebSocketTransport(options.MaxMessageSizeBytes),
            coordinator: this,
            facts: this,
            heartbeatInfo: this,
            log: log);

        _sync = new SynchronizationEngine(options, _store, applier, SendAsync, log, _identity.DeviceId);

        var handlers = new List<ICommandHandler>
        {
            new GetSystemInfoHandler(),
            new GetInstalledAppsHandler(),
            new InstallAppHandler(winget),
            new UninstallAppHandler(winget),
            new UpdateAppHandler(winget),
            new CheckAppHandler(winget),
            new ApplyDnsHandler(dns, options),
            new CheckDnsHandler(dns, options),
            new ApplyBrowserPolicyHandler(browser, _store),
            new CheckBrowserPolicyHandler(browser),
            new RemoveBrowserPolicyHandler(browser),
            new ApplyAppPolicyHandler(appPolicy),
            new CheckAppPolicyHandler(appPolicy),
            new SyncPolicyHandler(_sync),
            new RestartAgentHandler(options, log),
            new RestartPcHandler(log),
            new ShutdownPcHandler(log),
            new LockPcHandler(log),
            new LogoffUserHandler(log),
            new RunAdminCommandHandler(options, log),
            new UpdateAgentHandler(options, log)
        };

        _commands = new CommandService(options, _store, handlers, SendAsync, log, _identity.DeviceId);
        _connection.MessageReceived += OnMessageReceived;
        _connection.StateChanged += state =>
            _log.Info("connection", "state changed", new { state = state.ToString().ToUpperInvariant() });
    }

    public string DeviceId => _identity.DeviceId;
    public ConnectionState ConnectionState => _connection.State;

    // ------------------------------------------------------------ ISessionFacts / IHeartbeatInfo

    int ISessionFacts.CurrentPolicyVersion() => _store.GetPolicyState().PolicyVersion;
    string? ISessionFacts.LastSyncIso() => _store.GetPolicyState().LastSyncIso;
    int ISessionFacts.PendingResultsCount() => _commands.PendingResults();
    int IHeartbeatInfo.PolicyVersion => _store.GetPolicyState().PolicyVersion;
    string IHeartbeatInfo.AgentVersion => AgentInfo.Version;
    string IHeartbeatInfo.Hostname => _hostname;
    string? IHeartbeatInfo.LocalIp => _localIp ??= SystemInfoCollector.GetLocalIPv4().FirstOrDefault();

    private Task<bool> SendAsync(Dictionary<string, object?> message) => _connection.SendAsync(message);

    // ---------------------------------------------------------------- lifecycle

    public void Start()
    {
        if (_started) return;
        _started = true;
        _startedAtUtc = DateTimeOffset.UtcNow;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _log.Info("agent", "agent starting", new
        {
            agent_version = AgentInfo.Version,
            device_id = _identity.DeviceId,
            hostname = _hostname,
            server = _options.ServerWebSocketUrl,
            data_directory = _options.DataDirectory,
            heartbeat_interval_sec = _options.HeartbeatIntervalSeconds
        });

        _commands.Start(ct);
        _backgroundTasks.Add(Task.Run(() => _connection.RunAsync(ct), ct));
        _backgroundTasks.Add(Task.Run(() => PeriodicReconciliationAsync(ct), ct));

        // Keep local IP current (DHCP changes, adapter swaps) without touching identity.
        _backgroundTasks.Add(Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                _localIp = SystemInfoCollector.GetLocalIPv4().FirstOrDefault();
            }
        }, ct));
    }

    public async Task StopAsync()
    {
        if (!_started) return;
        _log.Info("agent", "agent stopping", new { device_id = _identity.DeviceId });
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { await _commands.StopAsync().ConfigureAwait(false); } catch { /* ignore */ }
        try { await Task.WhenAll(_backgroundTasks).ConfigureAwait(false); }
        catch { /* shutdown path: background tasks must swallow their own cancellations */ }
        _store.Dispose();
        _log.Info("agent", "agent stopped", null);
        _log.Flush();
    }

    /// <summary>Safety reconciliation loop (PROTOCOL_CONTRACT §11): sync periodically, never push-only.</summary>
    private async Task PeriodicReconciliationAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(60, _options.SyncIntervalSeconds));
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                if (_connection.State == ConnectionState.Ready)
                {
                    _log.Info("sync", "periodic reconciliation starting", new { interval_sec = interval.TotalSeconds });
                    await _sync.SynchronizeAsync("periodic").ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log.Error("sync", "periodic reconciliation failed", ex.Message, ex);
            }
        }
    }

    // ----------------------------------------------------- ISessionCoordinator

    /// <summary>Post-authentication sequence on every session (PROTOCOL_CONTRACT §22):
    /// synchronize → flush result outbox → fetch pending commands.</summary>
    public async Task OnSessionEstablishedAsync(ConnectionSession session)
    {
        string reason = _firstSession ? "boot" : "reconnect";
        _firstSession = false;

        try
        {
            var outcome = await _sync.SynchronizeAsync(reason).ConfigureAwait(false);
            _log.Info("agent", "post-connect sync finished", new { reason, status = outcome.Status, policy_version = outcome.PolicyVersion });
        }
        catch (Exception ex)
        {
            _log.Error("agent", "post-connect sync failed (will retry on periodic reconciliation)", ex.Message, ex);
        }

        try { await _commands.FlushOutboxAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.Error("agent", "outbox flush failed", ex.Message, ex); }

        try { await _commands.RequestPendingAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.Error("agent", "pending-command fetch failed", ex.Message, ex); }
    }

    // ------------------------------------------------------- message dispatch

    private void OnMessageReceived(AgentMessage msg)
    {
        switch (msg.Type)
        {
            case MsgType.COMMAND:
                _ = Task.Run(async () =>
                {
                    try { await _commands.HandleCommandMessageAsync(msg).ConfigureAwait(false); }
                    catch (Exception ex) { _log.Error("commands", "COMMAND handling crashed", ex.Message, ex); }
                });
                break;

            case MsgType.SYNC_PAYLOAD:
                _sync.OnSyncPayload(msg);
                break;

            case MsgType.COMMAND_RESULT_ACK:
                _commands.HandleResultAck(msg);
                break;

            case MsgType.SYNC_REQUEST:
            {
                string reason = msg.GetString("reason") ?? "admin";
                bool force = msg.GetBool("force", true);
                _ = Task.Run(async () =>
                {
                    try { await _sync.SynchronizeAsync(reason, force).ConfigureAwait(false); }
                    catch (Exception ex) { _log.Error("sync", "server-requested sync failed", ex.Message, ex); }
                });
                break;
            }

            case MsgType.POLICY_CHANGED:
                _log.Info("sync", "policy changed notification", new { policy_version = msg.GetInt("policy_version") });
                DebouncedPolicySync();
                break;

            case MsgType.PING:
                _ = Task.Run(async () =>
                {
                    await SendAsync(Protocol.Make(MsgType.PONG, _identity.DeviceId, new Dictionary<string, object?>
                    {
                        ["pong_for"] = msg.MsgId,
                        ["pong_timestamp"] = Tx.NowIso()
                    })).ConfigureAwait(false);
                });
                break;

            case MsgType.GET_STATE:
                _ = Task.Run(async () =>
                {
                    await SendAsync(BuildStateReport()).ConfigureAwait(false);
                });
                break;

            case MsgType.ERROR:
                _log.Warn("agent", "server reported error", new
                {
                    code = msg.GetString("code"),
                    message = msg.GetString("message"),
                    fatal = msg.GetBool("fatal", false)
                });
                break;

            default:
                _log.Debug("agent", "unknown message type ignored", new { type = msg.Type });
                break;
        }
    }

    /// <summary>POLICY_CHANGED is debounced (2s) so bursts collapse into one sync run.</summary>
    private void DebouncedPolicySync()
    {
        lock (_debounceLock)
        {
            _policyChangeDebounce?.Cancel();
            _policyChangeDebounce?.Dispose();
            _policyChangeDebounce = new CancellationTokenSource();
            var token = _policyChangeDebounce.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    await _sync.SynchronizeAsync("policy_changed").ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* superseded by a newer notification */ }
                catch (Exception ex) { _log.Error("sync", "policy-changed sync failed", ex.Message, ex); }
            }, token);
        }
    }

    private Dictionary<string, object?> BuildStateReport()
    {
        var policy = _store.GetPolicyState();
        var quick = SystemInfoCollector.CollectQuick();
        return Protocol.Make(MsgType.STATE_REPORT, _identity.DeviceId, new Dictionary<string, object?>
        {
            ["hostname"] = quick.Hostname,
            ["os_version"] = quick.OsVersion,
            ["agent_version"] = AgentInfo.Version,
            ["local_ip"] = quick.LocalIp,
            ["all_ips"] = quick.AllIps,
            ["service_status"] = _started ? "RUNNING" : "STOPPED",
            ["connection_state"] = _connection.State.ToString().ToUpperInvariant(),
            ["policy_version"] = policy.PolicyVersion,
            ["last_successful_sync"] = policy.LastSyncIso,
            ["agent_started_at"] = Tx.ToIso(_startedAtUtc),
            ["commands_running"] = _commands.RunningCommandIds(),
            ["pending_results"] = _commands.PendingResults(),
            ["server"] = _options.ServerWebSocketUrl,
            ["management_base"] = _options.DefaultManagementAddress
        });
    }
}
