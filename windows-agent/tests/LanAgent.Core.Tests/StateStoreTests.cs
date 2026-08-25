using LanAgent.Core.Logging;
using LanAgent.Core.Persistence;
using LanAgent.Core.Util;
using Xunit;

namespace LanAgent.Core.Tests;

public class StateStoreTests : IDisposable
{
    private readonly SqliteStateStore _store;

    public StateStoreTests()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lanagent-test-{Guid.NewGuid():N}.db");
        _store = new SqliteStateStore(path, NullLog.Instance);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void Meta_roundtrips()
    {
        Assert.Null(_store.GetMeta("device_id"));
        _store.SetMeta("device_id", "550e8400-e29b-41d4-a716-446655440000");
        Assert.Equal("550e8400-e29b-41d4-a716-446655440000", _store.GetMeta("device_id"));
        _store.SetMeta("device_id", "updated");
        Assert.Equal("updated", _store.GetMeta("device_id"));
    }

    [Fact]
    public void Command_history_lifecycle_and_idempotent_lookup()
    {
        var entry = new CommandHistoryEntry
        {
            CommandId = "c-1", CommandType = "GET_SYSTEM_INFO", Status = CommandStatus.Received,
            Attempt = 1, ReceivedAt = Tx.NowIso()
        };
        _store.UpsertCommand(entry);

        var stored = _store.GetCommand("c-1");
        Assert.NotNull(stored);
        Assert.Equal(CommandStatus.Received, stored!.Status);

        entry.Status = CommandStatus.Success;
        entry.ExitCode = 0;
        entry.ResultJson = """{"hostname":"PC-1"}""";
        entry.CompletedAt = Tx.NowIso();
        _store.UpsertCommand(entry);

        stored = _store.GetCommand("c-1");
        Assert.Equal(CommandStatus.Success, stored!.Status);
        Assert.Equal(0, stored.ExitCode);
        Assert.Contains("PC-1", stored.ResultJson);
        Assert.True(CommandStatus.IsTerminal(stored.Status));
    }

    [Fact]
    public void Outbox_survives_until_acked()
    {
        _store.SaveOutboxResult("c-9", """{"command_id":"c-9","status":"SUCCESS"}""");
        Assert.Equal(1, _store.OutboxCount());
        Assert.True(_store.HasOutboxResult("c-9"));

        var pending = _store.GetUnackedResults();
        Assert.Single(pending);
        Assert.Equal("c-9", pending[0].CommandId);

        _store.MarkOutboxAcked("c-9");
        Assert.Equal(0, _store.OutboxCount());
        Assert.Empty(_store.GetUnackedResults());
    }

    [Fact]
    public void Interrupted_commands_are_detected()
    {
        _store.UpsertCommand(new CommandHistoryEntry
        {
            CommandId = "c-run", CommandType = "INSTALL_APP", Status = CommandStatus.Running,
            Attempt = 1, ReceivedAt = Tx.NowIso()
        });
        _store.UpsertCommand(new CommandHistoryEntry
        {
            CommandId = "c-done", CommandType = "GET_SYSTEM_INFO", Status = CommandStatus.Success,
            Attempt = 1, ReceivedAt = Tx.NowIso(), CompletedAt = Tx.NowIso(), ExitCode = 0
        });

        var interrupted = _store.GetInterruptedCommands();
        Assert.Single(interrupted);
        Assert.Equal("c-run", interrupted[0].CommandId);
    }

    [Fact]
    public void RecentCompleted_orders_by_completion_desc()
    {
        for (int i = 0; i < 5; i++)
        {
            _store.UpsertCommand(new CommandHistoryEntry
            {
                CommandId = $"c-{i}", CommandType = "T", Status = CommandStatus.Success,
                Attempt = 1, ReceivedAt = Tx.NowIso(),
                CompletedAt = DateTimeOffset.Now.AddSeconds(i).ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
                ExitCode = 0
            });
        }
        var recent = _store.RecentCompletedCommandIds(3);
        Assert.Equal(3, recent.Count);
        Assert.Equal("c-4", recent[0]);
    }

    [Fact]
    public void Prune_bounds_history()
    {
        for (int i = 0; i < 20; i++)
        {
            _store.UpsertCommand(new CommandHistoryEntry
            {
                CommandId = $"c-{i}", CommandType = "T", Status = CommandStatus.Success,
                Attempt = 1, ReceivedAt = Tx.NowIso(),
                CompletedAt = DateTimeOffset.Now.AddSeconds(i).ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
                ExitCode = 0
            });
        }
        _store.PruneHistory(5);
        var recent = _store.RecentCompletedCommandIds(100);
        Assert.Equal(5, recent.Count);
    }

    [Fact]
    public void Policy_state_roundtrips()
    {
        var initial = _store.GetPolicyState();
        Assert.Equal(0, initial.PolicyVersion);

        _store.SetPolicyState(51, """{"dns":{"enabled":true}}""", Tx.NowIso());
        var policy = _store.GetPolicyState();
        Assert.Equal(51, policy.PolicyVersion);
        Assert.NotNull(policy.DesiredStateJson);
        Assert.NotNull(policy.LastSyncIso);
    }
}
