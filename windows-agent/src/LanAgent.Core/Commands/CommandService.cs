using System.Text.Json;
using System.Threading.Channels;
using LanAgent.Core.Configuration;
using LanAgent.Core.Logging;
using LanAgent.Core.Persistence;
using LanAgent.Core.Protocol;
using LanAgent.Core.Util;

namespace LanAgent.Core.Commands;

/// <summary>
/// Command engine: acknowledgement (RECEIVED), execution (RUNNING), terminal results
/// (SUCCESS/FAILED), idempotency by command_id, a durable result outbox and crash recovery.
/// The server remains the ultimate source of command state; this layer guarantees the agent side
/// of PROTOCOL_CONTRACT §8.
/// </summary>
public sealed class CommandService
{
    private readonly AgentOptions _options;
    private readonly IStateStore _store;
    private readonly IReadOnlyDictionary<string, ICommandHandler> _handlers;
    private readonly SendProtocolMessage _send;
    private readonly IAgentLog _log;
    private readonly string _deviceId;

    private Channel<CommandDefinition>? _queue;
    private Task? _loop;
    private CancellationTokenSource? _cts;
    private readonly object _runningLock = new();
    private readonly HashSet<string> _running = new();
    private readonly SemaphoreSlim _enqueueGate = new(1, 1);

    public CommandService(
        AgentOptions options,
        IStateStore store,
        IEnumerable<ICommandHandler> handlers,
        SendProtocolMessage send,
        IAgentLog log,
        string deviceId)
    {
        _options = options;
        _store = store;
        _handlers = handlers.ToDictionary(h => h.CommandType, StringComparer.Ordinal);
        _send = send;
        _log = log;
        _deviceId = deviceId;
    }

    public event Action<string>? CommandStarted;
    public event Action<string>? CommandFinished;

    // --------------------------------------------------------------- lifecycle

    /// <summary>Crash recovery + start of the execution workers.</summary>
    public void Start(CancellationToken stopToken)
    {
        RecoverInterruptedCommands();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        _queue = Channel.CreateUnbounded<CommandDefinition>(new UnboundedChannelOptions
        {
            SingleReader = _options.MaxCommandConcurrency == 1,
            SingleWriter = false
        });

        var workers = new List<Task>();
        var queue = _queue;
        var cts = _cts;
        for (int i = 0; i < Math.Max(1, _options.MaxCommandConcurrency); i++)
            workers.Add(Task.Run(() => ConsumeLoopAsync(queue.Reader, cts.Token)));
        _loop = Task.WhenAll(workers);
        _log.Info("commands", "command service started", new { workers = _options.MaxCommandConcurrency, handlers = _handlers.Count });
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _queue?.Writer.TryComplete();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* workers ignore cancellations */ }
        }
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Commands left RECEIVED/RUNNING by a previous process are marked FAILED and reported — they are
    /// never blindly re-executed (the operation may have partially or fully completed before the crash).
    /// The server can re-issue if an administrator chooses to.
    /// </summary>
    private void RecoverInterruptedCommands()
    {
        var interrupted = _store.GetInterruptedCommands();
        foreach (var entry in interrupted)
        {
            var recovered = new CommandHistoryEntry
            {
                CommandId = entry.CommandId,
                CommandType = entry.CommandType,
                Status = CommandStatus.Failed,
                Attempt = entry.Attempt,
                ReceivedAt = entry.ReceivedAt,
                StartedAt = entry.StartedAt,
                CompletedAt = Tx.NowIso(),
                ExitCode = -1,
                ResultJson = null,
                Error = "interrupted by agent restart; not re-executed automatically"
            };
            _store.UpsertCommand(recovered);
            QueueResultForDelivery(BuildResultFields(recovered, entry.CommandType, attempt: entry.Attempt, duplicate: false));
            _log.Warn("commands", "recovered interrupted command as FAILED", new { command_id = entry.CommandId, type = entry.CommandType });
        }
    }

    // ------------------------------------------------------------ inbound COMMAND

    /// <summary>Entry point for an inbound COMMAND message: validate, ack, dedupe, enqueue.</summary>
    public async Task HandleCommandMessageAsync(AgentMessage msg)
    {
        string? commandId = msg.GetString("command_id");
        string? commandType = msg.GetString("command_type");

        if (string.IsNullOrEmpty(commandId) || string.IsNullOrEmpty(commandType))
        {
            await SendErrorAsync(msg, ErrorCode.ValidationError, "COMMAND missing command_id/command_type").ConfigureAwait(false);
            return;
        }

        // One-PC targeting: the message must be addressed to this device (PROTOCOL_CONTRACT §5.12/§19).
        if (!string.Equals(msg.DeviceId, _deviceId, StringComparison.OrdinalIgnoreCase))
        {
            _log.Error("commands", "command addressed to another device — ignored", new
            {
                command_id = commandId, addressed_to = msg.DeviceId, me = _deviceId
            });
            await SendErrorAsync(msg, ErrorCode.WrongDevice, $"command targets device '{msg.DeviceId}' but this agent is '{_deviceId}'").ConfigureAwait(false);
            return;
        }

        int attempt = msg.GetInt("attempt") ?? 1;
        var payload = msg.Get("payload");
        int? timeout = msg.GetInt("timeout_sec");
        bool requiresResult = msg.GetBool("requires_result", true);

        await _enqueueGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var existing = _store.GetCommand(commandId);

            // ---- duplicate / idempotency protection (PROTOCOL_CONTRACT §8.3) ----
            if (existing is not null)
            {
                bool duplicateRunning = existing.Status is CommandStatus.Received or CommandStatus.Running;
                if (CommandStatus.IsTerminal(existing.Status))
                {
                    _log.Info("commands", "duplicate of completed command — replaying stored result, NOT executing", new
                    {
                        command_id = commandId, type = existing.CommandType, status = existing.Status
                    });
                    _store.UpsertCommand(new CommandHistoryEntry
                    {
                        CommandId = existing.CommandId,
                        CommandType = existing.CommandType,
                        Status = existing.Status,
                        Attempt = Math.Max(existing.Attempt, attempt),
                        ReceivedAt = existing.ReceivedAt,
                        StartedAt = existing.StartedAt,
                        CompletedAt = existing.CompletedAt,
                        ExitCode = existing.ExitCode,
                        ResultJson = existing.ResultJson,
                        Error = existing.Error,
                        DuplicateCount = 1
                    });
                    await SendReceivedAsync(commandId, duplicate: true).ConfigureAwait(false);
                    QueueResultForDelivery(BuildResultFields(existing, existing.CommandType, attempt, duplicate: true));
                    await FlushOutboxAsync().ConfigureAwait(false);
                    return;
                }
                if (duplicateRunning)
                {
                    _log.Info("commands", "redelivery of in-flight command ignored", new { command_id = commandId, status = existing.Status });
                    await SendReceivedAsync(commandId, duplicate: true).ConfigureAwait(false);
                    return;
                }
            }

            // First delivery: persist RECEIVED, acknowledge, enqueue.
            var entry = new CommandHistoryEntry
            {
                CommandId = commandId,
                CommandType = commandType,
                Status = CommandStatus.Received,
                Attempt = attempt,
                ReceivedAt = Tx.NowIso()
            };
            _store.UpsertCommand(entry);
            _log.Info("commands", "command received", new { command_id = commandId, command_type = commandType, attempt });

            await SendReceivedAsync(commandId, duplicate: false).ConfigureAwait(false);

            var definition = new CommandDefinition
            {
                CommandId = commandId,
                CommandType = commandType,
                Attempt = attempt,
                CreatedAt = msg.GetString("created_at") ?? Tx.NowIso(),
                Payload = payload,
                TimeoutSeconds = timeout,
                RequiresResult = requiresResult
            };
            _queue?.Writer.TryWrite(definition);
        }
        finally
        {
            _enqueueGate.Release();
        }
    }

    // ------------------------------------------------------------- execution

    private async Task ConsumeLoopAsync(ChannelReader<CommandDefinition> reader, CancellationToken ct)
    {
        await foreach (var command in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await ExecuteAsync(command, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error("commands", "command execution crashed", ex.Message, ex);
                try
                {
                    PersistAndDeliver(command, HandlerResult.Fail($"internal agent error: {ex.Message}"), startedIso: null);
                }
                catch { /* never crash the worker */ }
            }
        }
    }

    private async Task ExecuteAsync(CommandDefinition command, CancellationToken ct)
    {
        string startedIso = Tx.NowIso();
        var started = DateTimeOffset.UtcNow;

        lock (_runningLock) { _running.Add(command.CommandId); }
        CommandStarted?.Invoke(command.CommandId);
        try
        {
            // RECEIVED -> RUNNING (PROTOCOL_CONTRACT §5.14/§11)
            var existing = _store.GetCommand(command.CommandId);
            var running = new CommandHistoryEntry
            {
                CommandId = command.CommandId,
                CommandType = command.CommandType,
                Status = CommandStatus.Running,
                Attempt = command.Attempt,
                ReceivedAt = existing?.ReceivedAt ?? startedIso,
                StartedAt = startedIso
            };
            _store.UpsertCommand(running);
            bool statusSent = await _send(Protocol.Make(MsgType.COMMAND_STATUS, _deviceId, new Dictionary<string, object?>
            {
                ["command_id"] = command.CommandId,
                ["status"] = CommandStatus.Running,
                ["started_at"] = startedIso
            })).ConfigureAwait(false);
            _log.Info("commands", "command running", new { command_id = command.CommandId, command_type = command.CommandType, status_sent = statusSent });

            HandlerResult outcome;
            if (!_handlers.TryGetValue(command.CommandType, out var handler))
            {
                outcome = HandlerResult.Fail($"UNSUPPORTED_TYPE: no handler for '{command.CommandType}'");
            }
            else
            {
                int timeoutSeconds = command.TimeoutSeconds ?? _options.CommandTimeoutSeconds;
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 7200)));
                try
                {
                    outcome = await handler.ExecuteAsync(new CommandContext
                    {
                        Command = command,
                        CancellationToken = timeoutCts.Token
                    }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    outcome = HandlerResult.Fail($"command timed out after {timeoutSeconds}s");
                }
            }

            var duration = DateTimeOffset.UtcNow - started;
            var entry = new CommandHistoryEntry
            {
                CommandId = command.CommandId,
                CommandType = command.CommandType,
                Status = string.IsNullOrEmpty(outcome.Error) && outcome.ExitCode == 0 ? CommandStatus.Success : CommandStatus.Failed,
                Attempt = command.Attempt,
                ReceivedAt = running.ReceivedAt,
                StartedAt = startedIso,
                CompletedAt = Tx.NowIso(),
                ExitCode = outcome.ExitCode,
                ResultJson = outcome.Result is null ? null : AgentJson.Serialize(outcome.Result),
                Error = outcome.Error
            };
            _store.UpsertCommand(entry);
            _store.PruneHistory(5000);

            if (command.RequiresResult)
            {
                QueueResultForDelivery(BuildResultFields(entry, command.CommandType, command.Attempt, duplicate: false));
                await FlushOutboxAsync().ConfigureAwait(false);
            }

            _log.Info("commands", "command finished", new
            {
                command_id = command.CommandId,
                command_type = command.CommandType,
                status = entry.Status,
                exit_code = entry.ExitCode,
                duration_ms = duration.TotalMilliseconds,
                error = entry.Error
            });
        }
        finally
        {
            lock (_runningLock) { _running.Remove(command.CommandId); }
            CommandFinished?.Invoke(command.CommandId);
        }
    }

    private void PersistAndDeliver(CommandDefinition command, HandlerResult outcome, string? startedIso)
    {
        var entry = new CommandHistoryEntry
        {
            CommandId = command.CommandId,
            CommandType = command.CommandType,
            Status = string.IsNullOrEmpty(outcome.Error) && outcome.ExitCode == 0 ? CommandStatus.Success : CommandStatus.Failed,
            Attempt = command.Attempt,
            ReceivedAt = startedIso ?? Tx.NowIso(),
            StartedAt = startedIso,
            CompletedAt = Tx.NowIso(),
            ExitCode = outcome.ExitCode,
            ResultJson = outcome.Result is null ? null : AgentJson.Serialize(outcome.Result),
            Error = outcome.Error
        };
        _store.UpsertCommand(entry);
        if (command.RequiresResult)
        {
            QueueResultForDelivery(BuildResultFields(entry, command.CommandType, command.Attempt, duplicate: false));
            _ = FlushOutboxAsync();
        }
    }

    // ------------------------------------------------------------ result outbox

    /// <summary>Result fields persisted durably BEFORE any send attempt (PROTOCOL_CONTRACT §8.4).</summary>
    private void QueueResultForDelivery(Dictionary<string, object?> resultFields)
    {
        string payloadJson = AgentJson.Serialize(resultFields);
        string? commandId = null;
        if (resultFields.TryGetValue("command_id", out var id) && id is string s) commandId = s;
        if (commandId is null) return;
        _store.SaveOutboxResult(commandId, payloadJson);
    }

    /// <summary>Send every un-acked result. Stops on first failure (retry happens after reconnect).</summary>
    public async Task FlushOutboxAsync()
    {
        var pending = _store.GetUnackedResults();
        foreach (var (commandId, payloadJson) in pending)
        {
            Dictionary<string, object?> fields;
            try
            {
                fields = AgentJson.Deserialize<Dictionary<string, object?>>(payloadJson);
            }
            catch (Exception ex)
            {
                _log.Error("commands", "outbox entry unreadable — dropping", new { command_id = commandId, error = ex.Message });
                _store.MarkOutboxAcked(commandId); // corrupted entry: remove rather than loop forever
                continue;
            }

            bool sent = await _send(Protocol.Make(MsgType.COMMAND_RESULT, _deviceId, fields)).ConfigureAwait(false);
            if (!sent)
            {
                _log.Info("commands", "outbox flush paused (no connection); will retry after reconnect", new { command_id, remaining = pending.Count });
                return;
            }
            _log.Info("commands", "COMMAND_RESULT sent", new { command_id = fields.TryGetValue("command_id", out var v) ? v : commandId, status = fields.TryGetValue("status", out var st) ? st : "?" });
        }
    }

    /// <summary>COMMAND_RESULT_ACK: the server durably recorded the result — drop the outbox copy.</summary>
    public void HandleResultAck(AgentMessage msg)
    {
        string? commandId = msg.GetString("command_id");
        if (string.IsNullOrEmpty(commandId)) return;
        bool recorded = msg.GetBool("recorded", true);
        if (recorded)
        {
            _store.MarkOutboxAcked(commandId);
            _log.Info("commands", "result acked by server", new { command_id = commandId });
        }
    }

    /// <summary>Ask the server to redeliver anything not terminal yet (PROTOCOL_CONTRACT §5.17).</summary>
    public async Task RequestPendingAsync()
    {
        var known = _store.RecentCompletedCommandIds(200);
        bool sent = await _send(Protocol.Make(MsgType.GET_PENDING_COMMANDS, _deviceId, new Dictionary<string, object?>
        {
            ["known_completed"] = known
        })).ConfigureAwait(false);
        _log.Info("commands", "requested pending commands", new { sent, known_completed = known.Count });
    }

    public List<string> RunningCommandIds()
    {
        lock (_runningLock) { return _running.ToList(); }
    }

    public int PendingResults() => _store.OutboxCount();

    // ---------------------------------------------------------------- helpers

    private static Dictionary<string, object?> BuildResultFields(CommandHistoryEntry entry, string commandType, int attempt, bool duplicate)
    {
        return new Dictionary<string, object?>
        {
            ["command_id"] = entry.CommandId,
            ["status"] = entry.Status,
            ["exit_code"] = entry.ExitCode ?? -1,
            ["result"] = ParseResultJson(entry.ResultJson),
            ["error"] = entry.Error,
            ["started_at"] = entry.StartedAt,
            ["completed_at"] = entry.CompletedAt,
            ["duration_ms"] = DurationMs(entry),
            ["attempt"] = attempt,
            ["duplicate"] = duplicate
        };
    }

    private static object? ParseResultJson(string? json)
    {
        if (json is null) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static long? DurationMs(CommandHistoryEntry entry)
    {
        if (DateTimeOffset.TryParse(entry.StartedAt, out var start) &&
            DateTimeOffset.TryParse(entry.CompletedAt, out var end) && end >= start)
            return (long)(end - start).TotalMilliseconds;
        return null;
    }

    private Task SendReceivedAsync(string commandId, bool duplicate)
        => _send(Protocol.Make(MsgType.COMMAND_RECEIVED, _deviceId, new Dictionary<string, object?>
        {
            ["command_id"] = commandId,
            ["received_at"] = Tx.NowIso(),
            ["duplicate"] = duplicate
        }));

    private async Task SendErrorAsync(AgentMessage msg, string code, string message)
    {
        var fields = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = message,
            ["msg_id_ref"] = msg.MsgId
        };
        if (msg.GetString("command_id") is string cid) fields["command_id"] = cid;
        await _send(Protocol.Make(MsgType.ERROR, _deviceId, fields)).ConfigureAwait(false);
    }
}
