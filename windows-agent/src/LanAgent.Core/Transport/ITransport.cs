using System.Net.WebSockets;
using System.Text;

namespace LanAgent.Core.Transport;

/// <summary>Abstract persistent message transport (WebSocket in production, fake in tests).</summary>
public interface ITransport : IDisposable
{
    /// <summary>Open the connection. Throws on failure/timeout.</summary>
    Task ConnectAsync(Uri url, TimeSpan timeout, CancellationToken ct);

    /// <summary>Send one text message. Throws on failure; caller decides session fate.</summary>
    Task SendAsync(string text, CancellationToken ct);

    /// <summary>
    /// Receive one text message. Returns null when the peer closed the connection cleanly.
    /// Throws on hard failure or cancellation (dead link detected by WebSocket keep-alive pings).
    /// </summary>
    Task<string?> ReceiveAsync(CancellationToken ct);

    /// <summary>Immediately tear the connection down (no graceful close handshake).</summary>
    void Abort();
}

/// <summary>WebSocket implementation of <see cref="ITransport"/> (RFC 6455 text frames, 1 message per frame).</summary>
public sealed class WebSocketTransport : ITransport
{
    private ClientWebSocket? _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly int _maxMessageBytes;

    public WebSocketTransport(int maxMessageBytes = 1024 * 1024)
    {
        _maxMessageBytes = maxMessageBytes;
    }

    public async Task ConnectAsync(Uri url, TimeSpan timeout, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        // Protocol-level keep-alive: dead peers (cable pull, silent drop) are detected even when
        // the application is idle. Interval mirrors the heartbeat cadence.
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(url, linked.Token).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        _socket = socket;
    }

    public async Task SendAsync(string text, CancellationToken ct)
    {
        var socket = _socket ?? throw new InvalidOperationException("transport not connected");
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (socket.State != WebSocketState.Open) throw new WebSocketException($"socket not open ({socket.State})");
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var socket = _socket ?? throw new InvalidOperationException("transport not connected");
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Abort();
                throw;
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                return null; // peer closed
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            if (result.MessageType != WebSocketMessageType.Text)
                throw new WebSocketException("binary frames are not part of the protocol (text only)");

            message.Write(buffer, 0, result.Count);
            if (message.Length > _maxMessageBytes)
            {
                Abort();
                throw new WebSocketException($"inbound message exceeds {_maxMessageBytes} bytes");
            }

            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(message.ToArray());
            // else: continue accumulating fragments
        }
    }

    public void Abort()
    {
        try { _socket?.Abort(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        try { _socket?.Dispose(); } catch { /* ignore */ }
        _sendLock.Dispose();
    }
}
