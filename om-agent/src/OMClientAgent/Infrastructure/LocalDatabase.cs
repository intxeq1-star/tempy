using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OMClientAgent.Core.Models;

namespace OMClientAgent.Infrastructure;

public sealed class LocalDatabase : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<LocalDatabase> _logger;
    private readonly object _lock = new();

    public LocalDatabase(string databaseFile, ILogger<LocalDatabase> logger)
    {
        _logger = logger;
        var dir = Path.GetDirectoryName(databaseFile);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databaseFile, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        Cursor = SyncState.Default;
        Initialize();
    }

    public SyncState Cursor { get; private set; }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private void Initialize()
    {
        using var c = Open();
        string[] ddl =
        {
            "CREATE TABLE IF NOT EXISTS Settings (Key TEXT PRIMARY KEY, Value TEXT)",
            "CREATE TABLE IF NOT EXISTS MachineIdentity (MachineId TEXT PRIMARY KEY, ComputerName TEXT, OsVersion TEXT, Architecture TEXT, DomainOrWorkgroup TEXT, AdvertisedIp TEXT, AgentVersion TEXT, EnrolledAtUtc TEXT)",
            "CREATE TABLE IF NOT EXISTS Jobs (JobId TEXT PRIMARY KEY, TargetMachineId TEXT, Type TEXT, ExecutionMode TEXT, Priority TEXT, Command TEXT, Action TEXT, Arguments TEXT, PackageId TEXT, PolicyId TEXT, Sha256 TEXT, DownloadUrl TEXT, TimeoutSeconds INTEGER, CreatedAtUtc TEXT, CreatedBy TEXT, ProtocolVersion TEXT, Status TEXT, ExitCode INTEGER, StartedAtUtc TEXT, CompletedAtUtc TEXT, StdOut TEXT, StdErr TEXT)",
            "CREATE TABLE IF NOT EXISTS Policies (PolicyId TEXT, Version INTEGER, Application TEXT, Action TEXT, Target TEXT, CreatedAtUtc TEXT, CreatedBy TEXT, PRIMARY KEY (PolicyId, Version))",
            "CREATE TABLE IF NOT EXISTS SyncState (Id INTEGER PRIMARY KEY CHECK (Id = 1), LastServerRevision INTEGER, LastUploadedResultRevision INTEGER, LastSyncAtUtc TEXT)"
        };
        foreach (var sql in ddl)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        using var upsert = c.CreateCommand();
        upsert.CommandText = "INSERT OR IGNORE INTO SyncState (Id, LastServerRevision, LastUploadedResultRevision, LastSyncAtUtc) VALUES (1,0,0,NULL)";
        upsert.ExecuteNonQuery();
    }

    public void SetSetting(string key, string value)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO Settings (Key, Value) VALUES ($k,$v) ON CONFLICT(Key) DO UPDATE SET Value=$v";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    public string? GetSetting(string key)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Settings WHERE Key=$k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SaveMachineIdentity(Core.Models.MachineIdentity id)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO MachineIdentity (MachineId, ComputerName, OsVersion, Architecture, DomainOrWorkgroup, AdvertisedIp, AgentVersion, EnrolledAtUtc) VALUES ($m,$cn,$os,$arch,$d,$ip,$ver,$e) ON CONFLICT(MachineId) DO UPDATE SET ComputerName=$cn, OsVersion=$os, Architecture=$arch, DomainOrWorkgroup=$d, AdvertisedIp=$ip, AgentVersion=$ver";
            cmd.Parameters.AddWithValue("$m", id.MachineId);
            cmd.Parameters.AddWithValue("$cn", id.ComputerName);
            cmd.Parameters.AddWithValue("$os", id.OsVersion ?? "");
            cmd.Parameters.AddWithValue("$arch", id.Architecture ?? "");
            cmd.Parameters.AddWithValue("$d", id.DomainOrWorkgroup ?? "");
            cmd.Parameters.AddWithValue("$ip", id.AdvertisedIp ?? "");
            cmd.Parameters.AddWithValue("$ver", id.AgentVersion);
            cmd.Parameters.AddWithValue("$e", id.EnrolledAtUtc.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    public Core.Models.MachineIdentity? LoadMachineIdentity()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MachineId, ComputerName, OsVersion, Architecture, DomainOrWorkgroup, AdvertisedIp, AgentVersion, EnrolledAtUtc FROM MachineIdentity LIMIT 1";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Core.Models.MachineIdentity
        {
            MachineId = r.GetString(0),
            ComputerName = r.GetString(1),
            OsVersion = r.IsDBNull(2) ? null : r.GetString(2),
            Architecture = r.IsDBNull(3) ? null : r.GetString(3),
            DomainOrWorkgroup = r.IsDBNull(4) ? null : r.GetString(4),
            AdvertisedIp = r.IsDBNull(5) ? null : r.GetString(5),
            AgentVersion = r.IsDBNull(6) ? "" : r.GetString(6),
            EnrolledAtUtc = DateTime.TryParse(r.IsDBNull(7) ? "" : r.GetString(7), out var d) ? d : DateTime.MinValue
        };
    }

    public void UpsertJob(Job job)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO Jobs (JobId, TargetMachineId, Type, ExecutionMode, Priority, Command, Action, Arguments, PackageId, PolicyId, Sha256, DownloadUrl, TimeoutSeconds, CreatedAtUtc, CreatedBy, ProtocolVersion, Status) VALUES ($id,$tm,$type,$mode,$pri,$cmd,$action,$args,$pkg,$pol,$sha,$url,$timeout,$create,$by,$proto,$status) ON CONFLICT(JobId) DO UPDATE SET Status=$status";
            cmd.Parameters.AddWithValue("$id", job.JobId);
            cmd.Parameters.AddWithValue("$tm", job.TargetMachineId);
            cmd.Parameters.AddWithValue("$type", job.Type.ToString());
            cmd.Parameters.AddWithValue("$mode", job.ExecutionMode.ToString());
            cmd.Parameters.AddWithValue("$pri", job.Priority.ToString());
            cmd.Parameters.AddWithValue("$cmd", job.Command ?? "");
            cmd.Parameters.AddWithValue("$action", job.Action ?? "");
            cmd.Parameters.AddWithValue("$args", job.Arguments ?? "");
            cmd.Parameters.AddWithValue("$pkg", job.PackageId ?? "");
            cmd.Parameters.AddWithValue("$pol", job.PolicyId ?? "");
            cmd.Parameters.AddWithValue("$sha", job.Sha256 ?? "");
            cmd.Parameters.AddWithValue("$url", job.DownloadUrl ?? "");
            cmd.Parameters.AddWithValue("$timeout", job.TimeoutSeconds);
            cmd.Parameters.AddWithValue("$create", job.CreatedAtUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$by", job.CreatedBy ?? "");
            cmd.Parameters.AddWithValue("$proto", job.ProtocolVersion);
            cmd.Parameters.AddWithValue("$status", JobStatus.Pending.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    public List<Job> GetPendingJobs()
    {
        var list = new List<Job>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT JobId, TargetMachineId, Type, ExecutionMode, Priority, Command, Action, Arguments, PackageId, PolicyId, Sha256, DownloadUrl, TimeoutSeconds, CreatedAtUtc, CreatedBy, ProtocolVersion, Status FROM Jobs WHERE Status IN ('Pending','Retrying','WaitingForUser') ORDER BY CASE Status WHEN 'WaitingForUser' THEN 1 ELSE 0 END, CASE Priority WHEN 'Critical' THEN 0 WHEN 'High' THEN 1 WHEN 'Normal' THEN 2 ELSE 3 END, CreatedAtUtc";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapJob(r));
        return list;
    }

    public Job? GetJob(string jobId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT JobId, TargetMachineId, Type, ExecutionMode, Priority, Command, Action, Arguments, PackageId, PolicyId, Sha256, DownloadUrl, TimeoutSeconds, CreatedAtUtc, CreatedBy, ProtocolVersion, Status FROM Jobs WHERE JobId=$id";
        cmd.Parameters.AddWithValue("$id", jobId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapJob(r) : null;
    }

    public bool HasCompletedJob(string jobId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM Jobs WHERE JobId=$id AND Status IN ('Success','Failed','Timeout','Cancelled')";
        cmd.Parameters.AddWithValue("$id", jobId);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public Job? GetCompletedJobResult(string jobId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT JobId, TargetMachineId, Type, ExecutionMode, Priority, Command, Action, Arguments, PackageId, PolicyId, Sha256, DownloadUrl, TimeoutSeconds, CreatedAtUtc, CreatedBy, ProtocolVersion, Status, ExitCode, StartedAtUtc, CompletedAtUtc, StdOut, StdErr FROM Jobs WHERE JobId=$id AND Status IN ('Success','Failed','Timeout','Cancelled')";
        cmd.Parameters.AddWithValue("$id", jobId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapJob(r) : null;
    }

    public void UpdateJobStatus(string jobId, JobStatus status, int? exitCode = null, string? stdout = null, string? stderr = null, DateTime? startedAtUtc = null, DateTime? completedAtUtc = null)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE Jobs SET Status=$st, ExitCode=COALESCE($exit, ExitCode), StdOut=COALESCE($so, StdOut), StdErr=COALESCE($se, StdErr), StartedAtUtc=COALESCE($s, StartedAtUtc), CompletedAtUtc=COALESCE($co, CompletedAtUtc) WHERE JobId=$id";
            cmd.Parameters.AddWithValue("$st", status.ToString());
            cmd.Parameters.AddWithValue("$exit", exitCode.HasValue ? exitCode.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$so", stdout ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$se", stderr ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$s", startedAtUtc.HasValue ? startedAtUtc.Value.ToString("o") : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$co", completedAtUtc.HasValue ? completedAtUtc.Value.ToString("o") : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$id", jobId);
            cmd.ExecuteNonQuery();
        }
    }

    private static Job MapJob(SqliteDataReader r) => new()
    {
        JobId = r.GetString(0),
        TargetMachineId = r.GetString(1),
        Type = Enum.TryParse<JobType>(r.GetString(2), out var t) ? t : JobType.Command,
        ExecutionMode = Enum.TryParse<JobExecutionMode>(r.GetString(3), out var e) ? e : JobExecutionMode.Admin,
        Priority = Enum.TryParse<JobPriority>(r.GetString(4), out var p) ? p : JobPriority.Normal,
        Command = r.IsDBNull(5) ? null : r.GetString(5),
        Action = r.IsDBNull(6) ? null : r.GetString(6),
        Arguments = r.IsDBNull(7) ? null : r.GetString(7),
        PackageId = r.IsDBNull(8) ? null : r.GetString(8),
        PolicyId = r.IsDBNull(9) ? null : r.GetString(9),
        Sha256 = r.IsDBNull(10) ? null : r.GetString(10),
        DownloadUrl = r.IsDBNull(11) ? null : r.GetString(11),
        TimeoutSeconds = r.GetInt32(12),
        CreatedAtUtc = DateTime.TryParse(r.IsDBNull(13) ? "" : r.GetString(13), out var c) ? c : DateTime.UtcNow,
        CreatedBy = r.IsDBNull(14) ? null : r.GetString(14),
        ProtocolVersion = r.IsDBNull(15) ? "" : r.GetString(15),
        Status = Enum.TryParse<JobStatus>(r.GetString(16), out var s) ? s : JobStatus.Pending,
        ExitCode = r.GetInt32(17),
        StartedAtUtc = DateTime.TryParse(r.IsDBNull(18) ? "" : r.GetString(18), out var st) ? st : null,
        CompletedAtUtc = DateTime.TryParse(r.IsDBNull(19) ? "" : r.GetString(19), out var co) ? co : null,
        StandardOutput = r.IsDBNull(20) ? null : r.GetString(20),
        StandardError = r.IsDBNull(21) ? null : r.GetString(21),
    };

    public List<Job> GetJobsCompletedSince(DateTime sinceUtc)
    {
        var list = new List<Job>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT JobId, TargetMachineId, Type, ExecutionMode, Priority, Command, Action, Arguments, PackageId, PolicyId, Sha256, DownloadUrl, TimeoutSeconds, CreatedAtUtc, CreatedBy, ProtocolVersion, Status, ExitCode, StartedAtUtc, CompletedAtUtc, StdOut, StdErr FROM Jobs WHERE Status IN ('Success','Failed','Timeout','Cancelled') AND (CompletedAtUtc IS NULL OR CompletedAtUtc >= $since OR $since = '')";
        cmd.Parameters.AddWithValue("$since", sinceUtc == DateTime.MinValue ? "" : sinceUtc.ToString("o"));
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapJob(r));
        return list;
    }

    public void UpsertPolicy(AppPolicy policy)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO Policies (PolicyId, Version, Application, Action, Target, CreatedAtUtc, CreatedBy) VALUES ($id,$ver,$app,$action,$target,$create,$by) ON CONFLICT(PolicyId, Version) DO UPDATE SET Action=$action, Target=$target";
            cmd.Parameters.AddWithValue("$id", policy.PolicyId);
            cmd.Parameters.AddWithValue("$ver", policy.Version);
            cmd.Parameters.AddWithValue("$app", policy.Application);
            cmd.Parameters.AddWithValue("$action", policy.Action.ToString());
            cmd.Parameters.AddWithValue("$target", policy.Target);
            cmd.Parameters.AddWithValue("$create", policy.CreatedAtUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$by", policy.CreatedBy ?? "");
            cmd.ExecuteNonQuery();
        }
    }

    public List<AppPolicy> GetAppliedPolicies()
    {
        var list = new List<AppPolicy>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT PolicyId, Version, Application, Action, Target, CreatedAtUtc, CreatedBy FROM Policies";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AppPolicy
            {
                PolicyId = r.GetString(0),
                Version = r.GetInt32(1),
                Application = r.GetString(2),
                Action = Enum.TryParse<ApplicationAction>(r.GetString(3), out var a) ? a : ApplicationAction.Block,
                Target = r.GetString(4),
                CreatedAtUtc = DateTime.TryParse(r.IsDBNull(5) ? "" : r.GetString(5), out var created) ? created : DateTime.UtcNow,
                CreatedBy = r.IsDBNull(6) ? "" : r.GetString(6)
            });
        }
        return list;
    }

    public void SaveSyncCursor(SyncCursor cursor)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE SyncState SET LastServerRevision=$r, LastUploadedResultRevision=$u, LastSyncAtUtc=$ts WHERE Id=1";
            cmd.Parameters.AddWithValue("$r", cursor.LastServerRevision);
            cmd.Parameters.AddWithValue("$u", cursor.LastUploadedResultRevision);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
            Cursor = new SyncState { LastServerRevision = cursor.LastServerRevision, LastUploadedResultRevision = cursor.LastUploadedResultRevision };
        }
    }

    public SyncCursor LoadSyncCursor()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT LastServerRevision, LastUploadedResultRevision, LastSyncAtUtc FROM SyncState WHERE Id=1";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new SyncCursor();
        return new SyncCursor
        {
            LastServerRevision = r.GetInt64(0),
            LastUploadedResultRevision = r.GetInt64(1),
            LastSyncAtUtc = r.IsDBNull(2) ? null : r.GetString(2)
        };
    }

    public ConnectionState LoadConnectionState()
    {
        var s = GetSetting("ConnectionState");
        return Enum.TryParse<ConnectionState>(s, out var st) ? st : ConnectionState.Unknown;
    }

    public void SaveConnectionState(ConnectionState state)
    {
        SetSetting("ConnectionState", state.ToString());
        SaveRuntimeState(new RuntimeStateSnapshot
        {
            ConnectionState = state,
            LastSeenAtUtc = DateTime.UtcNow
        });
    }

    public RuntimeStateSnapshot LoadRuntimeState()
    {
        var s = GetSetting("RuntimeState");
        if (s is null) return new RuntimeStateSnapshot();
        try { return System.Text.Json.JsonSerializer.Deserialize<RuntimeStateSnapshot>(s) ?? new RuntimeStateSnapshot(); }
        catch { return new RuntimeStateSnapshot(); }
    }

    public void SaveRuntimeState(RuntimeStateSnapshot state)
    {
        SetSetting("RuntimeState", System.Text.Json.JsonSerializer.Serialize(state));
    }

    public void Dispose() => SqliteConnection.ClearAllPools();
}

public sealed class SyncState
{
    public static SyncState Default => new();
    public long LastServerRevision { get; set; }
    public long LastUploadedResultRevision { get; set; }
}

public sealed class RuntimeStateSnapshot
{
    public ConnectionState ConnectionState { get; set; } = ConnectionState.Unknown;
    public DateTime LastSeenAtUtc { get; set; }
    public DateTime? LastSyncAtUtc { get; set; }
    public string? LastError { get; set; }
    public string? LastServerUrl { get; set; }
}
