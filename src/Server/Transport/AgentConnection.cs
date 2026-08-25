using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LanManagement.Server.Protocol;

namespace LanManagement.Server.Transport;

public interface IAgentConnection
{
    Guid SessionId { get; }
    string? DeviceId { get; }
    string RemoteIp { get; }
    DateTimeOffset ConnectedAt { get; }
    bool IsAuthenticated { get; }
    bool IsOpen { get; }
    void BindDevice(string deviceId);
    Task SendAsync<T>(T message, CancellationToken cancellationToken);
    Task CloseAsync(WebSocketCloseStatus closeStatus, string description, CancellationToken cancellationToken);
}

public sealed class WebSocketAgentConnection(WebSocket socket, string remoteIp) : IAgentConnection, IDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private string? _deviceId;

    public Guid SessionId { get; } = Guid.NewGuid();
    public string? DeviceId => _deviceId;
    public string RemoteIp { get; } = remoteIp;
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public bool IsAuthenticated => _deviceId is not null;
    public bool IsOpen => socket.State == WebSocketState.Open;

    public void BindDevice(string deviceId) => _deviceId = deviceId;

    public async Task SendAsync<T>(T message, CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            throw new WebSocketException(WebSocketError.InvalidState, "Agent WebSocket is not open.");
        }

        var text = JsonSerializer.Serialize(message, ProtocolJson.Options);
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsOpen)
            {
                throw new WebSocketException(WebSocketError.InvalidState, "Agent WebSocket closed while sending.");
            }
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task CloseAsync(WebSocketCloseStatus closeStatus, string description, CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(closeStatus, description, cancellationToken);
            }
            catch (WebSocketException)
            {
                // The peer may already have gone away; removal is still handled by the session runner.
            }
        }
    }

    public void Dispose() => _sendLock.Dispose();
}

public interface IAgentConnectionManager
{
    Task BindAsync(string deviceId, IAgentConnection connection, CancellationToken cancellationToken);
    bool TryGet(string deviceId, out IAgentConnection? connection);
    bool IsCurrent(string deviceId, IAgentConnection connection);
    bool Remove(string deviceId, IAgentConnection connection);
    IReadOnlyCollection<IAgentConnection> Connections { get; }
}

/// <summary>Device ID, not IP address, is the connection key. A newer authenticated session supersedes an older one.</summary>
public sealed class AgentConnectionManager : IAgentConnectionManager
{
    private readonly ConcurrentDictionary<string, IAgentConnection> _connections = new(StringComparer.Ordinal);
    private readonly ILogger<AgentConnectionManager> _logger;

    public AgentConnectionManager(ILogger<AgentConnectionManager> logger) => _logger = logger;

    public IReadOnlyCollection<IAgentConnection> Connections => _connections.Values.ToArray();

    public async Task BindAsync(string deviceId, IAgentConnection connection, CancellationToken cancellationToken)
    {
        connection.BindDevice(deviceId);
        if (_connections.TryGetValue(deviceId, out var existing) && existing.SessionId != connection.SessionId)
        {
            _connections[deviceId] = connection;
            _logger.LogWarning("A new authenticated session superseded session {OldSession} for {DeviceId}.", existing.SessionId, deviceId);
            await existing.CloseAsync(WebSocketCloseStatus.NormalClosure, "Superseded by a newer authenticated connection.", cancellationToken);
            return;
        }

        _connections[deviceId] = connection;
    }

    public bool TryGet(string deviceId, out IAgentConnection? connection)
    {
        if (_connections.TryGetValue(deviceId, out var candidate) && candidate.IsOpen && candidate.IsAuthenticated)
        {
            connection = candidate;
            return true;
        }

        connection = null;
        return false;
    }

    public bool IsCurrent(string deviceId, IAgentConnection connection) =>
        _connections.TryGetValue(deviceId, out var existing) && existing.SessionId == connection.SessionId;

    public bool Remove(string deviceId, IAgentConnection connection)
    {
        if (!IsCurrent(deviceId, connection))
        {
            return false;
        }

        return ((ICollection<KeyValuePair<string, IAgentConnection>>)_connections)
            .Remove(new KeyValuePair<string, IAgentConnection>(deviceId, connection));
    }
}
