using System.Text.Json;
using LanAgent.Core.Commands;
using LanAgent.Core.Configuration;
using LanAgent.Core.Logging;
using LanAgent.Core.Persistence;
using LanAgent.Core.Protocol;
using Xunit;

namespace LanAgent.Core.Tests;

/// <summary>Scriptable send channel: captures messages and simulates connectivity.</summary>
public sealed class FakeSender
{
    public bool Connected { get; set; } = true;
    public List<Dictionary<string, object?>> Sent { get; } = new();
    private readonly object _lock = new();

    public Task<bool> SendAsync(Dictionary<string, object?> message)
    {
        lock (_lock) Sent.Add(message);
        return Task.FromResult(Connected);
    }

    public List<Dictionary<string, object?>> SentSnapshot()
    {
        lock (_lock) { return Sent.ToList(); }
    }

    public async Task UntilAsync(Func<Dictionary<string, object?>, bool> predicate, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (SentSnapshot().Any(predicate)) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("condition on sent messages not met");
    }
}

public sealed class FakeHandler : ICommandHandler
{
    public string CommandType { get; }
    public int Executions { get; private set; }
    private readonly Func<CommandContext, HandlerResult>? _behavior;

    public FakeHandler(string commandType, Func<CommandContext, HandlerResult>? behavior = null)
    {
        CommandType = commandType;
        _behavior = behavior;
    }

    public Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        Executions++;
        return Task.FromResult(_behavior?.Invoke(context) ?? HandlerResult.Ok(new Dictionary<string, object?> { ["ok"] = true }));
    }
}

public sealed class CommandServiceTests : IDisposable
{
    private readonly AgentOptions _options;
    private readonly SqliteStateStore _store;
    private readonly FakeSender _sender;
    private readonly FakeHandler _handler;
    private readonly CommandService _service;
    private readonly CancellationTokenSource _cts = new();
    private const string DeviceId = "device-test-1";

    public CommandServiceTests()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"lanagent-cmd-{Guid.NewGuid():N}.db");
        _options = new AgentOptions { EnrollmentKey = "test", DataDirectory = Path.GetDirectoryName(dbPath)! };
        _store = new SqliteStateStore(dbPath, NullLog.Instance);
        _sender = new FakeSender();
        _handler = new FakeHandler(CommandType.GetSystemInfo);
        _service = new CommandService(_options, _store, new[] { _handler }, _sender.SendAsync, NullLog.Instance, DeviceId);
        _service.Start(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _service.StopAsync().GetAwaiter().GetResult();
        _store.Dispose();
        _cts.Dispose();
    }

    private static AgentMessage CommandMessage(string commandId, string commandType, string payloadJson = "{}", string deviceId = DeviceId, int attempt = 1)
    {
        var dict = Protocol.Make(MsgType.COMMAND, deviceId, new Dictionary<string, object?>
        {
            ["command_id"] = commandId,
            ["command_type"] = commandType,
            ["payload"] = JsonDocument.Parse(payloadJson).RootElement.Clone(),
            ["created_at"] = Tx.NowIso(),
            ["attempt"] = attempt
        });
        return Protocol.Parse(Protocol.Serialize(dict));
    }

    private static AgentMessage ResultAckMessage(string commandId)
    {
        var dict = Protocol.Make(MsgType.COMMAND_RESULT_ACK, DeviceId, new Dictionary<string, object?>
        {
            ["command_id"] = commandId,
            ["recorded"] = true
        });
        return Protocol.Parse(Protocol.Serialize(dict));
    }

    private static string TypeOf(Dictionary<string, object?> msg) => msg["type"] as string ?? "";

    [Fact]
    public async Task Full_lifecycle_received_running_result()
    {
        await _service.HandleCommandMessageAsync(CommandMessage("c-100", CommandType.GetSystemInfo));

        await _sender.UntilAsync(m => TypeOf(m) == MsgType.COMMAND_RESULT && (m["command_id"] as string) == "c-100");

        var received = _sender.SentSnapshot().Single(m => TypeOf(m) == MsgType.COMMAND_RECEIVED && (m["command_id"] as string) == "c-100");
        Assert.Equal(false, received["duplicate"]);

        var running = _sender.SentSnapshot().Single(m => TypeOf(m) == MsgType.COMMAND_STATUS && (m["command_id"] as string) == "c-100");
        Assert.Equal("RUNNING", running["status"]);

        var result = _sender.SentSnapshot().Single(m => TypeOf(m) == MsgType.COMMAND_RESULT && (m["command_id"] as string) == "c-100");
        Assert.Equal("SUCCESS", result["status"]);
        Assert.Equal(0, result["exit_code"]);
        Assert.Equal(false, result["duplicate"]);

        Assert.Equal(1, _handler.Executions);
        var entry = _store.GetCommand("c-100");
        Assert.Equal(CommandStatus.Success, entry!.Status);

        // Result was sent but not acked yet → still in outbox.
        Assert.Equal(1, _store.OutboxCount());
        _service.HandleResultAck(ResultAckMessage("c-100"));
        Assert.Equal(0, _store.OutboxCount());
    }

    [Fact]
    public async Task Duplicate_command_is_never_executed_twice()
    {
        await _service.HandleCommandMessageAsync(CommandMessage("c-200", CommandType.GetSystemInfo));
        await _sender.UntilAsync(m => TypeOf(m) == MsgType.COMMAND_RESULT && (m["command_id"] as string) == "c-200");

        // Redelivery of the completed command.
        await _service.HandleCommandMessageAsync(CommandMessage("c-200", CommandType.GetSystemInfo, attempt: 2));
        await _sender.UntilAsync(m =>
            TypeOf(m) == MsgType.COMMAND_RESULT && (m["command_id"] as string) == "c-200" && Equals(m["duplicate"], true));

        Assert.Equal(1, _handler.Executions);
        // Only one RUNNING status ever sent.
        Assert.Single(_sender.SentSnapshot().Where(m => TypeOf(m) == MsgType.COMMAND_STATUS && (m["command_id"] as string) == "c-200"));
    }

    [Fact]
    public async Task Command_for_another_device_is_rejected()
    {
        await _service.HandleCommandMessageAsync(CommandMessage("c-300", CommandType.GetSystemInfo, deviceId: "someone-else"));
        await Task.Delay(150);
        Assert.Equal(0, _handler.Executions);
        Assert.Contains(_sender.SentSnapshot(), m => TypeOf(m) == MsgType.ERROR && Equals(m["code"], ErrorCode.WrongDevice));
    }

    [Fact]
    public async Task Result_survives_disconnection_in_outbox_and_flushes_on_reconnect()
    {
        _sender.Connected = false; // link "down" during execution/ack
        await _service.HandleCommandMessageAsync(CommandMessage("c-400", CommandType.GetSystemInfo));

        await Task.Delay(300); // execution completes; result cannot be sent
        Assert.Equal(1, _handler.Executions);
        Assert.Equal(1, _store.OutboxCount());

        _sender.Connected = true; // reconnect
        await _service.FlushOutboxAsync();
        Assert.Contains(_sender.SentSnapshot(), m => TypeOf(m) == MsgType.COMMAND_RESULT && (m["command_id"] as string) == "c-400");

        _service.HandleResultAck(ResultAckMessage("c-400"));
        Assert.Equal(0, _store.OutboxCount());
    }

    [Fact]
    public async Task Interrupted_commands_recover_as_failed_without_rerun()
    {
        // A previous process left c-500 in RUNNING state.
        _store.UpsertCommand(new CommandHistoryEntry
        {
            CommandId = "c-500", CommandType = CommandType.GetSystemInfo, Status = CommandStatus.Running,
            Attempt = 1, ReceivedAt = Tx.NowIso(), StartedAt = Tx.NowIso()
        });

        var recovering = new CommandService(_options, _store, new[] { _handler }, _sender.SendAsync, NullLog.Instance, DeviceId);
        recovering.Start(_cts.Token);

        await _sender.UntilAsync(m => TypeOf(m) == MsgType.COMMAND_RESULT && (m["command_id"] as string) == "c-500");
        var entry = _store.GetCommand("c-500");
        Assert.Equal(CommandStatus.Failed, entry!.Status);
        Assert.Contains("interrupted", entry.Error);
        Assert.Equal(0, _handler.Executions);
        await recovering.StopAsync();
    }

    [Fact]
    public async Task Unknown_command_type_fails_cleanly()
    {
        await _service.HandleCommandMessageAsync(CommandMessage("c-600", "SOME_FUTURE_TYPE"));
        await _sender.UntilAsync(m => TypeOf(m) == MsgType.COMMAND_RESULT && (m["command_id"] as string) == "c-600");
        var result = _sender.SentSnapshot().Single(m => TypeOf(m) == MsgType.COMMAND_RESULT && (m["command_id"] as string) == "c-600");
        Assert.Equal("FAILED", result["status"]);
        Assert.Contains("UNSUPPORTED_TYPE", result["error"] as string);
    }

    [Fact]
    public async Task Get_pending_reports_recent_completed()
    {
        await _service.HandleCommandMessageAsync(CommandMessage("c-700", CommandType.GetSystemInfo));
        await _sender.UntilAsync(m => TypeOf(m) == MsgType.COMMAND_RESULT && (m["command_id"] as string) == "c-700");
        _service.HandleResultAck(ResultAckMessage("c-700"));

        await _service.RequestPendingAsync();
        await _sender.UntilAsync(m => TypeOf(m) == MsgType.GET_PENDING_COMMANDS);
        var request = _sender.SentSnapshot().Single(m => TypeOf(m) == MsgType.GET_PENDING_COMMANDS);
        var known = Assert.IsAssignableFrom<IEnumerable<object>>(request["known_completed"]!);
        Assert.Contains("c-700", known.Cast<string>());
    }
}
