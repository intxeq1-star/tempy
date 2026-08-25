# LanAgent — Windows Background Agent (AI #1)

Production Windows background agent for the 89-PC centralized LAN management system.
It connects to **Server.exe** (AI #2) at `192.168.1.100:8765` and implements **only**
the shared wire protocol:

> **`PROTOCOL_CONTRACT.md` is the single source of truth.** AI #2 builds Server.exe from that
> document alone — no coordination beyond it is required or assumed.

```
                     LAN
                      |
              192.168.1.100
                      |
               +--------------+
               |   SERVER.EXE |   (AI #2 — independent implementation)
               +------+-------+
                      |  WebSocket (ws(s)://192.168.1.100:8765/agent/ws), JSON, contract v1
        +-----------+-----------+
        v           v           v
     PC-001      PC-002  ...  PC-089
      LanAgent    LanAgent     LanAgent   (this repo: Windows Service per PC)
```

## What it does

- Runs as a **Windows Service** (`LanAgent`, auto-start, LocalSystem after authorized admin install, restart-on-crash recovery).
- Maintains a **persistent WebSocket** to the management server with a full connection state machine
  (`DISCONNECTED → CONNECTING → AUTHENTICATING → CONNECTED → SYNCHRONIZING → READY → RECONNECTING`),
  exponential backoff (2 s → 60 s, jittered, reset after stable connection) and exactly **one** connection owner.
- **Heartbeats every 15 s** (server-overridable) — the server is authoritative for online/offline.
- **Durable command execution**: `RECEIVED → RUNNING → SUCCESS/FAILED` with immediate acknowledgement,
  idempotency by `command_id` (never executes a completed command twice — replays the stored result),
  a local SQLite **result outbox** flushed after every reconnect until the server acks, and crash
  recovery that never blindly re-runs interrupted commands.
- **Desired-state synchronization** (`SYNC_REQUEST/SYNC_PAYLOAD/SYNC_RESULT`) on boot, reconnect,
  server push, admin action **and** periodically (default 5 min) — never push-only.
- Management capabilities: system info, installed apps, **winget** install/uninstall/update,
  **DNS** management (physical adapters only, idempotent, verified), **browser policy**
  (Chrome Incognito / Edge InPrivate via documented HKLM policies), **AppLocker** app policy,
  restart/shutdown/lock/logoff, `RUN_ADMIN_COMMAND` with output capture + timeout + optional
  executable allowlist, and self-update (`UPDATE_AGENT`) with SHA-256 (+ optional RSA signature) verification.
- Identity: a UUID generated at first start, persisted in SQLite — survives reboot, IP changes,
  reinstalls of the network stack. IP is never the identity.

## Repository layout

```
windows-agent/
├── PROTOCOL_CONTRACT.md          ← THE shared protocol contract (AI #2 implements from this)
├── docs/ARCHITECTURE.md          ← component/architecture walkthrough
├── LanAgent.sln
├── build.ps1                     ← build/test/publish helper
├── installer/
│   ├── install-agent.ps1         ← admin installer (config + ACL + service + recovery)
│   └── uninstall-agent.ps1
├── src/
│   ├── LanAgent.Core/            ← the agent itself (transport, connection, commands, sync, management)
│   └── LanAgent.Service/         ← Windows Service host (.NET Worker Service; also runs as console)
└── tests/
    ├── LanAgent.Core.Tests/      ← xUnit unit tests (run anywhere)
    └── LanAgent.TestServer/      ← reference Server.exe simulator + end-to-end scenarios
```

## Getting the working .exe

The executable is produced by compiling this project — it is not (and cannot be) generated inside
a text-only environment. Three ways to get the finished binary:

1. **Zero-install (cloud build):** enable the included workflow once — copy
   `windows-agent/ci/agent-build.yml` to `.github/workflows/agent-build.yml` and commit it
   (do this from your own GitHub account / the web UI; automation tokens often lack workflow
   permission). After that, every push runs the unit tests and builds **single-file,
   self-contained** `LanAgent.Service.exe` + `LanAgent.TestServer.exe` on a GitHub Windows runner —
   download them from the run's *Artifacts* section. Pushing a tag `agent-v1.0.0` attaches the exe
   to a GitHub Release.
2. **One command, any Windows PC with the .NET 8 SDK** (`winget install Microsoft.DotNet.SDK.8`):
   ```powershell
   cd windows-agent
   .\build-release.ps1          # tests + publish + zip  →  release\LanAgent\LanAgent.Service.exe
   ```
3. **Manual publish:**
   ```powershell
   dotnet publish src/LanAgent.Service -c Release -r win-x64 --self-contained true ^
     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
     -p:EnableCompressionInSingleFile=true -o release\LanAgent
   ```

The output is one self-contained `LanAgent.Service.exe` (~40 MB) — the 89 managed PCs need **no
.NET runtime installed**. Deploy it with the installer:

```powershell
.\installer\install-agent.ps1 -EnrollmentKey "<secret issued by the server>" -SourceDir .\LanAgent
```

The test harness exe (`LanAgent.TestServer.exe`) runs on your admin PC / any PC:
`LanAgent.TestServer.exe --port=8765 --localhost` (see "Running the end-to-end test harness").

## Building

Requires the **.NET 8 SDK** (Windows for deployment; unit tests build/run on any OS).

```powershell
cd windows-agent
.\build.ps1 -RunUnitTests -Publish
# output: publish/LanAgent/LanAgent.Service.exe
```

## Running the end-to-end test harness

Terminal 1 — simulated server (implements the contract from scratch, no agent code):

```powershell
dotnet run --project tests/LanAgent.TestServer -- --port=8765 --localhost
```

Terminal 2 — the agent as a console app pointed at the harness (env-var config overrides):

```powershell
$env:Agent__ServerIp="127.0.0.1"; $env:Agent__ServerPort="8765"
$env:Agent__EnrollmentKey="test-enroll-key"
$env:Agent__HeartbeatIntervalSeconds="5"; $env:Agent__BackoffInitialSeconds="1"; $env:Agent__BackoffMaxSeconds="5"
dotnet run --project src/LanAgent.Service
```

The harness runs the full scenario suite (registration, heartbeat cadence, reconnect+sync,
one-PC command, redelivery, duplicate idempotency, **offline→pending→reconnect→execute**,
**network-drop mid-command with outbox result survival**, DNS/browser checks, app install failure
handling, `RUN_ADMIN_COMMAND` capture, server-pushed sync, `POLICY_CHANGED`, ping/pong, `GET_STATE`,
unknown-type handling) and prints a PASS/FAIL summary. Add `--mutate` to also exercise the
state-changing `APPLY_DNS` / `APPLY_BROWSER_POLICY` scenarios on the test machine.

Unit tests: `dotnet test tests/LanAgent.Core.Tests` — protocol parsing, backoff, persistence,
command idempotency/outbox/recovery, sync engine version semantics.

## Installing on the 89 PCs (authorized, by an administrator)

```powershell
# on each PC (or via your software distribution / GPO / Intune):
.\installer\install-agent.ps1 -EnrollmentKey "<secret issued by the server>" -SourceDir .\publish\LanAgent
```

The installer: copies binaries, writes `appsettings.json` (server IP/port, enrollment key),
creates and ACLs `C:\ProgramData\LanAgent`, creates the `LanAgent` service (auto start, LocalSystem,
crash restart recovery 5 s/30 s/60 s) and starts it. Nothing self-elevates — the agent never
attempts privilege escalation; it simply runs with the privileges legitimately granted at install.

## Configuration reference (`appsettings.json` → `Agent` section)

| Key | Default | Meaning |
|---|---|---|
| `ServerIp` / `ServerPort` | `192.168.1.100` / `8765` | Server location (constants, not scattered) |
| `UseTls` | `false` | `wss://` + `https://` instead of plain |
| `EnrollmentKey` | installer-set | Shared secret for first registration |
| `HeartbeatIntervalSeconds` | `15` | Heartbeat cadence (server may override 5–300) |
| `SyncIntervalSeconds` | `300` | Periodic reconciliation safety net |
| `BackoffInitial/MaxSeconds` | `2` / `60` | Reconnect backoff bounds |
| `CommandTimeoutSeconds` | `600` | Default per-command execution timeout |
| `MaxCommandConcurrency` | `1` | Commands execute serially by default |
| `AdminCommandAllowlistEnabled` | `false` | Restrict `RUN_ADMIN_COMMAND` executables |
| `Dns.Servers` | `["192.168.1.100"]` | DNS servers applied by APPLY_DNS / policy |
| `DataDirectory` | `C:\ProgramData\LanAgent` | Logs, `state.db` (SQLite), `device-id.txt` |
| `AgentUpdatePublicKey` | `""` | RSA public key to verify signed agent updates |

## Operations

- Logs: `C:\ProgramData\LanAgent\logs\lanagent-YYYYMMDD.log` — structured JSON lines, daily
  rotation, 10 MB size cap, 14-day retention.
- State: `C:\ProgramData\LanAgent\state.db` (SQLite/WAL) — device identity+token, command
  history (idempotency), result outbox, applied policy version.
- Graceful stop: SCM stop or `net stop LanAgent` (in-flight commands are either completed and
  persisted or recovered as FAILED on next start — never blindly re-run).
- The agent trusts **only** the configured server; it ignores redirects and never accepts
  management traffic from anywhere else.

## Security posture

Authorized endpoint management only: no credential theft, no UAC bypass, no AV evasion, no
hidden persistence — the opposite: every privileged action flows through an administrator-installed
service, an authenticated server channel, validated commands with unique IDs and a full local audit
trail. The enrollment key and device token are secrets; the installer restricts the data directory
to Administrators/SYSTEM.
