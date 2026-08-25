using System.Text.Json;
using System.Text.Json.Serialization;
using LanManagement.Server.Domain;

namespace LanManagement.Server.Protocol;

public static class ProtocolConstants
{
    public const string Version = "1.0";
    public const int MaxDeviceIdLength = 128;
    public const string RegisterDevice = "REGISTER_DEVICE";
    public const string Registered = "REGISTERED";
    public const string Heartbeat = "HEARTBEAT";
    public const string SyncRequest = "SYNC_REQUEST";
    public const string SyncResult = "SYNC_RESULT";
    public const string Command = "COMMAND";
    public const string CommandReceived = "COMMAND_RECEIVED";
    public const string CommandResult = "COMMAND_RESULT";
    public const string Acknowledgement = "ACK";
    public const string Error = "ERROR";
}

public abstract record AgentMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("message_id")]
    public Guid MessageId { get; init; }

    [JsonPropertyName("sent_at")]
    public DateTimeOffset SentAt { get; init; }

    [JsonPropertyName("device_id")]
    public string DeviceId { get; init; } = string.Empty;
}

public sealed record RegisterDeviceMessage : AgentMessage
{
    [JsonPropertyName("protocol_version")]
    public string ProtocolVersion { get; init; } = string.Empty;

    [JsonPropertyName("hostname")]
    public string Hostname { get; init; } = string.Empty;

    [JsonPropertyName("agent_version")]
    public string AgentVersion { get; init; } = string.Empty;

    [JsonPropertyName("os_version")]
    public string OsVersion { get; init; } = string.Empty;

    [JsonPropertyName("local_ip")]
    public string LocalIp { get; init; } = string.Empty;

    [JsonPropertyName("auth_token")]
    public string AuthToken { get; init; } = string.Empty;

    [JsonPropertyName("reported_policy_version")]
    public int ReportedPolicyVersion { get; init; }

    [JsonPropertyName("actual_state")]
    public JsonElement? ActualState { get; init; }
}

public sealed record HeartbeatMessage : AgentMessage
{
    [JsonPropertyName("heartbeat_time")]
    public DateTimeOffset HeartbeatTime { get; init; }

    [JsonPropertyName("agent_version")]
    public string? AgentVersion { get; init; }

    [JsonPropertyName("local_ip")]
    public string? LocalIp { get; init; }

    [JsonPropertyName("reported_policy_version")]
    public int? ReportedPolicyVersion { get; init; }

    [JsonPropertyName("actual_state")]
    public JsonElement? ActualState { get; init; }
}

public sealed record AgentSyncRequestMessage : AgentMessage
{
    [JsonPropertyName("reported_policy_version")]
    public int ReportedPolicyVersion { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record SyncResultMessage : AgentMessage
{
    [JsonPropertyName("policy_version")]
    public int PolicyVersion { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("actual_state")]
    public JsonElement ActualState { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("completed_at")]
    public DateTimeOffset CompletedAt { get; init; }
}

public sealed record CommandReceivedMessage : AgentMessage
{
    [JsonPropertyName("command_id")]
    public Guid CommandId { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("received_at")]
    public DateTimeOffset ReceivedAt { get; init; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; init; }
}

public sealed record CommandResultMessage : AgentMessage
{
    [JsonPropertyName("command_id")]
    public Guid CommandId { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("completed_at")]
    public DateTimeOffset CompletedAt { get; init; }

    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; init; }

    [JsonPropertyName("output")]
    public string? Output { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("result_data")]
    public JsonElement? ResultData { get; init; }
}

public sealed record ServerConfiguration(
    [property: JsonPropertyName("server_endpoint")] string ServerEndpoint,
    [property: JsonPropertyName("heartbeat_interval_seconds")] int HeartbeatIntervalSeconds,
    [property: JsonPropertyName("heartbeat_timeout_seconds")] int HeartbeatTimeoutSeconds,
    [property: JsonPropertyName("maximum_message_bytes")] int MaximumMessageBytes);

public sealed record RegisteredMessage
{
    [JsonPropertyName("type")]
    public string Type => ProtocolConstants.Registered;
    [JsonPropertyName("message_id")]
    public Guid MessageId { get; init; } = Guid.NewGuid();
    [JsonPropertyName("sent_at")]
    public DateTimeOffset SentAt { get; init; }
    [JsonPropertyName("device_id")]
    public string DeviceId { get; init; } = string.Empty;
    [JsonPropertyName("server_time")]
    public DateTimeOffset ServerTime { get; init; }
    [JsonPropertyName("server_configuration")]
    public ServerConfiguration ServerConfiguration { get; init; } = null!;
    [JsonPropertyName("desired_policy_version")]
    public int DesiredPolicyVersion { get; init; }
    [JsonPropertyName("desired_policy")]
    public JsonElement DesiredPolicy { get; init; }
    [JsonPropertyName("pending_command_count")]
    public int PendingCommandCount { get; init; }
    [JsonPropertyName("sync_required")]
    public bool SyncRequired { get; init; }
}

public sealed record ServerSyncRequestMessage
{
    [JsonPropertyName("type")]
    public string Type => ProtocolConstants.SyncRequest;
    [JsonPropertyName("message_id")]
    public Guid MessageId { get; init; } = Guid.NewGuid();
    [JsonPropertyName("sent_at")]
    public DateTimeOffset SentAt { get; init; }
    [JsonPropertyName("request_id")]
    public Guid RequestId { get; init; } = Guid.NewGuid();
    [JsonPropertyName("device_id")]
    public string DeviceId { get; init; } = string.Empty;
    [JsonPropertyName("policy_version")]
    public int PolicyVersion { get; init; }
    [JsonPropertyName("desired_policy")]
    public JsonElement DesiredPolicy { get; init; }
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "RECONCILIATION";
}

public sealed record OutboundCommandMessage
{
    [JsonPropertyName("type")]
    public string Type => ProtocolConstants.Command;
    [JsonPropertyName("message_id")]
    public Guid MessageId { get; init; } = Guid.NewGuid();
    [JsonPropertyName("sent_at")]
    public DateTimeOffset SentAt { get; init; }
    [JsonPropertyName("command_id")]
    public Guid CommandId { get; init; }
    [JsonPropertyName("device_id")]
    public string DeviceId { get; init; } = string.Empty;
    [JsonPropertyName("command_type")]
    public string CommandType { get; init; } = string.Empty;
    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("attempt")]
    public int Attempt { get; init; }
}

public sealed record AcknowledgementMessage
{
    [JsonPropertyName("type")]
    public string Type => ProtocolConstants.Acknowledgement;
    [JsonPropertyName("message_id")]
    public Guid MessageId { get; init; } = Guid.NewGuid();
    [JsonPropertyName("sent_at")]
    public DateTimeOffset SentAt { get; init; }
    [JsonPropertyName("acknowledged_message_id")]
    public Guid AcknowledgedMessageId { get; init; }
    [JsonPropertyName("acknowledged_type")]
    public string AcknowledgedType { get; init; } = string.Empty;
    [JsonPropertyName("command_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CommandId { get; init; }
}

public sealed record ErrorMessage
{
    [JsonPropertyName("type")]
    public string Type => ProtocolConstants.Error;
    [JsonPropertyName("message_id")]
    public Guid MessageId { get; init; } = Guid.NewGuid();
    [JsonPropertyName("sent_at")]
    public DateTimeOffset SentAt { get; init; }
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
    [JsonPropertyName("correlation_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CorrelationId { get; init; }
}

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static JsonElement ToElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static JsonElement ToElement<T>(T value) => ToElement(JsonSerializer.Serialize(value, Options));
}
