using System.Text.Json;
using LanAgent.Core.Configuration;
using LanAgent.Core.Logging;
using LanAgent.Core.Persistence;
using LanAgent.Core.Protocol;
using LanAgent.Core.Util;

namespace LanAgent.Core.Sync;

public sealed record SyncItem(string Item, string Status, string Detail);

public sealed record SyncOutcome(string Status, int PolicyVersion, List<SyncItem> Items, string? Error);

/// <summary>
/// Desired-state reconciliation (PROTOCOL_CONTRACT §11/§21/§22): SYNC_REQUEST → SYNC_PAYLOAD →
/// apply → verify → SYNC_RESULT. Triggered after boot, after reconnect, on server push, on
/// server-ordered SYNC_REQUEST, and periodically as safety reconciliation.
/// </summary>
public sealed class SynchronizationEngine
{
    private readonly AgentOptions _options;
    private readonly IStateStore _store;
    private readonly IDesiredStateApplier _applier;
    private readonly SendProtocolMessage _send;
    private readonly IAgentLog _log;
    private readonly string _deviceId;

    private TaskCompletionSource<AgentMessage>? _payloadWaiter;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SynchronizationEngine(
        AgentOptions options,
        IStateStore store,
        IDesiredStateApplier applier,
        SendProtocolMessage send,
        IAgentLog log,
        string deviceId)
    {
        _options = options;
        _store = store;
        _applier = applier;
        _send = send;
        _log = log;
        _deviceId = deviceId;
    }

    /// <summary>Feed an inbound SYNC_PAYLOAD to a waiting sync run.</summary>
    public void OnSyncPayload(AgentMessage message)
    {
        var waiter = Interlocked.Exchange(ref _payloadWaiter, null);
        if (waiter is not null)
        {
            waiter.TrySetResult(message);
        }
        else
        {
            _log.Warn("sync", "unsolicited SYNC_PAYLOAD ignored", new { policy_version = message.GetInt("policy_version") });
        }
    }

    /// <summary>Run one full synchronization cycle. Serialized (one at a time).</summary>
    public async Task<SyncOutcome> SynchronizeAsync(string reason, bool force = false)
    {
        bool acquired = false;
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            acquired = true;
        }
        catch
        {
            return Failed(0, "sync gate failure");
        }

        try
        {
            var current = _store.GetPolicyState();
            int currentVersion = current.PolicyVersion;

            await _send(Protocol.Make(MsgType.SYNC_STARTED, _deviceId, new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["current_policy_version"] = currentVersion
            })).ConfigureAwait(false);

            var waiter = new TaskCompletionSource<AgentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var previous = Interlocked.Exchange(ref _payloadWaiter, waiter);
            previous?.TrySetCanceled();

            bool requestSent = await _send(Protocol.Make(MsgType.SYNC_REQUEST, _deviceId, new Dictionary<string, object?>
            {
                ["current_policy_version"] = currentVersion,
                ["reason"] = reason
            })).ConfigureAwait(false);
            if (!requestSent)
                return Failed(currentVersion, "not connected");

            AgentMessage payload;
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.SyncResponseTimeoutSeconds));
                payload = await waiter.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log.Warn("sync", "SYNC_PAYLOAD timed out", new { reason, timeout_sec = _options.SyncResponseTimeoutSeconds });
                return Failed(currentVersion, $"no SYNC_PAYLOAD within {_options.SyncResponseTimeoutSeconds}s");
            }

            int serverVersion = payload.GetInt("policy_version") ?? currentVersion;
            JsonElement? desired = payload.Get("desired_state");
            string? desiredRaw = desired is { ValueKind: JsonValueKind.Object } ? desired.Value.GetRawText() : null;

            string status;
            var items = new List<SyncItem>();

            if (!force && serverVersion == currentVersion && current.LastSyncIso is not null)
            {
                status = "NO_CHANGE";
                _store.SetPolicyState(serverVersion, desiredRaw ?? current.DesiredStateJson, Tx.NowIso());
            }
            else
            {
                _log.Info("sync", "applying desired state", new { from = currentVersion, to = serverVersion, reason, force });
                items = await _applier.ApplyAsync(desired).ConfigureAwait(false);
                bool any = items.Count > 0;
                bool allOk = items.All(i => i.Status == "SUCCESS");
                status = !any ? "NO_CHANGE" : allOk ? "SUCCESS" : "PARTIAL";

                // Only advance the stored policy version when everything applied — a PARTIAL run keeps
                // the old version so periodic reconciliation retries the failed sections.
                if (status == "PARTIAL")
                    _store.SetPolicyState(currentVersion, current.DesiredStateJson, Tx.NowIso());
                else
                    _store.SetPolicyState(serverVersion, desiredRaw, Tx.NowIso());
            }

            string lastSync = Tx.NowIso();
            _log.Info("sync", "synchronization finished", new { status, policy_version = serverVersion, items = items.Count, reason });

            await _send(Protocol.Make(MsgType.SYNC_RESULT, _deviceId, new Dictionary<string, object?>
            {
                ["policy_version"] = serverVersion,
                ["previous_policy_version"] = currentVersion,
                ["status"] = status,
                ["applied"] = items.Select(i => new Dictionary<string, object?>
                {
                    ["item"] = i.Item,
                    ["status"] = i.Status,
                    ["detail"] = i.Detail
                }).ToList(),
                ["error"] = null
            })).ConfigureAwait(false);

            return new SyncOutcome(status, serverVersion, items, null);
        }
        catch (Exception ex)
        {
            _log.Error("sync", "synchronization crashed", ex.Message, ex);
            return Failed(_store.GetPolicyState().PolicyVersion, ex.Message);
        }
        finally
        {
            if (acquired) _gate.Release();
        }
    }

    private SyncOutcome Failed(int version, string error)
    {
        _log.Error("sync", "synchronization failed", new { error, policy_version = version });
        // Best-effort failure report so the server/UI can see it.
        _ = _send(Protocol.Make(MsgType.SYNC_RESULT, _deviceId, new Dictionary<string, object?>
        {
            ["policy_version"] = version,
            ["previous_policy_version"] = version,
            ["status"] = "FAILED",
            ["applied"] = new List<object>(),
            ["error"] = error
        }));
        return new SyncOutcome("FAILED", version, new List<SyncItem>(), error);
    }
}
