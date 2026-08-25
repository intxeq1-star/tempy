# LAN MANAGEMENT — PROTOCOL CONTRACT (v1)

**This document is the complete, normative communication contract between the Windows Background Agent (AI #1) and Server.exe (AI #2).**

AI #2 must be able to implement Server.exe using ONLY this document. The Agent is implemented against this exact contract. Any behavior not specified here MUST NOT be assumed by either side.

- Contract version: `1`
- Agent implementation: `LanAgent.Core` / `LanAgent.Service` (see `src/`)
- Reference server simulator (implements the server side of this contract for testing): `tests/LanAgent.TestServer`

---

## 1. FIXED CONSTANTS (defaults)

| Constant              | Default value                        | Notes                                           |
|-----------------------|--------------------------------------|-------------------------------------------------|
| `SERVER_IP`           | `192.168.1.100`                      | Fixed on this LAN. Agent-configurable at install.|
| `SERVER_PORT`         | `8765`                               | TCP. Agent-configurable at install.              |
| `MANAGEMENT_BASE`     | `http://192.168.1.100:8765`          | Derived from the two values above.               |
| `WS_PATH`             | `/agent/ws`                          | WebSocket endpoint path.                         |
| `UPDATE_PATH_PREFIX`  | `/agent/update/`                     | HTTP download endpoint for agent updates.        |
| `HEALTH_PATH`         | `/health`                            | Optional HTTP diagnostics endpoint.              |
| `HEARTBEAT_INTERVAL`  | `15` seconds                         | Server may override per-device (see §7).         |
| `OFFLINE_TIMEOUT`     | `45` seconds (3 × heartbeat)         | Server marks device OFFLINE after this.          |
| `PING_TIMEOUT`        | `10` seconds                         | Agent must answer server `PING` within this.     |
| `CONNECT_TIMEOUT`     | `10` seconds                         | WebSocket open timeout (agent side).             |
| `AUTH_TIMEOUT`        | `15` seconds                         | Max wait for REGISTER_ACK / HELLO_ACK.           |
| `COMMAND_ACK_TIMEOUT` | `30` seconds (server-side duty)      | Server may resend un-acked COMMAND after this.   |
| `COMMAND_MAX_ATTEMPTS`| `5` (server-side duty)               | Delivery attempts per command.                   |
| `MAX_MESSAGE_BYTES`   | `1,048,576` (1 MiB)                  | Per WebSocket text frame. Larger = protocol error.|
| `PROTOCOL_VERSION`    | `1`                                  | Envelope field `v`.                              |

Both sides use configuration constants; nothing is scattered. TLS: transport may be upgraded to `wss://` (`UseTls=true`); the protocol is identical over `ws` and `wss`. Default deployment on the isolated management LAN is `ws://`.

---

## 2. TRANSPORT

1. **Primary transport: WebSocket** (RFC 6455). The Agent is always the client and initiates every connection.
   - URL: `ws://192.168.1.100:8765/agent/ws` (or `wss://…` when TLS is enabled).
   - No WebSocket subprotocol is negotiated; the path `/agent/ws` identifies the protocol.
   - Every WebSocket **text frame carries exactly one JSON message** (UTF-8, no BOM, no batching, no fragmentation requirements beyond RFC 6455).
2. **Secondary: plain HTTP on the same port** for:
   - `GET /agent/update/{version}/{fileName}` → `200` body = update package bytes (used only by `UPDATE_AGENT`). `404` if unknown.
   - `GET /health` → `200` `{"status":"ok","server_time":"…"}` (optional, diagnostics).
   - WebSocket upgrade requests arrive on the same port and path `/agent/ws`.
3. One persistent connection per device. The Agent maintains exactly one connection manager; the Server must accept one concurrent authenticated session per `device_id` and should close any older session when a new one authenticates (last-writer-wins).

---

## 3. JSON RULES

- Encoding UTF-8. Object keys are `snake_case`. One message per frame.
- Timestamps: ISO-8601 **with explicit numeric UTC offset**, local time preferred, e.g. `2026-08-25T18:30:00+05:30`. Receivers must accept any offset and normalize to UTC internally.
- UUIDs: canonical lowercase hyphenated form.
- Booleans are JSON booleans, numbers are JSON numbers. Enums are lowercase strings where stated.
- Unknown fields MUST be ignored by both sides (forward compatibility).
- Max frame size 1 MiB. Both sides close the connection with a protocol error on violation.

### 3.1 Common envelope (ALL messages, both directions)

```json
{
  "v": 1,
  "type": "COMMAND",
  "msg_id": "0d0ee2f7-9c30-4b86-8ba6-7db39bcbf6a6",
  "timestamp": "2026-08-25T18:30:00+05:30",
  "device_id": "550e8400-e29b-41d4-a716-446655440000"
}
```

| Field       | Type   | Meaning                                                        |
|-------------|--------|----------------------------------------------------------------|
| `v`         | int    | Protocol version. `1`.                                         |
| `type`      | string | Message type, uppercase (catalog in §5).                       |
| `msg_id`    | string | UUID, unique per message instance (tracing / dedup of pushes). |
| `timestamp` | string | Sender local time, ISO-8601 with offset.                       |
| `device_id` | string | Agent→Server: sender's device UUID. Server→Agent: the **target** device UUID (must match the connected agent; see `WRONG_DEVICE`). Omitted only on `POLICY_CHANGED` and `PING`, `ERROR` broadcasts. |

---

## 4. AUTHENTICATION & REGISTRATION

Model: **enrollment key (shared secret, provisioned at install) → per-device bearer token (issued by server, stored by agent)**.

1. **First connection ever** (no stored token): Agent sends `REGISTER` containing the enrollment key.
   - Server validates the enrollment key. On success replies `REGISTER_ACK` containing a fresh `device_token` (opaque string ≥ 32 chars; the server stores a hash of it). On failure replies `ERROR` with code `ENROLLMENT_REJECTED` or `AUTH_FAILED` and closes.
2. **Subsequent connections**: Agent sends `HELLO` with the stored token.
   - Valid → `HELLO_ACK` (session authenticated; device is ONLINE).
   - Invalid/expired (`AUTH_EXPIRED`, `AUTH_FAILED`) → Agent falls back to `REGISTER` (it still holds the enrollment key). Server closes the socket after an auth `ERROR` unless `fatal:false` is set.
3. Replay protection: `REGISTER` carries `nonce` + `timestamp`. The Server MUST reject `REGISTER` whose `timestamp` skews more than `±300s` from server time (`VALIDATION_ERROR`) and MUST reject a reused nonce within the skew window (`REPLAY_DETECTED`). `HELLO` replay is harmless (bearer token over a fresh TCP connection); the server closing the old session on new `HELLO` is the dedup.
4. Agent trusts **only** the configured `SERVER_IP:SERVER_PORT`. It never follows redirects and ignores any server-supplied alternate address.

Security level of this model: shared-key + bearer token over an isolated management LAN, optionally TLS. It is intentionally free of PKI, but everything is authenticated and message-validated.

---

## 5. MESSAGE CATALOG

Legend: **A→S** agent→server, **S→A** server→agent. Fields listed beyond the envelope. `*` = required.

### 5.1 A→S `REGISTER` (first connection / token recovery)

```json
{
  "v": 1, "type": "REGISTER", "msg_id": "…", "timestamp": "…",
  "device_id": "550e8400-…",
  "hostname": "PC-031",
  "os_version": "Windows 11 Pro 23H2 (build 22631.4317)",
  "os_build": "10.0.22631",
  "os_arch": "X64",
  "agent_version": "1.0.0",
  "local_ip": "192.168.1.31",
  "all_ips": ["192.168.1.31", "192.168.1.32"],
  "enrollment_key": "INSTALLER_PROVIDED_SECRET",
  "nonce": "9f1c2a4e-…",
  "capabilities": ["winget","dns","browser_policy","app_policy","power","admin_command","agent_update"]
}
```

### 5.2 S→A `REGISTER_ACK`

```json
{
  "v": 1, "type": "REGISTER_ACK", "msg_id": "…", "timestamp": "…", "device_id": "…",
  "device_token": "b7f3…opaque…≥32 chars",
  "server_time": "2026-08-25T18:30:01+05:30",
  "policy_version": 42,
  "heartbeat_interval_sec": 15
}
```
Agent stores `device_token` durably and immediately sends `HELLO` on the same connection? — **No.** `REGISTER_ACK` fully authenticates the session; the Agent proceeds to synchronization directly. `HELLO` is used only at the start of later connections.

### 5.3 A→S `HELLO` (every reconnect with existing token)

```json
{
  "v": 1, "type": "HELLO", "msg_id": "…", "timestamp": "…", "device_id": "…",
  "device_token": "b7f3…stored bearer token…",
  "agent_version": "1.0.0",
  "policy_version": 49,
  "last_sync_at": "2026-08-25T17:58:03+05:30",
  "pending_results_count": 2,
  "capabilities": ["winget","dns","browser_policy","app_policy","power","admin_command","agent_update"]
}
```
`device_token` authenticates the session (bearer token issued by `REGISTER_ACK`). Invalid/expired token ⇒ `ERROR` `AUTH_EXPIRED`/`AUTH_FAILED` (agent then falls back to `REGISTER`).
`pending_results_count`: results the agent still owes (outbox) — server may use it for UI/reconciliation.

### 5.4 S→A `HELLO_ACK`

```json
{
  "v": 1, "type": "HELLO_ACK", "msg_id": "…", "timestamp": "…", "device_id": "…",
  "server_time": "2026-08-25T18:30:01+05:30",
  "policy_version": 51,
  "heartbeat_interval_sec": 15,
  "commands_pending": 3
}
```
`commands_pending` is informational (agent will fetch via `GET_PENDING_COMMANDS` regardless).

### 5.5 A→S `HEARTBEAT` (every `heartbeat_interval_sec` while session is up)

```json
{
  "v": 1, "type": "HEARTBEAT", "msg_id": "…", "timestamp": "2026-08-25T18:30:00+05:30",
  "device_id": "…",
  "agent_version": "1.0.0",
  "policy_version": 49,
  "state": "READY",
  "hostname": "PC-031",
  "local_ip": "192.168.1.31"
}
```
`state` is one of the agent connection states (§9) — diagnostic only; **the server remains authoritative for presence.**

### 5.6 S→A `PING`

```json
{ "v": 1, "type": "PING", "msg_id": "…", "timestamp": "…" }
```
Agent replies `PONG` within 10 s. Optional for the server (WebSocket-level liveness is also acceptable).

### 5.7 A→S `PONG`

```json
{ "v": 1, "type": "PONG", "msg_id": "…", "timestamp": "…", "device_id": "…", "pong_for": "<msg_id of PING>" }
```

### 5.8 A→S `SYNC_REQUEST`

```json
{
  "v": 1, "type": "SYNC_REQUEST", "msg_id": "…", "timestamp": "…", "device_id": "…",
  "current_policy_version": 49,
  "reason": "reconnect"            // boot|first_install|reconnect|periodic|server_push|admin|policy_changed
}
```

### 5.9 S→A `SYNC_PAYLOAD`

```json
{
  "v": 1, "type": "SYNC_PAYLOAD", "msg_id": "…", "timestamp": "…", "device_id": "…",
  "policy_version": 51,
  "issued_at": "2026-08-25T18:30:05+05:30",
  "desired_state": { …see §6… }
}
```
Server MUST send this in response to every `SYNC_REQUEST` (even if unchanged — then `policy_version` equals the agent's current and the agent applies nothing and reports `NO_CHANGE`). Only one in-flight `SYNC_PAYLOAD` per connection.

### 5.10 A→S `SYNC_STARTED`

```json
{
  "v": 1, "type": "SYNC_STARTED", "msg_id": "…", "timestamp": "…", "device_id": "…",
  "reason": "admin",
  "current_policy_version": 49
}
```
Sent whenever a synchronization run begins. Mandatory when triggered by a server `SYNC_REQUEST` or `POLICY_CHANGED`.

### 5.11 A→S `SYNC_RESULT`

```json
{
  "v": 1, "type": "SYNC_RESULT", "msg_id": "…", "timestamp": "…", "device_id": "…",
  "policy_version": 51,
  "previous_policy_version": 49,
  "status": "SUCCESS",                       // SUCCESS|PARTIAL|FAILED|NO_CHANGE
  "applied": [
    { "item": "dns",   "status": "SUCCESS", "detail": "LAN: 192.168.1.100 (was 1.1.1.1)" },
    { "item": "browser_policy.chrome.incognito", "status": "SUCCESS", "detail": "disabled" },
    { "item": "apps.Microsoft.VisualStudioCode", "status": "FAILED", "detail": "winget exit 0x8A15002B" }
  ],
  "error": null
}
```
`applied` has one entry per reconciled section. `status` semantics: `SUCCESS` = all sections ok; `PARTIAL` = ≥1 section failed; `FAILED` = sync could not run; `NO_CHANGE` = nothing to apply.

### 5.12 S→A `COMMAND`

```json
{
  "v": 1, "type": "COMMAND", "msg_id": "…", "timestamp": "…", "device_id": "550e8400-…",
  "command_id": "0a1b2c3d-…",               // UUID — GLOBALLY UNIQUE, the idempotency key
  "command_type": "GET_SYSTEM_INFO",
  "payload": { },
  "created_at": "2026-08-25T18:29:00+05:30",
  "attempt": 1,
  "timeout_sec": 600,
  "requires_result": true
}
```
- `device_id` MUST be the connected agent's ID; agent replies `ERROR`/`WRONG_DEVICE` and ignores otherwise.
- `attempt` starts at 1 and increments on each server redelivery.
- `timeout_sec` optional (agent clamps to its configured `AdminCommandMaxTimeoutSeconds` for `RUN_ADMIN_COMMAND`; other types use type defaults).
- `requires_result=false` is allowed for fire-and-forget types (`RESTART_PC`, `SHUTDOWN_PC`) — agent still acks `COMMAND_RECEIVED` but may not send a final result; server then completes the job on `RECEIVED`. Default `true`.

### 5.13 A→S `COMMAND_RECEIVED` (immediate, before execution)

```json
{
  "v": 1, "type": "COMMAND_RECEIVED", "msg_id": "…", "timestamp": "…", "device_id": "…",
  "command_id": "0a1b2c3d-…",
  "received_at": "2026-08-25T18:30:06+05:30",
  "duplicate": false
}
```
`duplicate:true` when the agent had already seen this `command_id` (redelivery). Server moves job `SENT → RECEIVED` on this message.

### 5.14 A→S `COMMAND_STATUS` (execution started)

```json
{
  "v": 1, "type": "COMMAND_STATUS", "msg_id": "…", "timestamp": "…", "device_id": "…",
  "command_id": "0a1b2c3d-…",
  "status": "RUNNING",
  "started_at": "2026-08-25T18:30:06+05:30"
}
```
Sent once, right before execution begins. Server moves job `RECEIVED → RUNNING`.

### 5.15 A→S `COMMAND_RESULT` (final, exactly one per command)

```json
{
  "v": 1, "type": "COMMAND_RESULT", "msg_id": "…", "timestamp": "…", "device_id": "…",
  "command_id": "0a1b2c3d-…",
  "status": "SUCCESS",                        // SUCCESS|FAILED
  "exit_code": 0,
  "result": { },
  "error": null,
  "started_at": "2026-08-25T18:30:06+05:30",
  "completed_at": "2026-08-25T18:30:07+05:30",
  "duration_ms": 812,
  "attempt": 1,
  "duplicate": false
}
```
- The agent persists this message durably **before** sending, and re-sends it after every reconnect until the server acknowledges it (§8.4).
- `duplicate:true` marks a replayed stored result (e.g. server asked for a completed command again). Server must not re-execute anything on receipt; the **first terminal result wins** — server upserts only if the job is not already terminal, and must still send `COMMAND_RESULT_ACK`.
- `result` is a JSON object whose schema depends on `command_type` (§10). `error` is a human-readable string or `null`.

### 5.16 S→A `COMMAND_RESULT_ACK`

```json
{
  "v": 1, "type": "COMMAND_RESULT_ACK", "msg_id": "…", "timestamp": "…", "device_id": "…",
  "command_id": "0a1b2c3d-…",
  "recorded": true
}
```
Server MUST send this after durably recording a terminal `COMMAND_RESULT`. On receipt the agent deletes its outbox copy. Absence of the ack ⇒ agent retries the result after the next reconnect.

### 5.17 A→S `GET_PENDING_COMMANDS`

```json
{
  "v": 1, "type": "GET_PENDING_COMMANDS", "msg_id": "…", "timestamp": "…", "device_id": "…",
  "known_completed": ["0a1b…", "77de…"]
}
```
Sent by the agent: on every session establishment (after `REGISTER_ACK`/`HELLO_ACK`), after each successful sync, and after it finishes draining its outbox. `known_completed` = up to 200 most-recent `command_id`s the agent has terminal results for — the server may use it to prune redeliveries (it MUST still deliver commands not in this list and not terminal).

**Server response:** zero or more `COMMAND` messages on the same connection (in `created_at` order). If none, send nothing (or an `ERROR`-free empty treatment — no explicit "empty" message exists).

### 5.18 S→A `SYNC_REQUEST`

```json
{
  "v": 1, "type": "SYNC_REQUEST", "msg_id": "…", "timestamp": "…", "device_id": "…",
  "reason": "admin",          // admin|config_changed
  "force": true
}
```
Server-ordered immediate synchronization ("administrator clicked SYNC"). Agent replies `SYNC_STARTED`, performs the full `SYNC_REQUEST`→`SYNC_PAYLOAD`→apply→verify cycle (§5.8–5.11), i.e. it issues its own `SYNC_REQUEST` to fetch the payload.

### 5.19 S→A `POLICY_CHANGED` (push notification, advisory)

```json
{
  "v": 1, "type": "POLICY_CHANGED", "msg_id": "…", "timestamp": "…",
  "policy_version": 51
}
```
Agent schedules a synchronization shortly after (debounced 2 s). Push is an optimization only — periodic reconciliation (§11) is mandatory on both sides.

### 5.20 S→A `GET_STATE`

```json
{ "v": 1, "type": "GET_STATE", "msg_id": "…", "timestamp": "…", "device_id": "…" }
```

### 5.21 A→S `STATE_REPORT` (reply to `GET_STATE`)

```json
{
  "v": 1, "type": "STATE_REPORT", "msg_id": "…", "timestamp": "…", "device_id": "…",
  "hostname": "PC-031",
  "os_version": "Windows 11 Pro 23H2 (build 22631.4317)",
  "agent_version": "1.0.0",
  "local_ip": "192.168.1.31",
  "all_ips": ["192.168.1.31"],
  "service_status": "RUNNING",
  "connection_state": "READY",
  "policy_version": 51,
  "last_successful_sync": "2026-08-25T18:30:07+05:30",
  "agent_started_at": "2026-08-25T08:00:00+05:30",
  "commands_running": ["c3d4…"],
  "pending_results": 0
}
```

### 5.22 `ERROR` (both directions)

```json
{
  "v": 1, "type": "ERROR", "msg_id": "…", "timestamp": "…", "device_id": "…",
  "code": "VALIDATION_ERROR",        // see below
  "message": "human readable",
  "msg_id_ref": "<msg_id of offending message>",
  "fatal": false
}
```
Codes: `AUTH_FAILED`, `AUTH_EXPIRED`, `ENROLLMENT_REJECTED`, `REPLAY_DETECTED`, `VALIDATION_ERROR`, `PROTOCOL_ERROR`, `UNKNOWN_MESSAGE`, `WRONG_DEVICE`, `MESSAGE_TOO_LARGE`, `RATE_LIMITED`, `INTERNAL_ERROR`, `SERVER_SHUTTING_DOWN`, `UNSUPPORTED_TYPE`.
`fatal:true` (auth/protocol) → sender will close/reject the connection. Agents treat any fatal auth error as "back off ≥60 s" unless token recovery via `REGISTER` is possible.

---

## 6. DESIRED STATE SCHEMA (`SYNC_PAYLOAD.desired_state`)

Every section is OPTIONAL. Missing section ⇒ agent leaves that area untouched (reports nothing for it). Empty object `{}` ⇒ policy version bump with nothing to do.

```json
{
  "dns": {
    "enabled": true,
    "servers": ["192.168.1.100"],
    "apply_to_wireless": false,
    "reset_to_dhcp": false
  },
  "browser_policy": {
    "chrome": { "incognito": "disabled" },   // enabled|disabled|forced|unmanaged
    "edge":   { "inprivate": "disabled" }    // enabled|disabled|forced|unmanaged
  },
  "app_policy": {
    "mode": "applocker",                     // none|applocker
    "rules": [ { "path": "*\\Tor Browser\\*.exe", "action": "deny" } ]   // action: deny|allow
  },
  "apps": [
    { "package_id": "Microsoft.VisualStudioCode", "state": "installed" } // installed|absent
  ]
}
```

Semantics per section (all idempotent — re-applying current state is a no-op and reports `SUCCESS`):

- **dns** — set the listed servers as static DNS on relevant **physical LAN adapters** (Ethernet by default; wireless only if `apply_to_wireless:true`). Virtual adapters (Hyper-V, VMware, VirtualBox, WSL, loopback, TAP, VPN) are never touched. `reset_to_dhcp:true` returns DNS to DHCP ("obtain automatically"). Verification: read back effective DNS and compare.
- **browser_policy** — Chrome `IncognitoModeAvailability` and Edge `InPrivateModeAvailability` registry policies (HKLM): `enabled`→delete value (0 also acceptable), `disabled`→1, `forced`→2, `unmanaged`→delete policy key. Verified by reading back.
- **app_policy** — `none` removes agent-managed AppLocker policy; `applocker` applies the rule list via `Set-AppLockerPolicy` (documented Windows mechanism; requires Enterprise/Education editions — otherwise the item reports `FAILED` with a clear detail).
- **apps** — winget package reconciliation: `installed` installs if missing, `absent` uninstalls if present.

---

## 7. HEARTBEAT / ONLINE-OFFLINE RULES (server-authoritative presence)

1. Agent sends `HEARTBEAT` every `heartbeat_interval_sec` (default 15 s) on the authenticated session. The interval may be overridden by the server via `REGISTER_ACK`/`HELLO_ACK` (`heartbeat_interval_sec`, clamped by the agent to 5–300 s).
2. **Server marks a device ONLINE** when: a session authenticates (`REGISTER_ACK` or `HELLO_ACK` sent).
3. **Server marks a device OFFLINE** when: the WebSocket closes/fails, **or** no `HEARTBEAT` has been received within `OFFLINE_TIMEOUT` (default 45 s) — whichever happens first.
4. On reconnect the `HELLO`/`REGISTER` handshake immediately flips the device ONLINE again.
5. The agent never asserts its own online status; it only feeds evidence (heartbeats + open socket). Presence is computed server-side.
6. During the whole session the agent also answers `PING` with `PONG` ≤ 10 s (extra liveness evidence).

---

## 8. RELIABILITY RULES (durable commands, retries, idempotency)

### 8.1 Server-side job lifecycle (SERVER DUTY — mandatory)

```
PENDING ──(COMMAND sent)──► SENT ──(COMMAND_RECEIVED)──► RECEIVED ──(COMMAND_STATUS RUNNING)──► RUNNING ──(COMMAND_RESULT)──► SUCCESS | FAILED
```

- Every admin action = durable job row (survives server restart), keyed by `command_id` UUID.
- "Run on all 89 PCs" ⇒ 89 rows, one per `device_id`. There is NO broadcast/multicast message in this protocol.
- Terminal states (`SUCCESS`/`FAILED`) are immutable: the first terminal `COMMAND_RESULT` wins; later duplicates are acknowledged but do not overwrite.
- A job targeted at an offline PC simply stays `PENDING` and is delivered when the agent reconnects (`GET_PENDING_COMMANDS`). **OFFLINE ≠ LOST.**

### 8.2 Server redelivery (SERVER DUTY)

- If `COMMAND` is sent and no `COMMAND_RECEIVED` arrives within `COMMAND_ACK_TIMEOUT` (30 s) **and the session is still up**, resend the same `command_id` with `attempt+1`, up to `COMMAND_MAX_ATTEMPTS` (5). After that leave the job `SENT`; it will be re-fetched by `GET_PENDING_COMMANDS` after the next reconnect.
- On session close, un-terminal jobs return to deliverable state (as if `PENDING`).

### 8.3 Agent idempotency (AGENT DUTY)

- `command_id` is the idempotency key. The agent keeps a local durable history.
- Receiving a `command_id` that is terminal (`SUCCESS`/`FAILED`) locally ⇒ do NOT execute; replay the stored `COMMAND_RESULT` with `duplicate:true`.
- Receiving a `command_id` currently `RUNNING` ⇒ ignore the redelivery (log only; still send `COMMAND_RECEIVED` with `duplicate:true`).
- Idempotent command implementations: app install/uninstall/update check current state first; DNS/browser policy application verifies desired == actual before changing anything.

### 8.4 Result durability (AGENT DUTY / SERVER DUTY)

- Agent persists `COMMAND_RESULT` in a local outbox **before** sending, deletes it only upon `COMMAND_RESULT_ACK` (`recorded:true`).
- Outbox is flushed: immediately, after every reconnect when the session becomes READY, and after every successful sync.
- Server upserts by `command_id` (first-terminal-wins) and always acks.

### 8.5 Agent reconnect behavior (AGENT DUTY)

```
DISCONNECTED → CONNECTING → AUTHENTICATING → CONNECTED → SYNCHRONIZING → READY
                                     ▲            │ fault/close           │
                                     └── RECONNECTING ◄─────────────────────┘
```

- Exponential backoff: 2 s initial, ×2 per failure, cap 60 s, ±25% jitter; reset after 60 s of stable connection. Never more than one connection attempt loop per agent.
- After each (re)connect: authenticate → compare `policy_version` → sync if stale (or always on reconnect) → flush result outbox → `GET_PENDING_COMMANDS` → execute → report → READY.
- Heartbeats resume immediately at READY.

---

## 9. AGENT CONNECTION STATES

`DISCONNECTED`, `CONNECTING`, `AUTHENTICATING`, `CONNECTED`, `SYNCHRONIZING`, `READY`, `RECONNECTING`.

- `DISCONNECTED` — service starting or stopped.
- `CONNECTING` — TCP/WebSocket open in progress.
- `AUTHENTICATING` — `REGISTER`/`HELLO` sent, waiting for ack.
- `CONNECTED` — ack received, session established.
- `SYNCHRONIZING` — policy reconciliation + outbox flush + pending fetch in progress.
- `READY` — steady state: heartbeats flowing, commands being accepted.
- `RECONNECTING` — waiting out the backoff after a fault; then `CONNECTING` again.

---

## 10. COMMAND TYPES AND RESULT SCHEMAS

All commands share the envelope of §5.12. `payload`/`result` schemas per type (`result` object may additionally carry `notes`). "Exit code" is a logical exit code (0 = ok) — not necessarily an OS process code, though for winget/process commands it mirrors the process exit code.

| Type | Payload | Result | Notes |
|---|---|---|---|
| `GET_SYSTEM_INFO` | `{}` | `{ "hostname", "os_version", "os_build", "os_arch", "agent_version", "uptime_sec", "cpu", "ram_total_mb", "ram_free_mb", "drives":[{"name","total_gb","free_gb"}], "local_ips":[…] , "machine_guid" }` | Read-only. |
| `GET_INSTALLED_APPS` | `{ "package_id": null }` | `{ "apps": [ {"name","version","publisher","source"} ], "count": n }` | Merges registry uninstall keys + winget list when available. Optional `package_id` filters. |
| `INSTALL_APP` | `{ "package_id": "Microsoft.VisualStudioCode", "scope": "machine" }` | `{ "package_id", "already_installed": bool, "installed_version", "winget_exit_code", "stdout_tail", "stderr_tail" }` | winget `install -e --id … --silent --accept-package-agreements --accept-source-agreements --scope machine`. Checks installed first (idempotent). |
| `UNINSTALL_APP` | `{ "package_id" }` | same shape (`already_installed:false` on success) | winget `uninstall -e --id … --silent`. Absent ⇒ SUCCESS no-op. |
| `UPDATE_APP` | `{ "package_id" }` | `{ "package_id", "was_up_to_date": bool, "installed_version", … }` | winget `upgrade -e --id …`. Up-to-date ⇒ SUCCESS no-op. |
| `CHECK_APP` | `{ "package_id" }` | `{ "package_id", "installed": bool, "version": "…" \| null }` | winget/registry lookup. |
| `APPLY_DNS` | `{ "servers": ["192.168.1.100"], "apply_to_wireless": false, "reset_to_dhcp": false }` | `{ "adapters": [ {"name","status":"SUCCESS","before":[…],"after":[…]} ], "changed": n }` | Defaults from policy when payload empty. Physical adapters only; verifies read-back. |
| `CHECK_DNS` | `{}` | `{ "adapters": [ {"name","dns_servers":[…],"compliant": bool} ], "expected": ["192.168.1.100"] }` | Read-only. |
| `APPLY_BROWSER_POLICY` | `{ "chrome_incognito": "disabled", "edge_inprivate": "disabled" }` | `{ "chrome": {"policy","value","applied"}, "edge": {"policy","value","applied"} }` | Registry policies; `unmanaged` removes. Empty payload ⇒ apply current desired state. |
| `CHECK_BROWSER_POLICY` | `{}` | `{ "chrome": {"incognito": "disabled"\|"enabled"\|"forced"\|"unmanaged"}, "edge": {…} }` | Read-only. |
| `REMOVE_BROWSER_POLICY` | `{ "browsers": ["chrome","edge"] }` | `{ "removed": ["chrome.incognito","edge.inprivate"] }` | Deletes the agent-managed policy values. |
| `APPLY_APP_POLICY` | `{ "mode": "applocker"\|"none", "rules": [ {"path","action"} ] }` | `{ "mode", "rules_applied": n, "detail" }` | `Set-AppLockerPolicy`. Unsupported edition ⇒ FAILED with explanatory error. |
| `CHECK_APP_POLICY` | `{}` | `{ "mode": "applocker"\|"none"\|"unknown", "rules": [ {"path","action"} ], "detail" }` | Reads effective policy. |
| `SYNC_POLICY` | `{ "force": true }` | `{ "policy_version", "status", "applied": [ … ] }` | Result mirrors the `SYNC_RESULT` of the triggered sync. |
| `RESTART_AGENT` | `{}` | `{ "restart_initiated": true }` | Agent restarts its Windows service. Result is sent BEFORE the restart. |
| `RESTART_PC` | `{ "delay_sec": 10, "message": "…" }` | `{ "scheduled": true, "delay_sec": 10 }` | `shutdown /r /t n`. Result sent before scheduling so it is not lost. |
| `SHUTDOWN_PC` | `{ "delay_sec": 10, "message": "…" }` | `{ "scheduled": true, "delay_sec": 10 }` | `shutdown /s /t n`. |
| `LOCK_PC` | `{}` | `{ "locked": true, "method": "WTSDisconnectSession" }` | Disconnects the active console session (locks the workstation). |
| `LOGOFF_USER` | `{}` | `{ "sessions_logged_off": n }` | Logs off interactive sessions via WTS. |
| `RUN_ADMIN_COMMAND` | `{ "command": "cmd.exe /c dir", "timeout_sec": 120, "working_dir": null }` | `{ "stdout", "stderr", "exit_code", "started_at", "completed_at", "duration_ms" }` | Runs in the service's administrative context. Timeout enforced (kill). Output capped at 256 KB each stream. Optional agent-side executable allowlist. |
| `UPDATE_AGENT` | `{ "version": "1.1.0", "file_name": "lanagent-1.1.0.zip", "sha256": "…hex…", "signature": "…optional RSA-SHA256 detached base64…" }` | `{ "staged": true, "version", "verified": true, "applying": true }` | Download from `GET {UPDATE_PATH_PREFIX}{version}/{file_name}`, verify SHA-256 (+RSA signature when the agent has the server public key configured), stage, then restart the service to swap binaries. Device identity/state preserved. Result sent before the swap. |

Unknown `command_type` ⇒ agent replies `COMMAND_RESULT` with `status:"FAILED"`, `error:"UNSUPPORTED_TYPE"`.

---

## 11. SYNCHRONIZATION TRIGGERS (both sides)

Agent synchronizes (`SYNC_REQUEST` cycle): after first registration; after service boot; after every reconnect; on server `SYNC_REQUEST`; on `POLICY_CHANGED` (debounced 2 s); on `SYNC_POLICY` command; **and periodically every `SyncIntervalSeconds` (default 300 s) as safety reconciliation**. Push-only is never assumed.

Server duties: monotonic `policy_version` per deployment (or per device — either is valid as long as it only moves forward); respond to every `SYNC_REQUEST`; record agent `policy_version` from heartbeats.

---

## 12. SEQUENCE EXAMPLES

### 12.1 Cold boot of PC-031 with a pending command

```
Agent(start) → CONNECTING → ws connect → AUTHENTICATING: HELLO ─────────►
                                                ◄──────── HELLO_ACK(policy_version=51, commands_pending=1)
                              CONNECTED
   SYNC_REQUEST(current=49, reason=boot) ─────►
   ◄────── SYNC_PAYLOAD(policy_version=51, desired_state{…})
   apply+verify … SYNC_RESULT(51, SUCCESS) ───►                     SYNCHRONIZING
   flush outbox (empty) … GET_PENDING_COMMANDS(known_completed=[…]) ►
   ◄────── COMMAND(command_id=c-031-1, RUN_ADMIN_COMMAND, attempt=1)
   COMMAND_RECEIVED(c-031-1) ─────────────────►
   COMMAND_STATUS(c-031-1, RUNNING) ─────────►
   …execute…
   COMMAND_RESULT(c-031-1, SUCCESS, …) ───────►
   ◄────── COMMAND_RESULT_ACK(c-031-1)                            READY
   HEARTBEAT every 15 s ►
```

### 12.2 Network drops mid-command, result survives

```
… COMMAND(c-9) received, RECEIVED+RUNNING sent, execution finishes just as LAN drops
   COMMAND_RESULT(c-9) send FAILS → persisted in local outbox
   [RECONNECTING with backoff] … link restored … CONNECTING → HELLO/HELLO_ACK
   → SYNCHRONIZING → outbox flush: COMMAND_RESULT(c-9, duplicate=false) ──►
   ◄── COMMAND_RESULT_ACK(c-9) → READY
```
Server sees RECEIVED→RUNNING before the drop and the final result after; `c-9` completes. If the drop happened before `COMMAND_RECEIVED`, the server's 30 s ack timer or the post-reconnect `GET_PENDING_COMMANDS` redelivers; agent executes once (idempotent).

### 12.3 Duplicate delivery

```
Server sends COMMAND(c-7) twice (attempt 1 and 2; first ack lost).
First arrival: RECEIVED → RUNNING → SUCCESS result.
Second arrival: already terminal ⇒ COMMAND_RECEIVED(duplicate=true),
                COMMAND_RESULT(c-7, duplicate=true, <stored result>) — no re-execution.
```

---

## 13. API ENDPOINT SUMMARY

| What | Where | Method |
|---|---|---|
| Agent WebSocket | `ws(s)://SERVER_IP:SERVER_PORT/agent/ws` | WebSocket upgrade |
| Agent update package | `http(s)://SERVER_IP:SERVER_PORT/agent/update/{version}/{fileName}` | GET |
| Health (optional) | `http(s)://SERVER_IP:SERVER_PORT/health` | GET |

All management traffic (registration, heartbeat, sync, commands, results) flows over the single WebSocket. No other endpoints exist in v1.

---

## 14. TEST MATRIX (implemented by `tests/LanAgent.TestServer`)

The reference test harness (simulated server) validates, over the real WebSocket protocol: registration; heartbeat cadence; disconnect/reconnect with backoff; synchronization; one-PC command; server redelivery; duplicate command idempotency; offline→pending→boot→execute→SUCCESS; result survival across network drop; DNS policy round trip; browser policy apply/check/remove; app install via winget; `RUN_ADMIN_COMMAND` capture; `SYNC_REQUEST` push; `PING/PONG`.

---

**END OF CONTRACT v1.** Anything not written here does not exist in the protocol.
