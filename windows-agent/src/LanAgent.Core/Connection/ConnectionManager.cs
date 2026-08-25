using System.Threading.Channels;
using LanAgent.Core.Configuration;
using LanAgent.Core.Identity;
using LanAgent.Core.Logging;
using LanAgent.Core.Protocol;
using LanAgent.Core.Transport;

namespace LanAgent.Core.Connection;

/// <summary>Connection states (PROTOCOL_CONTRACT §9).</summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Authenticating,
    Connected,
    Synchronizing,
    Ready,
    Reconnecting
}

/// <summary>
/// One authenticated server session: the live transport plus a scope that is cancelled the moment
/// the session ends (fault, close or service shutdown). All sends are serialized through it.
/// </summary>
public sealed class ConnectionSession
{
    private readonly ITransport _transport;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _scope = new();
    private volatile bool _closed;

    public ConnectionSession(string sessionId, ITransport transport, CancellationToken stopToken)
    {
        SessionId = sessionId;
        EstablishedAtUtc = DateTimeOffset.UtcNow;
        _transport = transport;
        StopToken = stopToken;
        stopToken.Register(() => Fault());
    }

    public string SessionId { get; }
    public DateTimeOffset EstablishedAtUtc { get; }
    public CancellationToken StopToken { get; }
    public CancellationToken ClosedToken => _scope.Token;
    public bool IsClosed => _closed;
    /// <summary>Messages successfully sent on this session (diagnostics).</summary>
    public long SentMessages { get; private set; }

    /// <summary>Send raw JSON. Returns false when the session is closed or the send fails (session is then faulted).</summary>
    public async Task<bool> SendAsync(string json)
    {
        if (_closed) return false;
        try
        {
            await _sendLock.WaitAsync(ClosedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        try
        {
            if (_closed) return false;
            await _transport.SendAsync(json, ClosedToken).ConfigureAwait(false);
            SentMessages++;
            return true;
        }
        catch (Exception)
        {
            Fault();
            return false;
        }
        finally
        {
            try { _sendLock.Release(); } catch (ObjectDisposedException) { /* ignore */ }
        }
    }

    /// <summary>Serialize and send a protocol message.</summary>
    public Task<bool> SendAsync(Dictionary<string, object?> message)
        => SendAsync(Protocol.Serialize(message));

    /// <summary>Receive one frame straight from the transport (used by the auth phase).</summary>
    public Task<string?> ReceiveOnce(CancellationToken ct) => _transport.ReceiveAsync(ct);

    /// <summary>Tear the session down; all scope consumers stop.</summary>
    public void Fault()
    {
        if (_closed) return;
        _closed = true;
        try { _scope.Cancel(); } catch { /* ignore */ }
        try { _transport.Abort(); } catch { /* ignore */ }
    }

    internal void Dispose()
    {
        Fault();
        try { _transport.Dispose(); } catch { /* ignore */ }
        _sendLock.Dispose();
        _scope.Dispose();
    }
}

/// <summary>Post-authentication work the engine performs each session (sync, outbox flush, pending fetch).</summary>
public interface ISessionCoordinator
{
    Task OnSessionEstablishedAsync(ConnectionSession session);
}

/// <summary>Agent facts the connection manager needs for HELLO (implemented by the engine).</summary>
public interface ISessionFacts
{
    int CurrentPolicyVersion();
    string? LastSyncIso();
    int PendingResultsCount();
}

/// <summary>Data the heartbeat payload needs (implemented by the engine).</summary>
public interface IHeartbeatInfo
{
    int PolicyVersion { get; }
    string AgentVersion { get; }
    string Hostname { get; }
    string? LocalIp { get; }
}

/// <summary>
/// THE single owner of the server connection. Runs one supervised loop:
/// connect → authenticate → (engine sync) → READY → receive/dispatch, and on any fault:
/// RECONNECTING → exponential backoff → retry. No other component creates connections.
/// </summary>
public sealed class ConnectionManager
{
    private readonly AgentOptions _options;
    private readonly IAgentCredentials _credentials;
    private readonly Func<ITransport> _transportFactory;
    private readonly ISessionCoordinator _coordinator;
    private readonly ISessionFacts _facts;
    private readonly IHeartbeatInfo _heartbeatInfo;
    private readonly IAgentLog _log;
    private readonly BackoffCalculator _backoff;
    private readonly object _stateLock = new();

    private ConnectionSession? _currentSession;
    private volatile ConnectionState _state = ConnectionState.Disconnected;
    private int _heartbeatIntervalSeconds;
    private long _sessionsEstablished;
    private static readonly TimeSpan StableResetWindow = TimeSpan.FromSeconds(60);

    public ConnectionManager(
        AgentOptions options,
        IAgentCredentials credentials,
        Func<ITransport> transportFactory,
        ISessionCoordinator coordinator,
        ISessionFacts facts,
        IHeartbeatInfo heartbeatInfo,
        IAgentLog log)
    {
        _options = options;
        _credentials = credentials;
        _transportFactory = transportFactory;
        _coordinator = coordinator;
        _facts = facts;
        _heartbeatInfo = heartbeatInfo;
        _log = log;
        _backoff = new BackoffCalculator(options.BackoffInitialSeconds, options.BackoffMaxSeconds, options.BackoffMultiplier);
        _heartbeatIntervalSeconds = options.HeartbeatIntervalSeconds;
    }

    public event Action<ConnectionState>? StateChanged;
    public event Action<AgentMessage>? MessageReceived;

    public ConnectionState State
    {
        get { lock (_stateLock) { return _state; } }
        private set { lock (_stateLock) { _state = value; } }
    }

    public long SessionsEstablished => Interlocked.Read(ref _sessionsEstablished);
    public DateTimeOffset? LastHeartbeatUtc { get; private set; }
    public int CurrentHeartbeatIntervalSeconds => _heartbeatIntervalSeconds;
    public int BackoffAttempts => _backoff.AttemptCount;

    /// <summary>
    /// Best-effort send on the current session. Returns false when there is no live session or the
    /// send failed. Durable delivery is layered on top by the result outbox + server redelivery.
    /// </summary>
    public async Task<bool> SendAsync(Dictionary<string, object?> message)
    {
        var session = _currentSession;
        if (session is null || session.IsClosed) return false;
        return await session.SendAsync(message).ConfigureAwait(false);
    }

    /// <summary>Main supervised connection loop. Runs until <paramref name="stopToken"/> fires.</summary>
    public async Task RunAsync(CancellationToken stopToken)
    {
        SetState(ConnectionState.Disconnected);
        _log.Info("connection", "connection manager starting", new { url = _options.ServerWebSocketUrl, device_id = _credentials.DeviceId });

        while (!stopToken.IsCancellationRequested)
        {
            ConnectionSession? session = null;
            ITransport transport = _transportFactory();
            var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
            try
            {
                SetState(ConnectionState.Connecting);
                await transport.ConnectAsync(new Uri(_options.ServerWebSocketUrl), TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds), sessionStop.Token)
                    .ConfigureAwait(false);

                session = new ConnectionSession(Util.Tx.NewUuid(), transport, sessionStop.Token);
                _currentSession = session;

                SetState(ConnectionState.Authenticating);
                bool authenticated = await AuthenticateAsync(session, sessionStop.Token).ConfigureAwait(false);
                if (!authenticated)
                {
                    _log.Warn("connection", "authentication failed; entering backoff", null);
                }
                else
                {
                    SetState(ConnectionState.Connected);
                    Interlocked.Increment(ref _sessionsEstablished);
                    _log.Info("connection", "session established", new
                    {
                        session_id = session.SessionId,
                        server = $"{_options.ServerIp}:{_options.ServerPort}",
                        heartbeat_interval_sec = _heartbeatIntervalSeconds
                    });

                    SetState(ConnectionState.Synchronizing);
                    try
                    {
                        await _coordinator.OnSessionEstablishedAsync(session).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (sessionStop.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Coordination problems must not kill the connection; reconciliation retries later.
                        _log.Error("connection", "session coordination failed (session stays up)", ex.Message, ex);
                    }

                    SetState(ConnectionState.Ready);

                    Task heartbeatTask = RunHeartbeatAsync(session, sessionStop.Token);
                    try
                    {
                        await ReceiveLoopAsync(session, sessionStop.Token).ConfigureAwait(false);
                        _log.Warn("connection", "server closed the connection", new { session_id = session.SessionId });
                    }
                    finally
                    {
                        session.Fault(); // stops the heartbeat loop
                        try { await heartbeatTask.ConfigureAwait(false); } catch { /* ignore */ }
                    }
                }
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warn("connection", "connection failed", new
                {
                    error = ex.Message,
                    exception_type = ex.GetType().Name,
                    server = _options.ServerWebSocketUrl
                });
            }
            finally
            {
                bool wasStable = session is not null && DateTimeOffset.UtcNow - session.EstablishedAtUtc >= StableResetWindow;
                session?.Fault();
                _currentSession = null;
                sessionStop.Cancel();
                session?.Dispose();
                transport.Dispose();
                sessionStop.Dispose();
                if (wasStable)
                {
                    _backoff.Reset();
                    _log.Info("connection", "stable session ended; backoff reset", null);
                }
                SetState(stopToken.IsCancellationRequested ? ConnectionState.Disconnected : ConnectionState.Reconnecting);
            }

            if (stopToken.IsCancellationRequested) break;

            TimeSpan delay = _backoff.Next();
            _log.Info("connection", "reconnect backoff", new { delay_sec = Math.Round(delay.TotalSeconds, 1), attempt = _backoff.AttemptCount });
            try { await Task.Delay(delay, stopToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        SetState(ConnectionState.Disconnected);
        _log.Info("connection", "connection manager stopped", null);
    }

    private async Task<bool> AuthenticateAsync(ConnectionSession session, CancellationToken ct)
    {
        // HELLO with the stored token; fall back to REGISTER (first run, or token rejected/expired).
        if (_credentials.HasToken)
        {
            AuthOutcome outcome = await PerformAuthAsync(session, BuildHello(), MsgType.HELLO_ACK, ct).ConfigureAwait(false);
            if (outcome == AuthOutcome.Acked) return true;
            if (outcome != AuthOutcome.TokenRejected) return false; // socket error / timeout
            _log.Warn("connection", "server rejected device token; re-registering with enrollment key", null);
            _credentials.ClearToken();
            if (session.IsClosed) return false;
        }

        AuthOutcome registerOutcome = await PerformAuthAsync(session, BuildRegister(), MsgType.REGISTER_ACK, ct).ConfigureAwait(false);
        return registerOutcome == AuthOutcome.Acked;
    }

    private enum AuthOutcome { Acked, TokenRejected, Failed }

    private async Task<AuthOutcome> PerformAuthAsync(ConnectionSession session, Dictionary<string, object?> message, string expectAck, CancellationToken ct)
    {
        if (!await session.SendAsync(message).ConfigureAwait(false))
        {
            _log.Error("connection", "failed to send auth message (socket error)", new { type = message["type"] });
            return AuthOutcome.Failed;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.AuthTimeoutSeconds));

        while (!timeoutCts.Token.IsCancellationRequested && !session.IsClosed)
        {
            string? frame;
            try
            {
                frame = await session.ReceiveOnce(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                break; // transport died while waiting for the ack
            }
            if (frame is null) break; // peer closed

            AgentMessage msg;
            try
            {
                msg = Protocol.Parse(frame, _options.MaxMessageSizeBytes);
            }
            catch (ProtocolException ex)
            {
                _log.Warn("connection", "malformed frame during authentication", new { error = ex.Message });
                continue;
            }

            switch (msg.Type)
            {
                case MsgType.REGISTER_ACK when expectAck == MsgType.REGISTER_ACK:
                case MsgType.HELLO_ACK when expectAck == MsgType.HELLO_ACK:
                    HandleAuthAck(msg);
                    return AuthOutcome.Acked;

                case MsgType.ERROR:
                    string? code = msg.GetString("code");
                    _log.Error("connection", "authentication rejected by server", new { code, message = msg.GetString("message") });
                    return code is ErrorCode.AuthExpired or ErrorCode.AuthFailed or ErrorCode.EnrollmentRejected
                        ? AuthOutcome.TokenRejected
                        : AuthOutcome.Failed;

                default:
                    // Nothing but the ack is expected pre-auth; ignore anything else until timeout.
                    _log.Debug("connection", "ignoring pre-auth message", new { type = msg.Type });
                    break;
            }
        }

        _log.Warn("connection", "authentication timed out or connection dropped", new { expect_ack = expectAck });
        return AuthOutcome.Failed;
    }

    private void HandleAuthAck(AgentMessage ack)
    {
        string? token = ack.GetString("device_token");
        if (!string.IsNullOrEmpty(token)) _credentials.SaveToken(token);

        int? hb = ack.GetInt("heartbeat_interval_sec");
        if (hb is >= 5 and <= 300) _heartbeatIntervalSeconds = hb.Value;

        _log.Info("connection", "authenticated", new
        {
            ack_type = ack.Type,
            server_policy_version = ack.GetInt("policy_version"),
            heartbeat_interval_sec = _heartbeatIntervalSeconds
        });
    }

    private Dictionary<string, object?> BuildRegister()
    {
        var sys = Management.SystemInfoCollector.CollectQuick();
        return Protocol.Make(MsgType.REGISTER, _credentials.DeviceId, new Dictionary<string, object?>
        {
            ["hostname"] = sys.Hostname,
            ["os_version"] = sys.OsVersion,
            ["os_build"] = sys.OsBuild,
            ["os_arch"] = sys.OsArch,
            ["agent_version"] = _heartbeatInfo.AgentVersion,
            ["local_ip"] = sys.LocalIp,
            ["all_ips"] = sys.AllIps,
            ["enrollment_key"] = _credentials.EnrollmentKey,
            ["nonce"] = Util.Tx.NewUuid(),
            ["capabilities"] = Capabilities.Default
        });
    }

    private Dictionary<string, object?> BuildHello()
    {
        return Protocol.Make(MsgType.HELLO, _credentials.DeviceId, new Dictionary<string, object?>
        {
            ["device_token"] = _credentials.Token ?? "",
            ["agent_version"] = _heartbeatInfo.AgentVersion,
            ["policy_version"] = _facts.CurrentPolicyVersion(),
            ["last_sync_at"] = _facts.LastSyncIso(),
            ["pending_results_count"] = _facts.PendingResultsCount(),
            ["capabilities"] = Capabilities.Default
        });
    }

    private async Task RunHeartbeatAsync(ConnectionSession session, CancellationToken ct)
    {
        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(5, _heartbeatIntervalSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.ClosedToken);
        try
        {
            while (!linked.Token.IsCancellationRequested)
            {
                // First heartbeat immediately: presence becomes visible the instant we are READY.
                var heartbeat = Protocol.Make(MsgType.HEARTBEAT, _credentials.DeviceId, new Dictionary<string, object?>
                {
                    ["agent_version"] = _heartbeatInfo.AgentVersion,
                    ["policy_version"] = _heartbeatInfo.PolicyVersion,
                    ["state"] = State.ToString().ToUpperInvariant(),
                    ["hostname"] = _heartbeatInfo.Hostname,
                    ["local_ip"] = _heartbeatInfo.LocalIp
                });

                bool sent = await session.SendAsync(heartbeat).ConfigureAwait(false);
                if (!sent)
                {
                    _log.Warn("heartbeat", "heartbeat send failed; connection will be re-established", new { session_id = session.SessionId });
                    session.Fault();
                    return;
                }
                LastHeartbeatUtc = DateTimeOffset.UtcNow;
                _log.Debug("heartbeat", "heartbeat sent", new { policy_version = _heartbeatInfo.PolicyVersion });

                try { await Task.Delay(interval, linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            linked.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(ConnectionSession session, CancellationToken ct)
    {
        var dispatch = Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        var dispatcher = Task.Run(async () =>
        {
            await foreach (var msg in dispatch.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try { MessageReceived?.Invoke(msg); }
                catch (Exception ex) { _log.Error("connection", "message handler crashed", ex.Message, ex); }
            }
        }, CancellationToken.None);

        try
        {
            while (!ct.IsCancellationRequested && !session.IsClosed)
            {
                string? frame = await session.ReceiveOnce(ct).ConfigureAwait(false);
                if (frame is null) return; // clean close by peer

                try
                {
                    var msg = Protocol.Parse(frame, _options.MaxMessageSizeBytes);
                    dispatch.Writer.TryWrite(msg);
                }
                catch (ProtocolException ex)
                {
                    _log.Warn("connection", "protocol violation in inbound frame", new { error = ex.Message, code = ex.Code });
                    await session.SendAsync(Protocol.Make(MsgType.ERROR, _credentials.DeviceId, new Dictionary<string, object?>
                    {
                        ["code"] = ex.Code,
                        ["message"] = ex.Message,
                        ["fatal"] = ex.Code == ErrorCode.MessageTooLarge
                    })).ConfigureAwait(false);
                    if (ex.Code == ErrorCode.MessageTooLarge)
                    {
                        session.Fault();
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stop requested or session faulted — exit quietly
        }
        catch (Exception ex)
        {
            _log.Warn("connection", "receive loop terminated by transport error", new { error = ex.Message });
            session.Fault();
        }
        finally
        {
            dispatch.Writer.TryComplete();
            try { await dispatcher.ConfigureAwait(false); } catch { /* dispatcher must not throw */ }
        }
    }

    private void SetState(ConnectionState value)
    {
        State = value;
        try { StateChanged?.Invoke(value); }
        catch (Exception ex) { _log.Error("connection", "state-changed handler crashed", ex.Message, ex); }
    }
}
