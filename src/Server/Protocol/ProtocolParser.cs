using System.Net;
using System.Text.Json;

namespace LanManagement.Server.Protocol;

public sealed record ProtocolParseError(string Code, string Message, Guid? CorrelationId = null);

public static class ProtocolParser
{
    public static bool TryParse(string json, out AgentMessage? message, out ProtocolParseError? error)
    {
        message = null;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = new("INVALID_MESSAGE", "A protocol message must be a JSON object.");
                return false;
            }

            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                error = new("INVALID_MESSAGE", "Field type is required.");
                return false;
            }

            var type = typeElement.GetString();
            var messageId = Guid.Empty;
            var hasMessageId = root.TryGetProperty("message_id", out var messageIdElement) &&
                               messageIdElement.ValueKind == JsonValueKind.String &&
                               Guid.TryParse(messageIdElement.GetString(), out messageId);
            if (!hasMessageId || messageId == Guid.Empty)
            {
                error = new("INVALID_MESSAGE", "Field message_id must be a UUID.");
                return false;
            }

            message = type switch
            {
                ProtocolConstants.RegisterDevice => Deserialize<RegisterDeviceMessage>(json),
                ProtocolConstants.Heartbeat => Deserialize<HeartbeatMessage>(json),
                ProtocolConstants.SyncRequest => Deserialize<AgentSyncRequestMessage>(json),
                ProtocolConstants.SyncResult => Deserialize<SyncResultMessage>(json),
                ProtocolConstants.CommandReceived => Deserialize<CommandReceivedMessage>(json),
                ProtocolConstants.CommandResult => Deserialize<CommandResultMessage>(json),
                _ => null
            };

            if (message is null)
            {
                error = new("UNSUPPORTED_MESSAGE", "The message type is not supported.", messageId);
                return false;
            }

            if (!ValidateCommon(message, out var validationError))
            {
                error = new("INVALID_MESSAGE", validationError, messageId);
                message = null;
                return false;
            }

            if (!ValidateSpecific(message, out validationError))
            {
                error = new("INVALID_MESSAGE", validationError, messageId);
                message = null;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = new("INVALID_JSON", "The message is not valid JSON.");
            return false;
        }
    }

    private static T Deserialize<T>(string json) where T : AgentMessage =>
        JsonSerializer.Deserialize<T>(json, ProtocolJson.Options)
        ?? throw new JsonException("Message body is empty.");

    private static bool ValidateCommon(AgentMessage message, out string error)
    {
        if (message.MessageId == Guid.Empty)
        {
            error = "Field message_id must be a UUID.";
            return false;
        }

        if (message.SentAt == default)
        {
            error = "Field sent_at must be an ISO-8601 timestamp.";
            return false;
        }

        if (!IsValidDeviceId(message.DeviceId))
        {
            error = "Field device_id is invalid.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool ValidateSpecific(AgentMessage message, out string error)
    {
        switch (message)
        {
            case RegisterDeviceMessage registration:
                if (!string.Equals(registration.ProtocolVersion, ProtocolConstants.Version, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(registration.Hostname) || registration.Hostname.Length > 255 ||
                    string.IsNullOrWhiteSpace(registration.AgentVersion) || registration.AgentVersion.Length > 64 ||
                    string.IsNullOrWhiteSpace(registration.OsVersion) || registration.OsVersion.Length > 512 ||
                    string.IsNullOrWhiteSpace(registration.LocalIp) || registration.LocalIp.Length > 64 ||
                    !IPAddress.TryParse(registration.LocalIp, out _) ||
                    string.IsNullOrWhiteSpace(registration.AuthToken) || registration.AuthToken.Length > 1024 ||
                    registration.ReportedPolicyVersion < 0 ||
                    (registration.ActualState.HasValue && registration.ActualState.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)))
                {
                    error = "REGISTER_DEVICE has missing or invalid fields.";
                    return false;
                }
                break;

            case HeartbeatMessage heartbeat:
                if (heartbeat.HeartbeatTime == default || heartbeat.ReportedPolicyVersion is < 0 ||
                    heartbeat.AgentVersion?.Length > 64 || heartbeat.LocalIp?.Length > 64 ||
                    (!string.IsNullOrWhiteSpace(heartbeat.LocalIp) && !IPAddress.TryParse(heartbeat.LocalIp, out _)) ||
                    (heartbeat.ActualState.HasValue && heartbeat.ActualState.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)))
                {
                    error = "HEARTBEAT has invalid fields.";
                    return false;
                }
                break;

            case AgentSyncRequestMessage syncRequest when syncRequest.ReportedPolicyVersion < 0 || syncRequest.Reason?.Length > 256:
                error = "SYNC_REQUEST has invalid fields.";
                return false;

            case SyncResultMessage syncResult:
                if (syncResult.PolicyVersion < 0 || syncResult.CompletedAt == default ||
                    syncResult.ActualState.ValueKind != JsonValueKind.Object ||
                    !IsSyncResultStatus(syncResult.Status) || syncResult.Error?.Length > 16384)
                {
                    error = "SYNC_RESULT has invalid fields.";
                    return false;
                }
                break;

            case CommandReceivedMessage received:
                if (received.CommandId == Guid.Empty || received.ReceivedAt == default ||
                    received.Status is not ("RECEIVED" or "RUNNING"))
                {
                    error = "COMMAND_RECEIVED has invalid fields.";
                    return false;
                }
                break;

            case CommandResultMessage result:
                if (result.CommandId == Guid.Empty || result.CompletedAt == default ||
                    result.Status is not ("SUCCESS" or "FAILED" or "CANCELLED") ||
                    result.Output?.Length > 262144 || result.Error?.Length > 65536 ||
                    (result.ResultData.HasValue && result.ResultData.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null)))
                {
                    error = "COMMAND_RESULT has invalid fields.";
                    return false;
                }
                break;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsSyncResultStatus(string? value) => value is "SUCCESS" or "FAILED";

    public static bool IsValidDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > ProtocolConstants.MaxDeviceIdLength)
        {
            return false;
        }

        return deviceId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }
}
