# LanAgent Architecture

## Components

```
LanAgent.Service (Windows Service host, .NET Worker Service)
└── AgentEngine (composition root; ISessionCoordinator + ISessionFacts + IHeartbeatInfo)
    ├── ConnectionManager        ← THE single owner of the server connection
    │   └── ConnectionSession    ← live socket + cancellable scope + serialized sends
    │       └── WebSocketTransport (ClientWebSocket, keep-alive pings, 1 MiB frame cap)
    ├── SynchronizationEngine    ← SYNC_REQUEST→SYNC_PAYLOAD→apply→verify→SYNC_RESULT
    │   └── DesiredStateApplier  ← dns / browser_policy / app_policy / apps sections
    ├── CommandService           ← ack, dedupe, execute, durable result outbox, recovery
    │   └── 21 ICommandHandler(s) → management integrations:
    │       ├── WingetClient        (winget.exe facade, idempotent)
    │       ├── DnsManager          (adapter selection + netsh + read-back verify)
    │       ├── BrowserPolicyManager(HKLM Chrome/Edge policy values)
    │       ├── AppPolicyManager    (AppLocker via PowerShell cmdlets)
    │       ├── PowerController     (shutdown.exe / WTS APIs)
    │       ├── SystemInfoCollector (registry, WTS-free, cross-platform safe)
    │       └── AgentUpdater        (download→verify→stage→swap via detached helper)
    ├── SqliteStateStore         ← meta / command_history / result_outbox / policy_state
    ├── DeviceIdentityService    ← permanent UUID + device token
    └── RollingFileLog           ← structured JSON lines, rotation, retention
```

## Connection lifecycle (one loop, no orphans)

```
RunAsync(stop)
 └─ CONNECTING ── ws connect (10s timeout)
     ├─ fail → RECONNECTING → backoff(2s,x2,±25%,cap60s) → retry
     └─ ok → AUTHENTICATING
          ├─ token exists → HELLO ── HELLO_ACK ─┐            (no token → REGISTER → REGISTER_ACK)
          ├─ AUTH_EXPIRED/FAILED → REGISTER ────┤
          └─ fail/timeout → RECONNECTING        ▼
        CONNECTED → SYNCHRONIZING (engine: sync → outbox flush → GET_PENDING_COMMANDS)
          → READY (heartbeat loop starts immediately; WebSocket keep-alive detects dead peers)
          → any fault/close → session scope cancelled → RECONNECTING → backoff → …
```

- Backoff resets after 60 s of stable connection.
- Only `ConnectionManager` creates transports; every other component sends through
  `SendAsync` best-effort and relies on the durability layers when it returns false.

## Command durability model (agent side of contract §8)

```
COMMAND arrives
 → validate target device_id (WRONG_DEVICE otherwise)
 → persist history=RECEIVED, send COMMAND_RECEIVED
 → already terminal?  → replay stored result (duplicate=true), no execution
 → already running?    → ignore redelivery
 → else enqueue (bounded channel, concurrency 1 by default)
 → history=RUNNING, send COMMAND_STATUS RUNNING
 → handler executes (timeout, output caps)
 → persist terminal state + result JSON + SAVE OUTBOX (durable BEFORE send)
 → send COMMAND_RESULT … on every reconnect until COMMAND_RESULT_ACK(recorded=true)
```

Crash recovery at service start: history rows stuck in RECEIVED/RUNNING are marked FAILED
("interrupted by agent restart; not re-executed automatically") and reported via the outbox —
the server/administrator decides whether to re-issue. Nothing dangerous is blindly re-run.

## Synchronization semantics

- Versions are monotonic per deployment. The agent applies a payload when the server version
  differs from the stored version (or on `force`).
- `PARTIAL` application (some sections failed) intentionally does **not** advance the stored
  policy version, so periodic reconciliation retries the failed sections.
- Triggers: boot, reconnect, `POLICY_CHANGED` (2 s debounce), server `SYNC_REQUEST`,
  `SYNC_POLICY` command, periodic timer (300 s default).

## Threading model

- `ConnectionManager.RunAsync`: one supervised loop per process.
- Receive loop dispatches to an unbounded channel consumed by a single dispatcher task;
  handlers run as fire-and-forget tasks so slow commands never block heartbeat/PONG paths.
- `CommandService` workers (1 by default) serialize command execution.
- SQLite access is serialized by a semaphore (WAL mode).
- Every long-running scope is tied to a CancellationTokenSource cancelled on session end or
  service stop — no orphaned loops.

## Local state (C:\ProgramData\LanAgent, ACL: Administrators+SYSTEM)

| File | Contents |
|---|---|
| `state.db` | meta (device_id, device_token, …), command_history, result_outbox, policy_state |
| `device-id.txt` | human-readable mirror of the device UUID |
| `logs/` | structured rolling logs |
| `updates/<version>/` | staged agent update packages + apply-update.cmd |

## Update flow (UPDATE_AGENT)

1. Download from the trusted server only (`/agent/update/{version}/{file}`).
2. SHA-256 required; RSA signature required when `AgentUpdatePublicKey` is configured.
3. Stage under ProgramData; write `apply-update.cmd` (net stop → robocopy /MIR (excluding
   appsettings/logs) → net start); spawn detached; report SUCCESS before the swap.
4. Device identity/state live in ProgramData and survive the swap; the agent reconnects and
   reports its new version.
