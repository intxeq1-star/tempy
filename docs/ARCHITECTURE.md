# Tempy Server architecture

## Correctness boundary

The architecture intentionally separates three facts:

```text
SQLite / EF Core database     = durable administrator intent and history
WebSocket connection          = temporary delivery channel
Windows Agent                 = authorized execution and local duplicate ledger
```

The command dispatcher never creates a job. `CommandService` creates an operation and all
per-device `CommandJob` rows in one transaction, then signals a background delivery worker. A
connection failure can at most delay delivery; it cannot delete intent.

## Components

| Component | Responsibility |
| --- | --- |
| `Data/ManagementDbContext` | Provider-agnostic EF Core entity model. SQLite is default; swap `UseSqlite` for a PostgreSQL/SQL Server provider in a future deployment. |
| `Domain/` | Device, group, policy, assignment, operation, job, result, application, audit and role entities. |
| `Protocol/` | Strict v1.0 JSON message records and validation. |
| `Transport/AgentSessionRunner` | One persistent WebSocket session; requires registration first and keys sessions by `device_id`. |
| `Security/` | PBKDF2 administrator passwords, hashed device tokens, cookie roles, CSRF and login rate limiting. |
| `Services/DeviceService` | Registration, heartbeat, state comparison, presence dashboard data. |
| `Services/CommandService` | Atomic job creation, receipts, results, audit and idempotent inbound result handling. |
| `Services/CommandDispatcherService` | Bounded delivery retries to online sessions only. |
| `Services/PolicyService` / `SyncDispatcherService` | Versioned desired state and durable device assignments. |
| `Services/DashboardEventBus` | Internal event bus exposed as same-origin Server-Sent Events. |
| `Api/` + `wwwroot/` | Authenticated administrator UI/API. |

## Database entities

The initial schema includes the requested production entities:

- `Device`, `DeviceGroup`, `DeviceGroupMembership`
- `Policy`, `DevicePolicyAssignment`
- `CommandOperation`, `CommandJob`, `CommandResult`
- `ApplicationCatalogItem`, `DeviceApplication`
- `AuditLog`, `AgentVersionRecord`, `ServerSetting`
- `AdminUser`, `ProcessedAgentMessage`

`CommandJob` contains the durable command fields: immutable `command_id`, device ID, type,
payload, creator/time, state, attempt counter/timestamps, receipt/start/completion times, exit code,
output, error, result data, and delivery error. Results are retained separately for audit history.

## Lifecycle summaries

### Command

```text
Admin request
  -> transaction: CommandOperation + one CommandJob per target, all PENDING
  -> dispatcher sees ONLINE device and durable job
  -> SENT / WebSocket COMMAND
  -> COMMAND_RECEIVED: RECEIVED or RUNNING
  -> COMMAND_RESULT: SUCCESS / FAILED / CANCELLED
```

An offline target does not enter a send loop. It stays `PENDING`; registration pulses the dispatcher.
On a disconnect or server restart, unacknowledged `SENT` work returns to `PENDING` with the same
command ID. This safely requires Agent-side idempotency, specified in `PROTOCOL_CONTRACT.md`.

### Policy

```text
Save desired state or SYNC ALL
  -> new monotonically increasing PolicyVersion
  -> DevicePolicyAssignment for every Device
  -> online sessions receive SYNC_REQUEST
  -> Agent SYNC_RESULT with full actual state
  -> synced only when actual values and policy version match
```

Offline assignments are not discarded. Device registration and heartbeat reconciliation trigger
policy dispatch automatically.

## Scale and operations

89 persistent Agent connections and 15-second heartbeats are well within Kestrel + SQLite’s expected
single-server workload. Timestamp fields are persisted as UTC Unix milliseconds so SQLite can safely
translate heartbeat/retry/timeout comparisons. SQLite is configured as the durable source for this single authority;
back up the database and test restore procedures. Keep command output bounded and do not store large
binary data in the database. For a multi-server or higher-write deployment, configure a supported EF
Core PostgreSQL/SQL Server provider and introduce database migrations/HA deliberately rather than
sharing a SQLite file.
