# Tempy LAN Agent ↔ Server Protocol Contract — v1.0

> **Normative contract.** This document is the complete wire contract implemented by `Server.exe`.
> An Agent written in any language is compatible when it implements this document exactly. In a
> conflict, the literal field names, field types, messages, and state rules below win.

## 1. Endpoint, transport, and framing

| Item | Contract |
| --- | --- |
| Default management address | `192.168.1.100` |
| Default port | `8765` |
| Agent endpoint path | `/agent/ws` |
| Default development/LAN URI | `ws://192.168.1.100:8765/agent/ws` |
| Production URI | `wss://192.168.1.100:8765/agent/ws` |
| Transport | One persistent RFC 6455 WebSocket per Agent connection, UTF-8 text messages only |
| Framing | **Exactly one JSON object per WebSocket message**. Do not concatenate JSON or use newline framing. |
| Maximum message size | `262144` UTF-8 bytes by default; supplied in `REGISTERED.server_configuration.maximum_message_bytes` |
| Protocol version | String exactly `"1.0"` |

`192.168.1.100` and `8765` are the server defaults. The deployment may change them in Server
configuration; an Agent must treat `REGISTERED.server_configuration.server_endpoint` as the
canonical reconnect endpoint after a successful registration.

The Server supports plain `ws` for an isolated bootstrap LAN because a new installation may not
yet have a certificate. Production deployments **MUST** use `wss` with a certificate trusted by
Agents. A bearer token is never safe over an untrusted plain-WebSocket network.

## 2. Common conventions

- Every timestamp is a JSON string in ISO-8601 / RFC 3339 form in UTC, for example
  `"2026-08-25T18:31:20.123Z"`.
- Every `message_id`, `request_id`, and `command_id` is a UUID string. Send a fresh `message_id`
  for each transmission. `command_id` is allocated by the Server and stays immutable forever.
- `device_id` is a case-sensitive authoritative string, 1–128 characters, matching
  `[A-Za-z0-9._-]+`. It is **not** an IP address. Examples: `PC-001`, `a3f1d...`.
- JSON property names are lowercase `snake_case`, exactly as shown. Unknown properties may be
  ignored; required properties may not be omitted or changed in type.
- All command `payload` and `result_data` values are JSON objects unless an explicitly allowed
  array is stated below. Numbers are JSON numbers, never numeric strings.
- An Agent opens a new socket after a disconnect and sends `REGISTER_DEVICE` again before any
  other message. The Server has no unauthenticated command endpoint.

## 3. Authentication and registration

The first message on every new WebSocket **MUST** be `REGISTER_DEVICE`. `auth_token` is sent only
in this message; after registration, the WebSocket session is authenticated as that `device_id`.

The Server accepts either:

1. a pre-provisioned per-device token whose SHA-256 digest is stored for that device; or
2. when the administrator explicitly enables enrollment, the configured enrollment token.

For a newly enrolled device, the Server binds the SHA-256 digest of the proof token to that device
record. The Agent continues using the same locally provisioned token on subsequent registration, so
the deployment can disable new enrollment without breaking already enrolled Agents. The deployment
provisions the token to the Agent through its installer/configuration. It is never returned by the
Server, logged, or stored in plaintext. Invalid authentication causes `ERROR` and
a close with policy-violation status.

### Agent → Server: `REGISTER_DEVICE`

All fields below are required except `actual_state`.

```json
{
  "type": "REGISTER_DEVICE",
  "message_id": "f7e6df65-15a0-45fd-ac95-232d79f3288a",
  "sent_at": "2026-08-25T18:31:20.000Z",
  "protocol_version": "1.0",
  "device_id": "PC-001",
  "hostname": "CLASSROOM-01",
  "agent_version": "1.0.0",
  "os_version": "Windows 11 Pro 23H2",
  "local_ip": "192.168.1.21",
  "auth_token": "provisioned-device-secret",
  "reported_policy_version": 41,
  "actual_state": {
    "dns": { "enabled": true, "server": "192.168.1.100", "status": "CORRECT" },
    "chrome": { "incognito": false },
    "edge": { "inprivate": false }
  }
}
```

| Field | Type | Rule |
| --- | --- | --- |
| `type` | string | Exactly `REGISTER_DEVICE` |
| `message_id` | UUID string | Fresh transmission ID |
| `sent_at` | timestamp string | Agent send time |
| `protocol_version` | string | Exactly `1.0` |
| `device_id` | string | Authoritative valid device ID |
| `hostname` | string | 1–255 chars |
| `agent_version` | string | 1–64 chars |
| `os_version` | string | 1–512 chars |
| `local_ip` | string | 1–64 chars; reported local address, not identity |
| `auth_token` | string | 1–1024 chars; bearer secret over TLS in production |
| `reported_policy_version` | integer | `0` if the Agent has never applied a policy |
| `actual_state` | object or omitted | Exact state shape in section 5 when available |

On success the Server upserts the device record, records the observed remote IP, sets it `ONLINE`,
loads its durable jobs, and replies with `REGISTERED`.

### Server → Agent: `REGISTERED`

```json
{
  "type": "REGISTERED",
  "message_id": "2b6f69f5-57e9-46c1-8be3-8acb37b0a0de",
  "sent_at": "2026-08-25T18:31:20.090Z",
  "device_id": "PC-001",
  "server_time": "2026-08-25T18:31:20.090Z",
  "server_configuration": {
    "server_endpoint": "wss://192.168.1.100:8765/agent/ws",
    "heartbeat_interval_seconds": 15,
    "heartbeat_timeout_seconds": 45,
    "maximum_message_bytes": 262144
  },
  "desired_policy_version": 42,
  "desired_policy": {
    "dns": { "enabled": true, "server": "192.168.1.100" },
    "chrome": { "incognito": false },
    "edge": { "inprivate": false }
  },
  "pending_command_count": 3,
  "sync_required": true
}
```

The Agent should schedule a policy reconciliation when `sync_required` is `true`, but it must also
wait for and process a Server `SYNC_REQUEST`. `pending_command_count` is informational; commands
are authoritative only when received as `COMMAND`.

## 4. Presence / heartbeat

After registration, the Agent sends a valid heartbeat approximately every 15 seconds. It uses the
interval supplied by `REGISTERED`; short jitter is permitted. The Server default timeout is 45
seconds. Missing valid heartbeats mark the device `OFFLINE`; the next successful registration or
heartbeat makes it `ONLINE` again.

### Agent → Server: `HEARTBEAT`

```json
{
  "type": "HEARTBEAT",
  "message_id": "5ca6d6a8-5e5c-4d72-92b0-18c65e4363dd",
  "sent_at": "2026-08-25T18:31:35.000Z",
  "device_id": "PC-001",
  "heartbeat_time": "2026-08-25T18:31:35.000Z",
  "agent_version": "1.0.0",
  "local_ip": "192.168.1.21",
  "reported_policy_version": 42,
  "actual_state": {
    "dns": { "enabled": true, "server": "192.168.1.100", "status": "CORRECT" },
    "chrome": { "incognito": false },
    "edge": { "inprivate": false }
  }
}
```

Required fields are `type`, `message_id`, `sent_at`, `device_id`, and `heartbeat_time`.
`agent_version`, `local_ip`, `reported_policy_version`, and `actual_state` are optional; if
supplied their types must match the example. The Server replies `ACK` for a valid heartbeat.

## 5. Desired policy synchronization

The Server owns desired policy. Agents report actual state; they do not invent a desired version.
A device is `SYNCED` only if the reported applied version equals the current desired version **and**
all of these actual values match:

```json
{
  "dns": {
    "enabled": true,
    "server": "192.168.1.100",
    "status": "CORRECT"
  },
  "chrome": { "incognito": false },
  "edge": { "inprivate": false }
}
```

`dns.status` is one of `CORRECT`, `INCORRECT`, or `UNKNOWN`. It is used for dashboard display;
the Server compares `dns.enabled` and `dns.server` itself. `chrome.incognito` and `edge.inprivate`
are booleans: `false` means the respective private-mode feature is disabled by policy.

### Agent → Server: `SYNC_REQUEST`

The Agent sends this after startup/reconnect or when it wants authoritative reconciliation.

```json
{
  "type": "SYNC_REQUEST",
  "message_id": "5b131989-a406-4207-8adc-378829b51c64",
  "sent_at": "2026-08-25T18:31:36.000Z",
  "device_id": "PC-001",
  "reported_policy_version": 41,
  "reason": "STARTUP"
}
```

`reason` is optional (0–256 chars). The Server replies `ACK` and schedules a Server-to-Agent
`SYNC_REQUEST`. Agents must tolerate duplicate sync requests.

### Server → Agent: `SYNC_REQUEST`

The same message name is direction-sensitive. This is the authoritative work request:

```json
{
  "type": "SYNC_REQUEST",
  "message_id": "9c7bb666-74e4-4964-ac0e-cba819db5b42",
  "sent_at": "2026-08-25T18:31:36.120Z",
  "request_id": "6e1d5e2b-9dd3-46ec-b7d6-0d4715bf3cac",
  "device_id": "PC-001",
  "policy_version": 42,
  "desired_policy": {
    "dns": { "enabled": true, "server": "192.168.1.100" },
    "chrome": { "incognito": false },
    "edge": { "inprivate": false }
  },
  "reason": "RECONCILIATION"
}
```

All fields are required. `reason` is a string. The Agent applies or verifies the desired policy,
then sends exactly one durable `SYNC_RESULT` for this request. Repeated Server requests for the same
version are safe and should re-verify rather than cause destructive duplicate work.

### Agent → Server: `SYNC_RESULT`

```json
{
  "type": "SYNC_RESULT",
  "message_id": "95d2fc9e-014e-4b2a-af2a-2c6378ecee29",
  "sent_at": "2026-08-25T18:31:37.000Z",
  "device_id": "PC-001",
  "policy_version": 42,
  "status": "SUCCESS",
  "actual_state": {
    "dns": { "enabled": true, "server": "192.168.1.100", "status": "CORRECT" },
    "chrome": { "incognito": false },
    "edge": { "inprivate": false }
  },
  "error": null,
  "completed_at": "2026-08-25T18:31:37.000Z"
}
```

`status` is exactly `SUCCESS` or `FAILED`; `actual_state` is required and must be an object;
`error` is an optional string (maximum 16 KiB); `completed_at` is required. The Server replies
`ACK`. An Agent persists the result until that ACK is received, then may compact it according to
its local retention policy.

## 6. Commands

### Server → Agent: `COMMAND`

```json
{
  "type": "COMMAND",
  "message_id": "e1e4e7d2-3487-4587-bfe1-7214cd394f2c",
  "sent_at": "2026-08-25T18:31:38.000Z",
  "command_id": "bc72f56a-c1c8-4c45-9a82-4cbd54ae1ca1",
  "device_id": "PC-001",
  "command_type": "RUN_ADMIN_COMMAND",
  "payload": { "command": "hostname", "timeout_seconds": 60 },
  "created_at": "2026-08-25T18:30:00.000Z",
  "attempt": 1
}
```

All fields are required. `attempt` is a positive integer and is diagnostic only. A repeated
`COMMAND` has the **same** `command_id`; it is a delivery retry, never a new command.

The Agent must first persist an accepted command ID to its local execution ledger, then send
`COMMAND_RECEIVED`, execute at most once, persist its result, send `COMMAND_RESULT`, and retain the
result until `ACK`. It must never execute a duplicate `command_id` merely because the Server sent
another `COMMAND` frame.

### Supported `command_type` values and payloads

All values are uppercase and exact. All payloads are objects. Unknown command types must be
reported as `FAILED`, not executed.

| Command type | Required payload fields | Optional payload fields / notes |
| --- | --- | --- |
| `GET_SYSTEM_INFO` | none | Agent returns information in `result_data` object. |
| `GET_INSTALLED_APPS` | none | Success `result_data` is `{ "applications": [ { "package_id": string, "display_name": string, "version": string } ] }` or the array itself. |
| `INSTALL_APP` | `package_id` string | `source` string, `desired_version` string/null, `install_arguments` object/null; Agent normally uses WinGet. |
| `UNINSTALL_APP` | `package_id` string | same application fields |
| `UPDATE_APP` | `package_id` string | same application fields |
| `CHECK_APP` | `package_id` string | same application fields |
| `APPLY_DNS` | `server` string | `enabled` boolean; set to the provided server when enabled. |
| `CHECK_DNS` | none | Return actual DNS state in `result_data`. |
| `APPLY_BROWSER_POLICY` | `desired_policy` object | Uses the policy shape in section 5. |
| `CHECK_BROWSER_POLICY` | none | Return actual browser state in `result_data`. |
| `APPLY_APP_POLICY` | `desired_policy` object | Agent-specific approved application policy. |
| `CHECK_APP_POLICY` | none | Return actual application-policy state in `result_data`. |
| `SYNC_POLICY` | none | Agent requests/re-runs policy reconciliation. |
| `RESTART_AGENT` | none | Result must be persisted before process restart. |
| `RESTART_PC` | none | Result indicates whether shutdown was accepted/scheduled. |
| `SHUTDOWN_PC` | none | Result indicates whether shutdown was accepted/scheduled. |
| `LOCK_PC` | none | none |
| `LOGOFF_USER` | none | none |
| `RUN_ADMIN_COMMAND` | `command` string | `timeout_seconds` integer (1–3600). Server authorization and audit are mandatory; Agent must not bypass Windows security. |

### Agent → Server: `COMMAND_RECEIVED`

```json
{
  "type": "COMMAND_RECEIVED",
  "message_id": "77c12c60-7f40-41e2-bc9b-581050052e1f",
  "sent_at": "2026-08-25T18:31:38.040Z",
  "device_id": "PC-001",
  "command_id": "bc72f56a-c1c8-4c45-9a82-4cbd54ae1ca1",
  "status": "RECEIVED",
  "received_at": "2026-08-25T18:31:38.040Z",
  "started_at": null
}
```

`status` is exactly `RECEIVED` or `RUNNING`. `received_at` is required. `started_at` is optional
for `RECEIVED`, and should be supplied for `RUNNING`. An Agent may later send a second
`COMMAND_RECEIVED` with `status: "RUNNING"`. The Server responds with `ACK`.

### Agent → Server: `COMMAND_RESULT`

```json
{
  "type": "COMMAND_RESULT",
  "message_id": "b3ca760f-f41d-4d9e-becd-9b89fb7d9a6e",
  "sent_at": "2026-08-25T18:31:39.000Z",
  "device_id": "PC-001",
  "command_id": "bc72f56a-c1c8-4c45-9a82-4cbd54ae1ca1",
  "status": "SUCCESS",
  "started_at": "2026-08-25T18:31:38.100Z",
  "completed_at": "2026-08-25T18:31:39.000Z",
  "exit_code": 0,
  "output": "CLASSROOM-01\r\n",
  "error": null,
  "result_data": null
}
```

| Field | Type / rule |
| --- | --- |
| `status` | Exactly `SUCCESS`, `FAILED`, or `CANCELLED` |
| `started_at` | timestamp or `null` |
| `completed_at` | required timestamp |
| `exit_code` | integer or `null` |
| `output` | string or `null`; Server persists up to its configured default of 64 KiB |
| `error` | string or `null`; Server persists up to its configured default of 16 KiB |
| `result_data` | object, array, or `null` |

The Server saves the result before sending `ACK`. A duplicate result with the same `message_id` is
accepted idempotently. A later result cannot overwrite an already persisted conflicting final
result. A `SUCCESS` result is the only proof of successful execution in the administrator UI.

## 7. Server acknowledgements and errors

### Server → Agent: `ACK`

The Server sends `ACK` for accepted heartbeat, sync request/result, command receipt, and command
result messages.

```json
{
  "type": "ACK",
  "message_id": "8cc9aa5c-528a-41c1-819f-1b1fed5649bb",
  "sent_at": "2026-08-25T18:31:39.020Z",
  "acknowledged_message_id": "b3ca760f-f41d-4d9e-becd-9b89fb7d9a6e",
  "acknowledged_type": "COMMAND_RESULT",
  "command_id": "bc72f56a-c1c8-4c45-9a82-4cbd54ae1ca1"
}
```

`command_id` is present only when the acknowledged message concerns a command; otherwise it is
omitted. The Agent may resend a durable result after a reconnect until it sees this ACK.

### Server → Agent: `ERROR`

```json
{
  "type": "ERROR",
  "message_id": "a4df2cd9-5db4-47bc-9585-dbd08b91f2f5",
  "sent_at": "2026-08-25T18:31:39.020Z",
  "code": "UNKNOWN_COMMAND",
  "message": "Command result was rejected.",
  "correlation_id": "b3ca760f-f41d-4d9e-becd-9b89fb7d9a6e"
}
```

`correlation_id` is optional and identifies the rejected incoming message. The Server does not put
secrets, stack traces, token data, or administrator details in errors.

Current error codes are: `INVALID_JSON`, `INVALID_MESSAGE`, `UNSUPPORTED_MESSAGE`,
`REGISTER_REQUIRED`, `ALREADY_REGISTERED`, `AUTH_REQUIRED`, `AUTH_FAILED`,
`DEVICE_NOT_PROVISIONED`, `DEVICE_DISABLED`, `DEVICE_ID_MISMATCH`, `UNKNOWN_COMMAND`,
`INVALID_RESULT_STATUS`, `CONFLICTING_RESULT`, and `COMMAND_REJECTED`.

## 8. Durable delivery, retries, and reconciliation

The following status strings are used by the Server database and administrator UI:

`PENDING`, `SENT`, `RECEIVED`, `RUNNING`, `SUCCESS`, `FAILED`, `TIMEOUT`, `CANCELLED`.

1. The administrator action is committed first. For ALL or group targeting, the Server creates one
   unique `CommandJob` and one unique `command_id` **for each device** in the transaction.
2. Offline target devices remain `PENDING`; the Server does not spin or claim they received work.
3. For an online authenticated connection, the Server durably changes a job to `SENT` and then
   sends `COMMAND`.
4. `COMMAND_RECEIVED` moves it to `RECEIVED` or `RUNNING`; `COMMAND_RESULT` moves it to a final
   status. A connection loss after `SENT` is not success or failure.
5. Before acknowledgement, retry delays are bounded exponential backoff: default 5, 10, 20, …,
   capped at 300 seconds and 10 attempts. The implementation never performs rapid infinite sends.
   A maximum-attempt exhaustion becomes `TIMEOUT`; administrators retain the audit record.
6. A disconnect or Server restart moves unacknowledged `SENT` work back to `PENDING` with the same
   `command_id`. This is why Agent-side duplicate suppression is mandatory.
7. An Agent persists its command ledger and unacknowledged final results across Agent/PC reboot.
   On registration/reconnect it reconciles policy and receives pending work automatically.
8. `RECEIVED`/`RUNNING` work has a configurable execution timeout (120 minutes default). A later
   valid durable agent result may reconcile a `TIMEOUT` record to actual `SUCCESS` or `FAILED`.

For policy, each device has a durable assignment to a policy version. Changing policy or pressing
SYNC ALL creates a new desired version and an assignment for every device. Online devices receive
`SYNC_REQUEST`; offline assignments remain pending and are dispatched after their next registration.

## 9. Compatibility checklist for Agent authors

An interoperable Agent must:

- use this exact endpoint, message names, snake_case fields, and UUID/timestamp formats;
- send `REGISTER_DEVICE` first after every WebSocket connect;
- use its configured `auth_token` only in registration;
- send a heartbeat around every 15 seconds and reconnect with backoff after network loss;
- maintain a persistent `command_id` execution/result ledger and never execute the same ID twice;
- send and retain `COMMAND_RECEIVED`, `SYNC_RESULT`, and `COMMAND_RESULT` until Server `ACK`;
- apply `desired_policy` as the Server’s desired state and return full actual state;
- treat IP as telemetry only, not device identity;
- avoid UAC bypass, credential collection, security evasion, or any operation not authorized by a
  received Server command and local Windows policy.

No extra undocumented request/response is required for compatibility with `Server.exe`.
