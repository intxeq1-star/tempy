using LanAgent.TestServer;

// =============================================================================================
// Test harness: reference Server.exe simulator (implemented from PROTOCOL_CONTRACT.md only).
// Start this first, then start the agent pointed at it:
//   Agent__ServerIp=127.0.0.1 Agent__EnrollmentKey=test-enroll-key Agent__HeartbeatIntervalSeconds=5
//   Agent__BackoffInitialSeconds=1 Agent__BackoffMaxSeconds=5 dotnet run --project src/LanAgent.Service
// Scenarios run in order against the first device that registers. --mutate enables scenarios
// that change machine state (APPLY_DNS / APPLY_BROWSER_POLICY / app install); read-only by default.
// =============================================================================================

int port = 8765;
bool localhostOnly = false;
bool mutate = false;
foreach (string arg in args)
{
    if (arg.StartsWith("--port=")) int.TryParse(arg["--port=".Length..], out port);
    if (arg == "--localhost") localhostOnly = true;
    if (arg == "--mutate") mutate = true;
}

Console.WriteLine("=== LanAgent test harness (simulated Server.exe) ===");
Console.WriteLine($"enrollment key: test-enroll-key   (agent must be configured with Agent__EnrollmentKey=test-enroll-key)");
Console.WriteLine($"mutating scenarios: {(mutate ? "ON" : "off (read-only commands)")}");

using var server = new SimServer(port, localhostOnly)
{
    EnrollmentKey = "test-enroll-key",
    // Non-invasive default desired state: DNS management disabled by policy, browsers unmanaged.
    DesiredStateJson = """
        {
          "dns": { "enabled": false, "servers": [], "apply_to_wireless": false, "reset_to_dhcp": false },
          "browser_policy": { "chrome": { "incognito": "unmanaged" }, "edge": { "inprivate": "unmanaged" } }
        }
        """
};
await server.StartAsync().ConfigureAwait(false);

var results = new List<(string Name, bool Pass, string Note)>();

Console.WriteLine();
Console.WriteLine("[harness] waiting for the agent to connect and REGISTER (start it now)…");
var register = await server.WaitForAsync(m => m.Type == "REGISTER", 300_000, "REGISTER");
string deviceId = register.Json.TryGetProperty("device_id", out var devEl) ? devEl.GetString()! : "";
Console.WriteLine($"[harness] device under test: {deviceId}");

// ---------------------------------------------------------------- scenario runner

async Task Scenario(string name, Func<Task> body)
{
    Console.WriteLine();
    Console.WriteLine($"--- scenario: {name} ---");
    try
    {
        await body().ConfigureAwait(false);
        results.Add((name, true, ""));
        Console.WriteLine($"[PASS] {name}");
    }
    catch (Exception ex)
    {
        results.Add((name, false, ex.Message));
        Console.WriteLine($"[FAIL] {name}: {ex.Message}");
    }
}

async Task<CommandJob> DeliverAndWaitTerminalAsync(CommandJob job, int timeoutMs = 90_000, int expectedRunning = 1)
{
    var delivered = await server.TryDeliverAsync(job).ConfigureAwait(false);
    if (!delivered) throw new TestFailureException("command not delivered (no live session)");
    _ = await server.WaitForAsync(m => m.Type == "COMMAND_RECEIVED" && IdOf(m.Json) == job.CommandId, 30_000, "COMMAND_RECEIVED").ConfigureAwait(false);
    _ = await server.WaitForAsync(m => m.Type == "COMMAND_STATUS" && IdOf(m.Json) == job.CommandId, 60_000, "COMMAND_STATUS RUNNING").ConfigureAwait(false);
    _ = await server.WaitForAsync(m => m.Type == "COMMAND_RESULT" && IdOf(m.Json) == job.CommandId, timeoutMs, "COMMAND_RESULT").ConfigureAwait(false);
    if (job.RunningSeen != expectedRunning)
        throw new TestFailureException($"expected {expectedRunning} RUNNING report(s), saw {job.RunningSeen}");
    if (job.FirstResult is null)
        throw new TestFailureException("no terminal result recorded");
    return job;
}

static string IdOf(System.Text.Json.JsonElement json)
    => json.TryGetProperty("command_id", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString()! : "";

// ---------------------------------------------------------------- scenarios

await Scenario("registration", async () =>
{
    // REGISTER already captured above; validate its documented fields.
    if (string.IsNullOrEmpty(deviceId)) throw new TestFailureException("REGISTER without device_id");
    foreach (string field in new[] { "hostname", "os_version", "agent_version", "local_ip", "enrollment_key", "nonce" })
        if (!register.Json.TryGetProperty(field, out _))
            throw new TestFailureException($"REGISTER missing field '{field}'");
});

await Scenario("heartbeat", async () =>
{
    var device = server.GetDevice(deviceId) ?? throw new TestFailureException("device unknown");
    int baseline = device.Heartbeats;
    var deadline = DateTime.UtcNow.AddSeconds(90);
    while (device.Heartbeats < baseline + 3)
    {
        if (DateTime.UtcNow > deadline)
            throw new TestFailureException($"heartbeat cadence too slow: {device.Heartbeats - baseline} in 90s (expected 3)");
        await Task.Delay(500);
    }
    var latest = server.Received.LastOrDefault(m => m.Type == "HEARTBEAT" && m.DeviceId == deviceId)
        ?? throw new TestFailureException("no heartbeat recorded");
    if (!latest.Json.TryGetProperty("agent_version", out _) || !latest.Json.TryGetProperty("policy_version", out _))
        throw new TestFailureException("HEARTBEAT missing agent_version/policy_version");
});

await Scenario("synchronization-on-connect", async () =>
{
    var syncRequest = await server.WaitForAsync(
        m => m.Type == "SYNC_REQUEST" && m.DeviceId == deviceId, 30_000, "SYNC_REQUEST").ConfigureAwait(false);
    string? reason = syncRequest.Json.TryGetProperty("reason", out var r) ? r.GetString() : null;
    if (reason is not ("boot" or "reconnect" or "periodic" or "first_install" or "server_push" or "admin" or "policy_changed"))
        throw new TestFailureException($"SYNC_REQUEST has invalid reason '{reason}'");
    var syncResult = await server.WaitForAsync(
        m => m.Type == "SYNC_RESULT" && m.DeviceId == deviceId, 60_000, "SYNC_RESULT").ConfigureAwait(false);
    string status = SimServer.GetString(syncResult.Json, "status") ?? "";
    if (status is not ("SUCCESS" or "NO_CHANGE" or "PARTIAL"))
        throw new TestFailureException($"SYNC_RESULT status was '{status}'");
});

await Scenario("one-pc-command (GET_SYSTEM_INFO)", async () =>
{
    var job = server.EnqueueCommand(deviceId, "GET_SYSTEM_INFO");
    await DeliverAndWaitTerminalAsync(job).ConfigureAwait(false);
    if (job.State != "SUCCESS") throw new TestFailureException($"job state {job.State}, expected SUCCESS");
    string status = SimServer.GetString(job.FirstResult!.Value, "status") ?? "";
    if (status != "SUCCESS") throw new TestFailureException($"result status {status}");
    var result = job.FirstResult.Value.TryGetProperty("result", out var res) && res.ValueKind == System.Text.Json.JsonValueKind.Object ? res : throw new TestFailureException("result.result missing");
    if (!result.TryGetProperty("hostname", out _)) throw new TestFailureException("GET_SYSTEM_INFO result missing hostname");
});

await Scenario("remote-admin-command (RUN_ADMIN_COMMAND)", async () =>
{
    var job = server.EnqueueCommand(deviceId, "RUN_ADMIN_COMMAND", new Dictionary<string, object?>
    {
        ["command"] = "echo hello-agent",
        ["timeout_sec"] = 30
    });
    await DeliverAndWaitTerminalAsync(job).ConfigureAwait(false);
    string stdout = "";
    if (job.FirstResult!.Value.TryGetProperty("result", out var res) && res.TryGetProperty("stdout", out var so))
        stdout = so.GetString() ?? "";
    if (!stdout.Contains("hello-agent")) throw new TestFailureException($"stdout did not contain marker: '{stdout.Trim()}'");
});

await Scenario("command-retry (redelivery, single execution)", async () =>
{
    string commandId = Guid.NewGuid().ToString("D");
    var job = server.EnqueueCommand(deviceId, "GET_INSTALLED_APPS", null, commandId);
    await DeliverAndWaitTerminalAsync(job).ConfigureAwait(false);
    // Redeliver the same command_id (attempt 2): must NOT execute again.
    await server.TryDeliverAsync(job).ConfigureAwait(false);
    await Task.Delay(5_000).ConfigureAwait(false);
    if (job.RunningSeen != 1) throw new TestFailureException($"command re-executed on redelivery (RUNNING x{job.RunningSeen})");
    if (job.ReceivedSeen < 2) throw new TestFailureException($"expected >=2 COMMAND_RECEIVED (saw {job.ReceivedSeen})");
});

await Scenario("duplicate-completed-command (idempotent replay)", async () =>
{
    var job = server.EnqueueCommand(deviceId, "GET_SYSTEM_INFO");
    await DeliverAndWaitTerminalAsync(job).ConfigureAwait(false);
    await server.TryDeliverAsync(job).ConfigureAwait(false); // duplicate delivery AFTER completion
    _ = await server.WaitForAsync(
        m => m.Type == "COMMAND_RESULT" && IdOf(m.Json) == job.CommandId && SimServer.GetBool(m.Json, "duplicate", false),
        30_000, "duplicate COMMAND_RESULT").ConfigureAwait(false);
    if (job.RunningSeen != 1) throw new TestFailureException("dangerous: duplicate command executed twice");
});

await Scenario("offline-command (PENDING survives, delivered on reconnect)", async () =>
{
    var job = server.EnqueueCommand(deviceId, "GET_SYSTEM_INFO"); // NOT delivered: stays PENDING
    server.DropDevice(deviceId);
    // The agent reconnects with HELLO (token auth), then asks for pending commands.
    _ = await server.WaitForAsync(m => m.Type == "HELLO" && m.DeviceId == deviceId, 60_000, "HELLO after reconnect", onlyNew: true).ConfigureAwait(false);
    _ = await server.WaitForAsync(m => m.Type == "GET_PENDING_COMMANDS" && m.DeviceId == deviceId, 60_000, "GET_PENDING_COMMANDS", onlyNew: true).ConfigureAwait(false);
    // The simulator delivers non-terminal jobs in response; wait for full lifecycle.
    var result = await server.WaitForAsync(
        m => m.Type == "COMMAND_RESULT" && IdOf(m.Json) == job.CommandId, 90_000, "result for offline command").ConfigureAwait(false);
    if (SimServer.GetString(result.Json, "status") != "SUCCESS")
        throw new TestFailureException("offline command did not complete successfully");
    if (job.RunningSeen != 1) throw new TestFailureException($"offline command executed {job.RunningSeen}x");
});

await Scenario("network-drop-mid-command (result survives in outbox)", async () =>
{
    var job = server.EnqueueCommand(deviceId, "RUN_ADMIN_COMMAND", new Dictionary<string, object?>
    {
        ["command"] = "echo outbox-test",
        ["timeout_sec"] = 30
    });
    bool delivered = await server.TryDeliverAsync(job).ConfigureAwait(false);
    if (!delivered) throw new TestFailureException("no live session");
    _ = await server.WaitForAsync(m => m.Type == "COMMAND_STATUS" && IdOf(m.Json) == job.CommandId, 30_000, "RUNNING before drop").ConfigureAwait(false);

    server.DropDevice(deviceId); // cable pull mid-execution

    var result = await server.WaitForAsync(
        m => m.Type == "COMMAND_RESULT" && IdOf(m.Json) == job.CommandId, 120_000, "COMMAND_RESULT after reconnect (outbox flush)").ConfigureAwait(false);
    if (SimServer.GetString(result.Json, "status") != "SUCCESS")
        throw new TestFailureException($"mid-drop command failed: {SimServer.GetString(result.Json, "error")}");
    string? stdout = null;
    if (result.Json.TryGetProperty("result", out var r) && r.TryGetProperty("stdout", out var so)) stdout = so.GetString();
    if (stdout is null || !stdout.Contains("outbox-test")) throw new TestFailureException("unexpected stdout for mid-drop command");
});

await Scenario("dns-policy (CHECK_DNS read-only)", async () =>
{
    var job = server.EnqueueCommand(deviceId, "CHECK_DNS", new Dictionary<string, object?>
    {
        ["servers"] = new[] { "192.168.1.100" }
    });
    await DeliverAndWaitTerminalAsync(job).ConfigureAwait(false);
    var status = SimServer.GetString(job.FirstResult!.Value, "status");
    Console.WriteLine($"    CHECK_DNS status={status} (FAILED is expected on non-Windows dev machines)");
});

if (mutate)
{
    await Scenario("dns-policy (APPLY_DNS mutating)", async () =>
    {
        var job = server.EnqueueCommand(deviceId, "APPLY_DNS", new Dictionary<string, object?>
        {
            ["servers"] = new[] { "192.168.1.100" }
        });
        await DeliverAndWaitTerminalAsync(job, timeoutMs: 120_000).ConfigureAwait(false);
        Console.WriteLine($"    APPLY_DNS status={SimServer.GetString(job.FirstResult!.Value, "status")}");
    });
}

await Scenario("browser-policy (CHECK read-only)", async () =>
{
    var job = server.EnqueueCommand(deviceId, "CHECK_BROWSER_POLICY");
    await DeliverAndWaitTerminalAsync(job).ConfigureAwait(false);
    var status = SimServer.GetString(job.FirstResult!.Value, "status");
    Console.WriteLine($"    CHECK_BROWSER_POLICY status={status}");
});

if (mutate)
{
    await Scenario("browser-policy (APPLY + CHECK + REMOVE mutating)", async () =>
    {
        var apply = server.EnqueueCommand(deviceId, "APPLY_BROWSER_POLICY", new Dictionary<string, object?>
        {
            ["chrome_incognito"] = "disabled",
            ["edge_inprivate"] = "disabled"
        });
        await DeliverAndWaitTerminalAsync(apply, timeoutMs: 60_000).ConfigureAwait(false);
        Console.WriteLine($"    APPLY status={SimServer.GetString(apply.FirstResult!.Value, "status")}");

        var check = server.EnqueueCommand(deviceId, "CHECK_BROWSER_POLICY");
        await DeliverAndWaitTerminalAsync(check).ConfigureAwait(false);
        Console.WriteLine($"    CHECK report={SimServer.GetString(check.FirstResult!.Value, "result")}");

        var remove = server.EnqueueCommand(deviceId, "REMOVE_BROWSER_POLICY", new Dictionary<string, object?>
        {
            ["browsers"] = new[] { "chrome", "edge" }
        });
        await DeliverAndWaitTerminalAsync(remove).ConfigureAwait(false);
    });
}

await Scenario("application-install (nonexistent package → clean FAILED)", async () =>
{
    var job = server.EnqueueCommand(deviceId, "INSTALL_APP", new Dictionary<string, object?>
    {
        ["package_id"] = "LanAgent.Test.NonexistentPackage"
    });
    await DeliverAndWaitTerminalAsync(job, timeoutMs: 180_000).ConfigureAwait(false);
    string status = SimServer.GetString(job.FirstResult!.Value, "status") ?? "";
    if (status != "FAILED") throw new TestFailureException($"install of nonexistent package should FAIL, got {status}");
});

await Scenario("server-requested-sync (SYNC_REQUEST push)", async () =>
{
    server.PolicyVersion += 1;
    await server.SendAsync(deviceId, new Dictionary<string, object?>
    {
        ["v"] = 1, ["type"] = "SYNC_REQUEST", ["msg_id"] = Guid.NewGuid().ToString("D"),
        ["timestamp"] = SimServer.NowIso(), ["device_id"] = deviceId,
        ["reason"] = "admin", ["force"] = true
    }).ConfigureAwait(false);
    _ = await server.WaitForAsync(m => m.Type == "SYNC_STARTED" && m.DeviceId == deviceId, 30_000, "SYNC_STARTED", onlyNew: true).ConfigureAwait(false);
    _ = await server.WaitForAsync(m => m.Type == "SYNC_REQUEST" && m.DeviceId == deviceId, 30_000, "agent SYNC_REQUEST", onlyNew: true).ConfigureAwait(false);
    _ = await server.WaitForAsync(m => m.Type == "SYNC_RESULT" && m.DeviceId == deviceId, 60_000, "SYNC_RESULT", onlyNew: true).ConfigureAwait(false);
});

await Scenario("policy-changed (POLICY_CHANGED push)", async () =>
{
    server.PolicyVersion += 1;
    await server.SendAsync(deviceId, new Dictionary<string, object?>
    {
        ["v"] = 1, ["type"] = "POLICY_CHANGED", ["msg_id"] = Guid.NewGuid().ToString("D"),
        ["timestamp"] = SimServer.NowIso(),
        ["policy_version"] = server.PolicyVersion
    }).ConfigureAwait(false);
    _ = await server.WaitForAsync(m => m.Type == "SYNC_REQUEST" && m.DeviceId == deviceId, 30_000, "SYNC_REQUEST after POLICY_CHANGED", onlyNew: true).ConfigureAwait(false);
    _ = await server.WaitForAsync(m => m.Type == "SYNC_RESULT" && m.DeviceId == deviceId, 60_000, "SYNC_RESULT", onlyNew: true).ConfigureAwait(false);
});

await Scenario("ping-pong", async () =>
{
    string pingId = Guid.NewGuid().ToString("D");
    await server.SendAsync(deviceId, new Dictionary<string, object?>
    {
        ["v"] = 1, ["type"] = "PING", ["msg_id"] = pingId, ["timestamp"] = SimServer.NowIso()
    }).ConfigureAwait(false);
    var pong = await server.WaitForAsync(m => m.Type == "PONG", 15_000, "PONG").ConfigureAwait(false);
    if (SimServer.GetString(pong.Json, "pong_for") != pingId) throw new TestFailureException("pong_for mismatch");
});

await Scenario("get-state", async () =>
{
    await server.SendAsync(deviceId, new Dictionary<string, object?>
    {
        ["v"] = 1, ["type"] = "GET_STATE", ["msg_id"] = Guid.NewGuid().ToString("D"),
        ["timestamp"] = SimServer.NowIso(), ["device_id"] = deviceId
    }).ConfigureAwait(false);
    var report = await server.WaitForAsync(m => m.Type == "STATE_REPORT", 15_000, "STATE_REPORT").ConfigureAwait(false);
    foreach (string field in new[] { "hostname", "agent_version", "policy_version", "connection_state", "service_status" })
        if (!report.Json.TryGetProperty(field, out _))
            throw new TestFailureException($"STATE_REPORT missing '{field}'");
});

await Scenario("unknown-command-type → FAILED result", async () =>
{
    var job = server.EnqueueCommand(deviceId, "SOME_FUTURE_COMMAND_TYPE");
    await DeliverAndWaitTerminalAsync(job).ConfigureAwait(false);
    if (job.State != "FAILED") throw new TestFailureException($"unknown type must produce FAILED, got {job.State}");
});

// ------------------------------------------------------------------ summary

Console.WriteLine();
Console.WriteLine("=== RESULTS ===");
int failed = 0;
foreach (var (name, pass, note) in results)
{
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}{(string.IsNullOrEmpty(note) ? "" : $"  — {note}")}");
    if (!pass) failed++;
}
Console.WriteLine($"\n{results.Count - failed}/{results.Count} scenarios passed.");
Console.WriteLine("Harness keeps running (Ctrl+C to stop) so you can poke at the agent manually.");
await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
return failed;

internal sealed class TestFailureException : Exception
{
    public TestFailureException(string message) : base(message) { }
}
