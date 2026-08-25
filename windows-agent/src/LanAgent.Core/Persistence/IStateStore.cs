namespace LanAgent.Core.Persistence;

/// <summary>Lifecycle status of a command as tracked in local history (mirrors server-side states).</summary>
public static class CommandStatus
{
    public const string Received = "RECEIVED";
    public const string Running = "RUNNING";
    public const string Success = "SUCCESS";
    public const string Failed = "FAILED";

    public static bool IsTerminal(string status) => status is Success or Failed;
}

/// <summary>A row of local command history — the durable basis for idempotency and crash recovery.</summary>
public sealed class CommandHistoryEntry
{
    public required string CommandId { get; set; }
    public required string CommandType { get; set; }
    public required string Status { get; set; }
    public int Attempt { get; set; }
    public required string ReceivedAt { get; set; }
    public string? StartedAt { get; set; }
    public string? CompletedAt { get; set; }
    public int? ExitCode { get; set; }
    /// <summary>Serialized "result" object of the final COMMAND_RESULT (JSON object or null).</summary>
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
    public int DuplicateCount { get; set; }
}

/// <summary>
/// Durable local state: device metadata, command history (idempotency), result outbox
/// (results owed to the server) and applied policy state. Backed by SQLite (WAL).
/// </summary>
public interface IStateStore : IDisposable
{
    // ---- meta (device id, token, misc) ----
    string? GetMeta(string key);
    void SetMeta(string key, string value);

    // ---- command history ----
    CommandHistoryEntry? GetCommand(string commandId);
    void UpsertCommand(CommandHistoryEntry entry);
    /// <summary>Most recent N terminal command ids (for GET_PENDING_COMMANDS.known_completed).</summary>
    List<string> RecentCompletedCommandIds(int count);
    /// <summary>History rows left in RUNNING/RECEIVED state by a previous process (crash recovery).</summary>
    List<CommandHistoryEntry> GetInterruptedCommands();
    /// <summary>Keep history bounded: prune oldest terminal rows beyond keepCount.</summary>
    void PruneHistory(int keepCount);

    // ---- result outbox ----
    void SaveOutboxResult(string commandId, string payloadJson);
    List<(string CommandId, string PayloadJson)> GetUnackedResults();
    bool HasOutboxResult(string commandId);
    void MarkOutboxAcked(string commandId);
    int OutboxCount();

    // ---- policy ----
    void SetPolicyState(int policyVersion, string? desiredStateJson, string lastSyncIso);
    PolicyStateRow GetPolicyState();
}

public sealed class PolicyStateRow
{
    public int PolicyVersion { get; set; }
    public string? DesiredStateJson { get; set; }
    public string? LastSyncIso { get; set; }
}
