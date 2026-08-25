using System.Net.WebSockets;
using System.Text;
using LanManagement.Server.Protocol;
using LanManagement.Server.Security;
using LanManagement.Server.Services;

namespace LanManagement.Server.Transport;

public interface IAgentSessionRunner
{
    Task RunAsync(WebSocket socket, string remoteIp, CancellationToken cancellationToken);
}

/// <summary>Owns one WebSocket session. Only REGISTER_DEVICE may be the first inbound message.</summary>
public sealed class AgentSessionRunner(
    IAgentAuthenticationService authentication,
    IAgentTokenHasher tokenHasher,
    IDeviceService devices,
    IPolicyService policies,
    ICommandService commands,
    IAgentConnectionManager connections,
    ICommandWorkSignal commandSignal,
    ISyncWorkSignal syncSignal,
    IClock clock,
    IOptions<LanManagement.Server.Options.ServerOptions> options,
    ILogger<AgentSessionRunner> logger) : IAgentSessionRunner
{
    public async Task RunAsync(WebSocket socket, string remoteIp, CancellationToken cancellationToken)
    {
        using var connection = new WebSocketAgentConnection(socket, remoteIp);
        try
        {
            while (!cancellationToken.IsCancellationRequested && (socket.State is WebSocketState.Open or WebSocketState.CloseReceived))
            {
                var raw = await ReceiveTextAsync(socket, options.Value.MaximumMessageBytes, cancellationToken);
                if (raw is null)
                {
                    break;
                }

                if (!ProtocolParser.TryParse(raw, out var message, out var parseError) || message is null)
                {
                    await SendErrorAsync(connection, parseError?.Code ?? "INVALID_MESSAGE", parseError?.Message ?? "Invalid message.", parseError?.CorrelationId, cancellationToken);
                    continue;
                }

                if (connection.DeviceId is null)
                {
                    if (message is not RegisterDeviceMessage registration)
                    {
                        await SendErrorAsync(connection, "REGISTER_REQUIRED", "REGISTER_DEVICE must be the first message on a connection.", message.MessageId, cancellationToken);
                        await connection.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Registration required.", cancellationToken);
                        break;
                    }

                    if (!await RegisterAsync(registration, connection, cancellationToken))
                    {
                        break;
                    }
                    continue;
                }

                if (message is RegisterDeviceMessage)
                {
                    await SendErrorAsync(connection, "ALREADY_REGISTERED", "A connection may register only once.", message.MessageId, cancellationToken);
                    continue;
                }

                if (!string.Equals(message.DeviceId, connection.DeviceId, StringComparison.Ordinal))
                {
                    await SendErrorAsync(connection, "DEVICE_ID_MISMATCH", "Message device_id does not match the authenticated connection.", message.MessageId, cancellationToken);
                    await connection.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Device ID mismatch.", cancellationToken);
                    break;
                }

                await HandleAuthenticatedMessageAsync(message, connection, cancellationToken);
            }
        }
        catch (WebSocketException exception)
        {
            logger.LogInformation(exception, "Agent WebSocket from {RemoteIp} closed unexpectedly.", remoteIp);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Server shutdown.
        }
        finally
        {
            if (connection.DeviceId is { } deviceId && connections.Remove(deviceId, connection))
            {
                await devices.MarkOfflineAsync(deviceId, "CONNECTION_CLOSED", CancellationToken.None);
            }
        }
    }

    private async Task<bool> RegisterAsync(RegisterDeviceMessage registration, IAgentConnection connection, CancellationToken cancellationToken)
    {
        var authenticationResult = await authentication.AuthenticateAsync(registration.DeviceId, registration.AuthToken, cancellationToken);
        if (!authenticationResult.Succeeded)
        {
            logger.LogWarning("Rejected registration for {DeviceId} from {RemoteIp}: {Code}", registration.DeviceId, connection.RemoteIp, authenticationResult.FailureCode);
            await SendErrorAsync(connection, authenticationResult.FailureCode ?? "AUTH_FAILED", "Agent authentication failed.", registration.MessageId, cancellationToken);
            await connection.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Authentication failed.", cancellationToken);
            return false;
        }

        var enrollmentHash = authenticationResult.CanEnroll ? tokenHasher.Hash(registration.AuthToken) : null;
        var result = await devices.RegisterAsync(registration, connection.RemoteIp, cancellationToken, enrollmentHash);

        // Do not expose this session to dispatchers until the Agent has received its registration
        // response. REGISTERED therefore always precedes SYNC_REQUEST or COMMAND on this socket.
        try
        {
            await connection.SendAsync(new RegisteredMessage
            {
                SentAt = clock.UtcNow,
                DeviceId = registration.DeviceId,
                ServerTime = clock.UtcNow,
                ServerConfiguration = new ServerConfiguration(options.Value.AgentEndpoint,
                    options.Value.HeartbeatIntervalSeconds, options.Value.HeartbeatTimeoutSeconds, options.Value.MaximumMessageBytes),
                DesiredPolicyVersion = result.DesiredPolicyVersion,
                DesiredPolicy = ProtocolJson.ToElement(result.DesiredPolicyJson),
                PendingCommandCount = result.PendingCommandCount,
                SyncRequired = result.SyncRequired
            }, cancellationToken);
        }
        catch
        {
            await devices.MarkOfflineAsync(registration.DeviceId, "REGISTRATION_RESPONSE_FAILED", CancellationToken.None);
            throw;
        }
        await connections.BindAsync(registration.DeviceId, connection, cancellationToken);
        await policies.QueueDeviceAsync(registration.DeviceId, "DEVICE_REGISTERED", cancellationToken);
        commandSignal.Pulse();
        syncSignal.Pulse();
        logger.LogInformation("Authenticated agent {DeviceId} ({Hostname}) from {RemoteIp}.", registration.DeviceId, registration.Hostname, connection.RemoteIp);
        return true;
    }

    private async Task HandleAuthenticatedMessageAsync(AgentMessage message, IAgentConnection connection, CancellationToken cancellationToken)
    {
        switch (message)
        {
            case HeartbeatMessage heartbeat:
                if (!await devices.RecordHeartbeatAsync(heartbeat, cancellationToken))
                {
                    await SendErrorAsync(connection, "DEVICE_DISABLED", "This device is no longer authorized.", heartbeat.MessageId, cancellationToken);
                    await connection.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Device disabled.", cancellationToken);
                    return;
                }
                await SendAcknowledgementAsync(connection, heartbeat.MessageId, ProtocolConstants.Heartbeat, null, cancellationToken);
                await policies.QueueDeviceAsync(heartbeat.DeviceId, "HEARTBEAT_RECONCILIATION", cancellationToken);
                syncSignal.Pulse();
                break;

            case AgentSyncRequestMessage syncRequest:
                await SendAcknowledgementAsync(connection, syncRequest.MessageId, ProtocolConstants.SyncRequest, null, cancellationToken);
                await policies.QueueDeviceAsync(syncRequest.DeviceId, syncRequest.Reason ?? "AGENT_REQUEST", cancellationToken);
                syncSignal.Pulse();
                break;

            case SyncResultMessage syncResult:
                await policies.HandleResultAsync(syncResult.DeviceId, syncResult.PolicyVersion, syncResult.Status,
                    syncResult.ActualState.GetRawText(), syncResult.Error, syncResult.CompletedAt, cancellationToken);
                await SendAcknowledgementAsync(connection, syncResult.MessageId, ProtocolConstants.SyncResult, null, cancellationToken);
                break;

            case CommandReceivedMessage receipt:
                var receiptOutcome = await commands.RecordReceiptAsync(receipt, cancellationToken);
                if (receiptOutcome.Accepted)
                {
                    await SendAcknowledgementAsync(connection, receipt.MessageId, ProtocolConstants.CommandReceived, receipt.CommandId, cancellationToken);
                }
                else
                {
                    await SendErrorAsync(connection, receiptOutcome.ErrorCode ?? "COMMAND_REJECTED", "Command receipt was rejected.", receipt.MessageId, cancellationToken);
                }
                break;

            case CommandResultMessage result:
                var resultOutcome = await commands.RecordResultAsync(result, cancellationToken);
                if (resultOutcome.Accepted)
                {
                    await SendAcknowledgementAsync(connection, result.MessageId, ProtocolConstants.CommandResult, result.CommandId, cancellationToken);
                }
                else
                {
                    await SendErrorAsync(connection, resultOutcome.ErrorCode ?? "COMMAND_REJECTED", "Command result was rejected.", result.MessageId, cancellationToken);
                }
                break;

            default:
                await SendErrorAsync(connection, "UNSUPPORTED_MESSAGE", "The message type is not allowed after registration.", message.MessageId, cancellationToken);
                break;
        }
    }

    private async Task SendAcknowledgementAsync(IAgentConnection connection, Guid messageId, string type, Guid? commandId, CancellationToken cancellationToken) =>
        await connection.SendAsync(new AcknowledgementMessage
        {
            SentAt = clock.UtcNow,
            AcknowledgedMessageId = messageId,
            AcknowledgedType = type,
            CommandId = commandId
        }, cancellationToken);

    private async Task SendErrorAsync(IAgentConnection connection, string code, string message, Guid? correlationId, CancellationToken cancellationToken) =>
        await connection.SendAsync(new ErrorMessage
        {
            SentAt = clock.UtcNow,
            Code = code,
            Message = message,
            CorrelationId = correlationId
        }, cancellationToken);

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, int maximumBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        await using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
                }
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new WebSocketException(WebSocketError.InvalidMessageType, "Only UTF-8 text WebSocket messages are accepted.");
            }

            if (stream.Length + result.Count > maximumBytes)
            {
                throw new WebSocketException(WebSocketError.HeaderError, "Protocol message exceeds configured size.");
            }
            await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
            }
        }
    }
}
