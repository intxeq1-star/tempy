using System.Text.Json;

namespace LanAgent.Core.Protocol;

#pragma warning disable CS1591 // missing XML docs

/// <summary>Message type constants (PROTOCOL_CONTRACT §5).</summary>
public static class MsgType
{
    // Agent -> Server
    public const string REGISTER = "REGISTER";
    public const string HELLO = "HELLO";
    public const string HEARTBEAT = "HEARTBEAT";
    public const string PONG = "PONG";
    public const string SYNC_REQUEST = "SYNC_REQUEST";
    public const string SYNC_STARTED = "SYNC_STARTED";
    public const string SYNC_RESULT = "SYNC_RESULT";
    public const string COMMAND_RECEIVED = "COMMAND_RECEIVED";
    public const string COMMAND_STATUS = "COMMAND_STATUS";
    public const string COMMAND_RESULT = "COMMAND_RESULT";
    public const string GET_PENDING_COMMANDS = "GET_PENDING_COMMANDS";
    public const string STATE_REPORT = "STATE_REPORT";
    public const string ERROR = "ERROR";

    // Server -> Agent
    public const string REGISTER_ACK = "REGISTER_ACK";
    public const string HELLO_ACK = "HELLO_ACK";
    public const string PING = "PING";
    public const string SYNC_PAYLOAD = "SYNC_PAYLOAD";
    public const string COMMAND = "COMMAND";
    public const string COMMAND_RESULT_ACK = "COMMAND_RESULT_ACK";
    public const string GET_STATE = "GET_STATE";
    public const string POLICY_CHANGED = "POLICY_CHANGED";
}

/// <summary>Error codes (PROTOCOL_CONTRACT §5.22).</summary>
public static class ErrorCode
{
    public const string AuthFailed = "AUTH_FAILED";
    public const string AuthExpired = "AUTH_EXPIRED";
    public const string EnrollmentRejected = "ENROLLMENT_REJECTED";
    public const string ReplayDetected = "REPLAY_DETECTED";
    public const string ValidationError = "VALIDATION_ERROR";
    public const string ProtocolError = "PROTOCOL_ERROR";
    public const string UnknownMessage = "UNKNOWN_MESSAGE";
    public const string WrongDevice = "WRONG_DEVICE";
    public const string MessageTooLarge = "MESSAGE_TOO_LARGE";
    public const string RateLimited = "RATE_LIMITED";
    public const string InternalError = "INTERNAL_ERROR";
    public const string ServerShuttingDown = "SERVER_SHUTTING_DOWN";
    public const string UnsupportedType = "UNSUPPORTED_TYPE";
}

/// <summary>Command types the agent implements (PROTOCOL_CONTRACT §10).</summary>
public static class CommandType
{
    public const string GetSystemInfo = "GET_SYSTEM_INFO";
    public const string GetInstalledApps = "GET_INSTALLED_APPS";
    public const string InstallApp = "INSTALL_APP";
    public const string UninstallApp = "UNINSTALL_APP";
    public const string UpdateApp = "UPDATE_APP";
    public const string CheckApp = "CHECK_APP";
    public const string ApplyDns = "APPLY_DNS";
    public const string CheckDns = "CHECK_DNS";
    public const string ApplyBrowserPolicy = "APPLY_BROWSER_POLICY";
    public const string CheckBrowserPolicy = "CHECK_BROWSER_POLICY";
    public const string RemoveBrowserPolicy = "REMOVE_BROWSER_POLICY";
    public const string ApplyAppPolicy = "APPLY_APP_POLICY";
    public const string CheckAppPolicy = "CHECK_APP_POLICY";
    public const string SyncPolicy = "SYNC_POLICY";
    public const string RestartAgent = "RESTART_AGENT";
    public const string RestartPc = "RESTART_PC";
    public const string ShutdownPc = "SHUTDOWN_PC";
    public const string LockPc = "LOCK_PC";
    public const string LogoffUser = "LOGOFF_USER";
    public const string RunAdminCommand = "RUN_ADMIN_COMMAND";
    public const string UpdateAgent = "UPDATE_AGENT";

    public static readonly IReadOnlyList<string> All = new[]
    {
        GetSystemInfo, GetInstalledApps, InstallApp, UninstallApp, UpdateApp, CheckApp,
        ApplyDns, CheckDns, ApplyBrowserPolicy, CheckBrowserPolicy, RemoveBrowserPolicy,
        ApplyAppPolicy, CheckAppPolicy, SyncPolicy, RestartAgent, RestartPc, ShutdownPc,
        LockPc, LogoffUser, RunAdminCommand, UpdateAgent
    };
}

/// <summary>Agent capabilities advertised in REGISTER/HELLO.</summary>
public static class Capabilities
{
    public static readonly string[] Default =
    {
        "winget", "dns", "browser_policy", "app_policy", "power", "admin_command", "agent_update"
    };
}

/// <summary>Raised when an inbound frame violates the protocol envelope.</summary>
public sealed class ProtocolException : Exception
{
    public string Code { get; }
    public ProtocolException(string code, string message) : base(message) => Code = code;
}

/// <summary>
/// Parsed inbound message: the common envelope fields plus typed accessors over the raw body.
/// Unknown fields are retained in <see cref="Body"/> for forward compatibility.
/// </summary>
public sealed class AgentMessage
{
    public required int V { get; init; }
    public required string Type { get; init; }
    public required string MsgId { get; init; }
    public string? Timestamp { get; init; }
    public string? DeviceId { get; init; }
    /// <summary>Full raw JSON of the message (envelope + payload fields).</summary>
    public required JsonElement Body { get; init; }

    public string? GetString(string name)
    {
        if (Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty(name, out var el))
        {
            if (el.ValueKind == JsonValueKind.String) return el.GetString();
            if (el.ValueKind == JsonValueKind.Null) return null;
            return el.GetRawText();
        }
        return null;
    }

    public int? GetInt(string name)
    {
        if (Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty(name, out var el))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v)) return v;
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int s)) return s;
        }
        return null;
    }

    public long? GetLong(string name)
    {
        if (Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty(name, out var el))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long v)) return v;
        }
        return null;
    }

    public bool GetBool(string name, bool defaultValue = false)
    {
        if (Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty(name, out var el))
        {
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.False) return false;
            if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out bool b)) return b;
        }
        return defaultValue;
    }

    public JsonElement? Get(string name)
    {
        if (Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty(name, out var el))
        {
            return el.ValueKind == JsonValueKind.Undefined ? null : el;
        }
        return null;
    }

    public T? GetEnum<T>(string name) where T : struct, Enum
    {
        string? raw = GetString(name);
        return raw is null ? null : Enum.TryParse<T>(raw, ignoreCase: true, out T value) ? value : null;
    }
}

/// <summary>Message construction / parsing / serialization (PROTOCOL_CONTRACT §3).</summary>
public static class Protocol
{
    public const int Version = 1;

    /// <summary>Builds an outbound message: envelope (v, type, msg_id, timestamp, device_id) + fields. Null values are dropped.</summary>
    public static Dictionary<string, object?> Make(string type, string? deviceId, Dictionary<string, object?>? fields = null)
    {
        var msg = new Dictionary<string, object?>(fields?.Count + 5 ?? 5)
        {
            ["v"] = Version,
            ["type"] = type,
            ["msg_id"] = Util.Tx.NewUuid(),
            ["timestamp"] = Util.Tx.NowIso()
        };
        if (deviceId is not null) msg["device_id"] = deviceId;
        if (fields is not null)
        {
            foreach (var kv in fields)
            {
                if (kv.Value is not null || kv.Key == "exit_code") msg[kv.Key] = kv.Value;
            }
        }
        return msg;
    }

    /// <summary>Parse and validate the envelope of an inbound frame. Throws <see cref="ProtocolException"/> on violation.</summary>
    public static AgentMessage Parse(string json, int maxBytes = 1024 * 1024)
    {
        if (string.IsNullOrEmpty(json)) throw new ProtocolException(ErrorCode.ProtocolError, "empty frame");
        if (System.Text.Encoding.UTF8.GetByteCount(json) > maxBytes)
            throw new ProtocolException(ErrorCode.MessageTooLarge, $"frame exceeds {maxBytes} bytes");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ProtocolException(ErrorCode.ProtocolError, "invalid JSON: " + ex.Message);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ProtocolException(ErrorCode.ProtocolError, "frame root must be a JSON object");

            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(typeEl.GetString()))
                throw new ProtocolException(ErrorCode.ProtocolError, "missing/invalid 'type'");

            int v = 1;
            if (root.TryGetProperty("v", out var vEl))
            {
                if (vEl.ValueKind != JsonValueKind.Number || !vEl.TryGetInt32(out v) || v < 1 || v > Version)
                    throw new ProtocolException(ErrorCode.ProtocolError, $"unsupported protocol version '{vEl.GetRawText()}'");
            }

            string? msgId = null;
            if (root.TryGetProperty("msg_id", out var msgIdEl) && msgIdEl.ValueKind == JsonValueKind.String)
                msgId = msgIdEl.GetString();

            string? ts = null;
            if (root.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.String)
                ts = tsEl.GetString();

            string? deviceId = null;
            if (root.TryGetProperty("device_id", out var devEl) && devEl.ValueKind == JsonValueKind.String)
                deviceId = devEl.GetString();

            return new AgentMessage
            {
                V = v,
                Type = typeEl.GetString()!,
                MsgId = msgId ?? Util.Tx.NewUuid(),
                Timestamp = ts,
                DeviceId = deviceId,
                Body = root.Clone()
            };
        }
    }

    public static string Serialize(Dictionary<string, object?> message) => Util.AgentJson.Serialize(message);
}
