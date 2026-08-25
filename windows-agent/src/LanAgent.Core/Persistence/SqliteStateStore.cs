using System.Data;
using Microsoft.Data.Sqlite;
using LanAgent.Core.Logging;

namespace LanAgent.Core.Persistence;

/// <summary>SQLite-backed state store. Single connection guarded by a semaphore (WAL mode).</summary>
public sealed class SqliteStateStore : IStateStore
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IAgentLog _log;
    private bool _disposed;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS meta(
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS command_history(
            command_id      TEXT PRIMARY KEY,
            command_type    TEXT NOT NULL,
            status          TEXT NOT NULL,
            attempt         INTEGER NOT NULL DEFAULT 1,
            received_at     TEXT NOT NULL,
            started_at      TEXT,
            completed_at    TEXT,
            exit_code       INTEGER,
            result_json     TEXT,
            error           TEXT,
            duplicate_count INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS result_outbox(
            command_id      TEXT PRIMARY KEY,
            payload_json    TEXT NOT NULL,
            created_at      TEXT NOT NULL,
            attempts        INTEGER NOT NULL DEFAULT 0,
            last_attempt_at TEXT,
            acked           INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS policy_state(
            id                 INTEGER PRIMARY KEY CHECK (id = 1),
            policy_version     INTEGER NOT NULL DEFAULT 0,
            desired_state_json TEXT,
            last_sync_at       TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_history_completed ON command_history(completed_at);
        """;

    public SqliteStateStore(string databasePath, IAgentLog log)
    {
        _log = log;
        string dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 15
        };
        _conn = new SqliteConnection(builder.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("PRAGMA foreign_keys=ON;");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private async Task<T> WithinGateAsync<T>(Func<Task<T>> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await work().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private Task WithinGateAsync(Func<Task> work) => WithinGateAsync<object?>(async () => { await work(); return null; });

    // ------------------------------------------------------------- meta

    public string? GetMeta(string key)
    {
        return WithinGateAsync<string?>(async () =>
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return result as string;
        }).GetAwaiter().GetResult();
    }

    public void SetMeta(string key, string value)
    {
        WithinGateAsync(async () =>
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO meta(key, value) VALUES(@key, @value) " +
                              "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }).GetAwaiter().GetResult();
    }

    // ------------------------------------------------------ command history

    public CommandHistoryEntry? GetCommand(string commandId)
    {
        return WithinGateAsync<CommandHistoryEntry?>(async () =>
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT command_id, command_type, status, attempt, received_at, started_at, completed_at, " +
                              "exit_code, result_json, error, duplicate_count FROM command_history WHERE command_id = @id";
            cmd.Parameters.AddWithValue("@id", commandId);
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            return await reader.ReadAsync().ConfigureAwait(false) ? ReadEntry(reader) : null;
        }).GetAwaiter().GetResult();
    }

    private static CommandHistoryEntry ReadEntry(SqliteDataReader reader)
    {
        return new CommandHistoryEntry
        {
            CommandId = reader.GetString(0),
            CommandType = reader.GetString(1),
            Status = reader.GetString(2),
            Attempt = reader.GetInt32(3),
            ReceivedAt = reader.GetString(4),
            StartedAt = reader.IsDBNull(5) ? null : reader.GetString(5),
            CompletedAt = reader.IsDBNull(6) ? null : reader.GetString(6),
            ExitCode = reader.IsDBNull(7) ? null : reader.GetInt32(7),
            ResultJson = reader.IsDBNull(8) ? null : reader.GetString(8),
            Error = reader.IsDBNull(9) ? null : reader.GetString(9),
            DuplicateCount = reader.GetInt32(10)
        };
    }

    public void UpsertCommand(CommandHistoryEntry entry)
    {
        WithinGateAsync(async () =>
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO command_history(command_id, command_type, status, attempt, received_at, started_at, completed_at, exit_code, result_json, error, duplicate_count)
                VALUES(@id, @type, @status, @attempt, @received, @started, @completed, @exit, @result, @error, @dups)
                ON CONFLICT(command_id) DO UPDATE SET
                    status = excluded.status,
                    attempt = MAX(command_history.attempt, excluded.attempt),
                    started_at = COALESCE(excluded.started_at, command_history.started_at),
                    completed_at = COALESCE(excluded.completed_at, command_history.completed_at),
                    exit_code = COALESCE(excluded.exit_code, command_history.exit_code),
                    result_json = COALESCE(excluded.result_json, command_history.result_json),
                    error = COALESCE(excluded.error, command_history.error),
                    duplicate_count = command_history.duplicate_count + excluded.duplicate_count
                """;
            cmd.Parameters.AddWithValue("@id", entry.CommandId);
            cmd.Parameters.AddWithValue("@type", entry.CommandType);
            cmd.Parameters.AddWithValue("@status", entry.Status);
            cmd.Parameters.AddWithValue("@attempt", entry.Attempt);
            cmd.Parameters.AddWithValue("@received", entry.ReceivedAt);
            cmd.Parameters.AddWithValue("@started", (object?)entry.StartedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@completed", (object?)entry.CompletedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@exit", (object?)entry.ExitCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@result", (object?)entry.ResultJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@error", (object?)entry.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@dups", entry.DuplicateCount);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }).GetAwaiter().GetResult();
    }

    public List<string> RecentCompletedCommandIds(int count)
    {
        return WithinGateAsync(async () =>
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT command_id FROM command_history " +
                              "WHERE status IN ('SUCCESS','FAILED') AND completed_at IS NOT NULL " +
                              "ORDER BY completed_at DESC LIMIT @n";
            cmd.Parameters.AddWithValue("@n", count);
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            var ids = new List<string>();
            while (await reader.ReadAsync().ConfigureAwait(false)) ids.Add(reader.GetString(0));
            return ids;
        }).GetAwaiter().GetResult();
    }

    public List<CommandHistoryEntry> GetInterruptedCommands()
    {
        return WithinGateAsync(async () =>
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT command_id, command_type, status, attempt, received_at, started_at, completed_at, " +
                              "exit_code, result_json, error, duplicate_count FROM command_history " +
                              "WHERE status IN ('RECEIVED','RUNNING')";
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            var entries = new List<CommandHistoryEntry>();
            while (await reader.ReadAsync().ConfigureAwait(false)) entries.Add(ReadEntry(reader));
            return entries;
        }).GetAwaiter().GetResult();
    }

    public void PruneHistory(int keepCount)
    {
        WithinGateAsync(async () =>
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM command_history WHERE status IN ('SUCCESS','FAILED') AND completed_at IS NOT NULL " +
                              "AND command_id NOT IN (SELECT command_id FROM command_history " +
                              "WHERE status IN ('SUCCESS','FAILED') AND completed_at IS NOT NULL " +
                              "ORDER BY completed_at DESC LIMIT @n)";
            cmd.Parameters.AddWithValue("@n", keepCount);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }).GetAwaiter().GetResult();
    }

    // ------------------------------------------------------------ result outbox

    public void SaveOutboxResult(string commandId, string payloadJson)
    {
        WithinGateAsync(async () =>
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO result_outbox(command_id, payload_json, created_at, attempts, acked)
                VALUES(@id, @payload, @created, 0, 0)
                ON CONFLICT(command_id) DO UPDATE SET payload_json = excluded.payload_json, acked = 0
                """;
            cmd.Parameters.AddWithValue("@id", commandId);
            cmd.Parameters.AddWithValue("@payload", payloadJson);
            cmd.Parameters.AddWithValue("@created", Util.Tx.NowIso());
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }).GetAwaiter().GetResult();
    }

    public List<(string CommandId, string PayloadJson)> GetUnackedResults()
    {
        return WithinGateAsync(async () =>
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT command_id, payload_json FROM result_outbox WHERE acked = 0 ORDER BY created_at";
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            var results = new List<(string, string)>();
            while (await reader.ReadAsync().ConfigureAwait(false))
                results.Add((reader.GetString(0), reader.GetString(1)));
            return results;
        }).GetAwaiter().GetResult();
    }

    public bool HasOutboxResult(string commandId)
    {
        return WithinGateAsync(async () =>
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM result_outbox WHERE command_id = @id AND acked = 0";
            cmd.Parameters.AddWithValue("@id", commandId);
            var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return Convert.ToInt64(result) > 0;
        }).GetAwaiter().GetResult();
    }

    public void MarkOutboxAcked(string commandId)
    {
        WithinGateAsync(async () =>
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM result_outbox WHERE command_id = @id";
            cmd.Parameters.AddWithValue("@id", commandId);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }).GetAwaiter().GetResult();
    }

    public int OutboxCount()
    {
        return WithinGateAsync(async () =>
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM result_outbox WHERE acked = 0";
            var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return Convert.ToInt32(result);
        }).GetAwaiter().GetResult();
    }

    // ------------------------------------------------------------------ policy

    public void SetPolicyState(int policyVersion, string? desiredStateJson, string lastSyncIso)
    {
        WithinGateAsync(async () =>
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO policy_state(id, policy_version, desired_state_json, last_sync_at)
                VALUES(1, @v, @d, @t)
                ON CONFLICT(id) DO UPDATE SET policy_version = excluded.policy_version,
                    desired_state_json = excluded.desired_state_json, last_sync_at = excluded.last_sync_at
                """;
            cmd.Parameters.AddWithValue("@v", policyVersion);
            cmd.Parameters.AddWithValue("@d", (object?)desiredStateJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@t", lastSyncIso);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }).GetAwaiter().GetResult();
    }

    public PolicyStateRow GetPolicyState()
    {
        return WithinGateAsync(async () =>
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT policy_version, desired_state_json, last_sync_at FROM policy_state WHERE id = 1";
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
                return new PolicyStateRow();
            return new PolicyStateRow
            {
                PolicyVersion = reader.GetInt32(0),
                DesiredStateJson = reader.IsDBNull(1) ? null : reader.GetString(1),
                LastSyncIso = reader.IsDBNull(2) ? null : reader.GetString(2)
            };
        }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _conn.Dispose(); } catch { /* ignore */ }
        _gate.Dispose();
    }
}
