using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LanAgent.TestServer;

/// <summary>One durable command job (server-side state machine per PROTOCOL_CONTRACT §8.1).</summary>
public sealed class CommandJob
{
    public required string CommandId { get; init; }
    public required string DeviceId { get; init; }
    public required string CommandType { get; init; }
    public required JsonElement Payload { get; init; }
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;
    public int Attempt { get; set; }
    public string State { get; set; } = "PENDING"; // PENDING|SENT|RECEIVED|RUNNING|SUCCESS|FAILED
    public int RunningSeen { get; set; }
    public int ReceivedSeen { get; set; }
    public JsonElement? FirstResult { get; set; }
    public bool ResultAckedTo { get; set; }
    public bool DuplicateResultSeen { get; set; }
    public bool Success => State == "SUCCESS";
}

public sealed class DeviceState
{
    public required string DeviceId { get; init; }
    public Session? Session { get; set; }
    public string? Token { get; set; }
    public DateTime LastHeartbeatUtc { get; set; }
    public int Heartbeats { get; set; }
    public DateTime OnlineSinceUtc { get; set; }
    public List<CommandJob> Jobs { get; } = new();
    public DateTimeOffset? LastResultAt { get; set; }
}

/// <summary>Raw inbound message + parsed JSON, kept for assertions.</summary>
public sealed record InboundMessage(string DeviceId, string Type, JsonElement Json, DateTime ReceivedUtc);

/// <summary>Simulated Server.exe: WebSocket protocol + durable command queue + policy store.</summary>
public sealed class SimServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, DeviceState> _devices = new();
    private readonly object _jobLock = new();

    public string EnrollmentKey { get; set; } = "test-enroll-key";
    public int PolicyVersion { get; set; } = 7;
    public string DesiredStateJson { get; set; } = """
        {
          "dns": { "enabled": true, "servers": ["192.168.1.100"], "apply_to_wireless": false, "reset_to_dhcp": false },
          "browser_policy": { "chrome": { "incognito": "disabled" }, "edge": { "inprivate": "disabled" } }
        }
        """;

    public ConcurrentQueue<InboundMessage> Received { get; } = new();
    private readonly List<TaskCompletionSource<InboundMessage>> _waiters = new();
    private readonly object _waiterLock = new();

    // ------------------------------------------------------------------ lifecycle

    public SimServer(int port, bool localhostOnly = false)
    {
        string prefix = localhostOnly ? $"http://localhost:{port}/" : $"http://*:{port}/";
        _listener.Prefixes.Add(prefix);
        Prefix = prefix;
        Port = port;
    }

    public string Prefix { get; }
    public int Port { get; }

    public async Task StartAsync()
    {
        _listener.Start();
        Console.WriteLine($"[server] listening on {Prefix}");
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        lock (_waiterLock)
        {
            foreach (var waiter in _waiters) waiter.TrySetCanceled();
            _waiters.Clear();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return; // listener stopped
            }

            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            _ = Task.Run(() => path switch
            {
                "/agent/ws" => HandleWebSocketAsync(ctx, ct),
                "/health" => HandleHealthAsync(ctx),
                var p when p.StartsWith("/agent/update/") => HandleUpdateDownloadAsync(ctx, p),
                _ => Respond404Async(ctx)
            }, ct);
        }
    }

    private static async Task HandleHealthAsync(HttpListenerContext ctx)
    {
        string body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["server_time"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz")
        });
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private static async Task HandleUpdateDownloadAsync(HttpListenerContext ctx, string path)
    {
        // Serves a dummy package so UPDATE_AGENT download paths can be exercised.
        byte[] bytes = Encoding.UTF8.GetBytes($"dummy package for {path}");
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private static Task Respond404Async(HttpListenerContext ctx)
    {
        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
        return Task.CompletedTask;
    }

    // ----------------------------------------------------------------- websocket

    private async Task HandleWebSocketAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        if (!ctx.Request.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }

        System.Net.WebSockets.WebSocket ws;
        try
        {
            ws = (await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false)).WebSocket;
        }
        catch
        {
            return;
        }

        var session = new Session(ws);
        try
        {
            await SessionLoopAsync(session, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[server] session ended: {ex.Message}");
        }
        finally
        {
            MarkOffline(session.DeviceId);
            session.Dispose();
        }
    }

    private async Task SessionLoopAsync(Session session, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested && session.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await session.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            string json = Encoding.UTF8.GetString(message.ToArray());
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch
            {
                await SendAsync(session, Error("PROTOCOL_ERROR", "invalid JSON", null)).ConfigureAwait(false);
                continue;
            }
            using (doc)
            {
                await HandleMessageAsync(session, doc.RootElement.Clone()).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleMessageAsync(Session session, JsonElement msg)
    {
        string type = GetString(msg, "type") ?? "";
        string deviceId = GetString(msg, "device_id") ?? session.DeviceId;

        Record(msg, deviceId, type);

        switch (type)
        {
            case "REGISTER":
                await HandleRegisterAsync(session, msg).ConfigureAwait(false);
                break;

            case "HELLO":
                await HandleHelloAsync(session, msg, deviceId).ConfigureAwait(false);
                break;

            case "HEARTBEAT":
            {
                var device = _devices.GetOrAdd(deviceId, id => new DeviceState { DeviceId = id });
                device.Heartbeats++;
                device.LastHeartbeatUtc = DateTime.UtcNow;
                if (device.Session is null || device.Session != session)
                {
                    // Presence is server-authoritative: a heartbeat on a non-authenticated socket
                    // does not flip ONLINE. (Contract §7.)
                }
                break;
            }

            case "SYNC_REQUEST":
            {
                var device = _devices.GetOrAdd(deviceId, id => new DeviceState { DeviceId = id });
                await SendAsync(session, new Dictionary<string, object?>
                {
                    ["v"] = 1,
                    ["type"] = "SYNC_PAYLOAD",
                    ["msg_id"] = Guid.NewGuid().ToString("D"),
                    ["timestamp"] = NowIso(),
                    ["device_id"] = deviceId,
                    ["policy_version"] = PolicyVersion,
                    ["issued_at"] = NowIso(),
                    ["desired_state"] = JsonDocument.Parse(DesiredStateJson).RootElement.Clone()
                }).ConfigureAwait(false);
                break;
            }

            case "COMMAND_RECEIVED":
            {
                var job = FindJob(GetString(msg, "command_id"));
                if (job is not null)
                {
                    lock (_jobLock)
                    {
                        job.ReceivedSeen++;
                        if (job.State is "PENDING" or "SENT") job.State = "RECEIVED";
                    }
                }
                break;
            }

            case "COMMAND_STATUS":
            {
                var job = FindJob(GetString(msg, "command_id"));
                if (job is not null)
                {
                    lock (_jobLock)
                    {
                        job.RunningSeen++;
                        job.State = "RUNNING";
                    }
                }
                break;
            }

            case "COMMAND_RESULT":
            {
                string commandId = GetString(msg, "command_id") ?? "";
                var job = FindJob(commandId);
                if (job is not null)
                {
                    lock (_jobLock)
                    {
                        bool duplicate = GetBool(msg, "duplicate", false);
                        if (job.FirstResult is null)
                        {
                            job.FirstResult = msg.Clone();
                            job.State = GetString(msg, "status") == "SUCCESS" ? "SUCCESS" : "FAILED";
                            job.ResultAckedTo = true;
                        }
                        else
                        {
                            job.DuplicateResultSeen = true; // first terminal result wins
                        }
                        var device = _devices.GetOrAdd(job.DeviceId, id => new DeviceState { DeviceId = id });
                        device.LastResultAt = DateTimeOffset.UtcNow;
                    }
                }
                // Server duty: always acknowledge durably-recorded results.
                await SendAsync(session, new Dictionary<string, object?>
                {
                    ["v"] = 1, ["type"] = "COMMAND_RESULT_ACK", ["msg_id"] = Guid.NewGuid().ToString("D"),
                    ["timestamp"] = NowIso(), ["device_id"] = deviceId,
                    ["command_id"] = commandId, ["recorded"] = true
                }).ConfigureAwait(false);
                break;
            }

            case "GET_PENDING_COMMANDS":
                await DeliverPendingAsync(session, deviceId, msg).ConfigureAwait(false);
                break;

            case "PONG":
            case "SYNC_STARTED":
            case "SYNC_RESULT":
            case "STATE_REPORT":
            case "ERROR":
                break; // recorded for assertions

            default:
                await SendAsync(session, Error("UNKNOWN_MESSAGE", $"type '{type}'", GetString(msg, "msg_id"))).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleRegisterAsync(Session session, JsonElement msg)
    {
        string deviceId = GetString(msg, "device_id") ?? "";
        string? key = GetString(msg, "enrollment_key");
        string? nonce = GetString(msg, "nonce");
        string? timestamp = GetString(msg, "timestamp");

        // Server duty: enrollment key + replay window validation (contract §4).
        if (!string.Equals(key, EnrollmentKey, StringComparison.Ordinal))
        {
            await SendAsync(session, Error("ENROLLMENT_REJECTED", "bad enrollment key", null, fatal: true)).ConfigureAwait(false);
            return;
        }
        if (nonce is not null && !_nonces.TryAdd(nonce, DateTime.UtcNow))
        {
            await SendAsync(session, Error("REPLAY_DETECTED", "nonce reuse", null, fatal: true)).ConfigureAwait(false);
            return;
        }
        if (timestamp is not null && DateTimeOffset.TryParse(timestamp, out var ts) &&
            Math.Abs((DateTimeOffset.UtcNow - ts.ToUniversalTime()).TotalSeconds) > 300)
        {
            await SendAsync(session, Error("VALIDATION_ERROR", "timestamp skew > 300s", null, fatal: true)).ConfigureAwait(false);
            return;
        }

        var device = _devices.GetOrAdd(deviceId, id => new DeviceState { DeviceId = id });
        string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        device.Token = token;
        device.Session = session;
        device.OnlineSinceUtc = DateTime.UtcNow;
        session.DeviceId = deviceId;
        session.Authenticated = true;

        await SendAsync(session, new Dictionary<string, object?>
        {
            ["v"] = 1, ["type"] = "REGISTER_ACK", ["msg_id"] = Guid.NewGuid().ToString("D"),
            ["timestamp"] = NowIso(), ["device_id"] = deviceId,
            ["device_token"] = token,
            ["server_time"] = NowIso(),
            ["policy_version"] = PolicyVersion,
            ["heartbeat_interval_sec"] = 5
        }).ConfigureAwait(false);
        Console.WriteLine($"[server] device {deviceId} REGISTERED (online)");
    }

    private async Task HandleHelloAsync(Session session, JsonElement msg, string deviceId)
    {
        var device = _devices.GetOrAdd(deviceId, id => new DeviceState { DeviceId = id });
        string? token = GetString(msg, "device_token");

        if (device.Token is null || token is null || device.Token != token)
        {
            await SendAsync(session, Error("AUTH_EXPIRED", "unknown/expired device_token", null, fatal: true)).ConfigureAwait(false);
            return;
        }

        device.Session = session;
        device.OnlineSinceUtc = DateTime.UtcNow;
        session.DeviceId = deviceId;
        session.Authenticated = true;

        int pending = 0;
        lock (_jobLock) pending = device.Jobs.Count(j => j.State is not "SUCCESS" and not "FAILED");

        await SendAsync(session, new Dictionary<string, object?>
        {
            ["v"] = 1, ["type"] = "HELLO_ACK", ["msg_id"] = Guid.NewGuid().ToString("D"),
            ["timestamp"] = NowIso(), ["device_id"] = deviceId,
            ["server_time"] = NowIso(),
            ["policy_version"] = PolicyVersion,
            ["heartbeat_interval_sec"] = 5,
            ["commands_pending"] = pending
        }).ConfigureAwait(false);
        Console.WriteLine($"[server] device {deviceId} HELLO (online)");
    }

    private async Task DeliverPendingAsync(Session session, string deviceId, JsonElement msg)
    {
        var device = _devices.GetOrAdd(deviceId, id => new DeviceState { DeviceId = id });
        var known = new HashSet<string>(StringComparer.Ordinal);
        if (msg.TryGetProperty("known_completed", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String) known.Add(el.GetString()!);
        }

        List<CommandJob> toSend;
        lock (_jobLock)
        {
            toSend = device.Jobs
                .Where(j => j.State is not "SUCCESS" and not "FAILED" && !known.Contains(j.CommandId))
                .OrderBy(j => j.CreatedUtc)
                .ToList();
        }
        foreach (var job in toSend)
        {
            await SendCommandAsync(session, job).ConfigureAwait(false);
        }
        Console.WriteLine($"[server] GET_PENDING_COMMANDS from {deviceId}: delivered {toSend.Count}");
    }

    // ------------------------------------------------------------------ test API

    private readonly ConcurrentDictionary<string, DateTime> _nonces = new();

    public DeviceState? GetDevice(string deviceId) => _devices.TryGetValue(deviceId, out var d) ? d : null;

    public CommandJob EnqueueCommand(string deviceId, string commandType, object? payload = null, string? commandId = null)
    {
        var device = _devices.GetOrAdd(deviceId, id => new DeviceState { DeviceId = id });
        var job = new CommandJob
        {
            CommandId = commandId ?? Guid.NewGuid().ToString("D"),
            DeviceId = deviceId,
            CommandType = commandType,
            Payload = payload is null
                ? JsonDocument.Parse("{}").RootElement.Clone()
                : JsonSerializer.SerializeToElement(payload)
        };
        lock (_jobLock) device.Jobs.Add(job);
        Console.WriteLine($"[server] command {job.CommandType} {job.CommandId} queued for {deviceId} (PENDING)");
        return job;
    }

    /// <summary>Deliver a job to the live session if any (SENT), else it stays PENDING for the next GET_PENDING_COMMANDS.</summary>
    public async Task<bool> TryDeliverAsync(CommandJob job)
    {
        var device = GetDevice(job.DeviceId);
        var session = device?.Session;
        if (session is null || !session.Open || !session.Authenticated) return false;
        await SendCommandAsync(session, job).ConfigureAwait(false);
        return true;
    }

    private async Task SendCommandAsync(Session session, CommandJob job)
    {
        lock (_jobLock)
        {
            job.Attempt++;
            if (job.State == "PENDING") job.State = "SENT";
        }
        await SendAsync(session, new Dictionary<string, object?>
        {
            ["v"] = 1, ["type"] = "COMMAND", ["msg_id"] = Guid.NewGuid().ToString("D"),
            ["timestamp"] = NowIso(), ["device_id"] = job.DeviceId,
            ["command_id"] = job.CommandId,
            ["command_type"] = job.CommandType,
            ["payload"] = job.Payload,
            ["created_at"] = job.CreatedUtc.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            ["attempt"] = job.Attempt,
            ["requires_result"] = true
        }).ConfigureAwait(false);
        Console.WriteLine($"[server] COMMAND {job.CommandType} {job.CommandId} sent (attempt {job.Attempt})");
    }

    public CommandJob? FindJob(string? commandId)
    {
        if (string.IsNullOrEmpty(commandId)) return null;
        lock (_jobLock)
        {
            foreach (var device in _devices.Values)
                foreach (var job in device.Jobs)
                    if (job.CommandId == commandId) return job;
        }
        return null;
    }

    /// <summary>Abruptly drop a device's connection (simulated cable pull).</summary>
    public void DropDevice(string deviceId)
    {
        var session = GetDevice(deviceId)?.Session;
        session?.Abort();
        MarkOffline(deviceId);
        Console.WriteLine($"[server] dropped connection for {deviceId} (device OFFLINE)");
    }

    private void MarkOffline(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return;
        var device = GetDevice(deviceId);
        if (device is null) return;
        if (device.Session is { } s && !s.Open)
        {
            device.Session = null; // offline until next HELLO/REGISTER (contract §7)
        }
    }

    public Task SendAsync(string deviceId, Dictionary<string, object?> message)
    {
        var session = GetDevice(deviceId)?.Session;
        if (session is null || !session.Open) return Task.CompletedTask;
        return SendAsync(session, message);
    }

    // ------------------------------------------------------------------- plumbing

    private async Task SendAsync(Session session, Dictionary<string, object?> message)
    {
        try
        {
            await session.SendAsync(JsonSerializer.Serialize(message)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[server] send failed: {ex.Message}");
        }
    }

    private static Dictionary<string, object?> Error(string code, string messageText, string? msgIdRef, bool fatal = false)
        => new()
        {
            ["v"] = 1, ["type"] = "ERROR", ["msg_id"] = Guid.NewGuid().ToString("D"),
            ["timestamp"] = NowIso(), ["code"] = code, ["message"] = messageText,
            ["msg_id_ref"] = msgIdRef, ["fatal"] = fatal
        };

    private void Record(JsonElement msg, string deviceId, string type)
    {
        var inbound = new InboundMessage(deviceId, type, msg, DateTime.UtcNow);
        Received.Enqueue(inbound);
        lock (_waiterLock)
        {
            _waiters.RemoveAll(w => w.Task.IsCompleted);
            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                _waiters[i].TrySetResult(inbound);
            }
        }
    }

    /// <summary>Wait for a message matching a predicate. Replays already-received messages unless
    /// <paramref name="onlyNew"/> is set (for ordering-sensitive assertions like "after reconnect").</summary>
    public async Task<InboundMessage> WaitForAsync(Func<InboundMessage, bool> predicate, int timeoutMs, string what, bool onlyNew = false)
    {
        DateTime startedAt = DateTime.UtcNow;

        if (!onlyNew)
        {
            foreach (var existing in Received)
                if (predicate(existing)) return existing;
        }

        var tcs = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiterLock) _waiters.Add(tcs);
        try
        {
            while (true)
            {
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (completed != tcs.Task)
                    throw new TimeoutException($"timed out waiting for {what} ({timeoutMs} ms)");
                InboundMessage msg = await tcs.Task.ConfigureAwait(false);
                if (onlyNew && msg.ReceivedUtc < startedAt) { /* stale delivery — keep waiting */ }
                else if (predicate(msg)) return msg;

                tcs = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_waiterLock) _waiters.Add(tcs);
            }
        }
        finally
        {
            lock (_waiterLock) _waiters.Remove(tcs);
        }
    }

    public static string? GetString(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object &&
           el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static bool GetBool(JsonElement el, string name, bool fallback)
        => el.ValueKind == JsonValueKind.Object &&
           el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : fallback;

    public static string NowIso() => DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz");

    /// <summary>One live WebSocket session.</summary>
    public sealed class Session : IDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        public System.Net.WebSockets.WebSocket Socket { get; }
        public string DeviceId { get; set; } = "";
        public bool Authenticated { get; set; }
        public bool Open => Socket.State == WebSocketState.Open;

        public Session(System.Net.WebSockets.WebSocket socket)
        {
            Socket = socket;
        }

        public async Task SendAsync(string json)
        {
            if (!Open) return;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Abort()
        {
            try { Socket.Abort(); } catch { /* ignore */ }
        }

        public void Dispose()
        {
            Abort();
            try { Socket.Dispose(); } catch { /* ignore */ }
            _sendLock.Dispose();
        }
    }
}
