# Tempy Server — durable Windows LAN management authority

This repository contains the central management server requested for a Windows LAN of up to roughly
89 managed PCs. The build output is a single Windows `Server.exe` hosting:

- an authenticated administrator console with live SSE updates;
- a persistent WebSocket endpoint for background Agents;
- SQLite-backed devices, policies, groups, jobs, results, applications, settings, and audit data;
- durable per-device command fan-out for one PC, groups, and ALL PCs;
- heartbeat presence, offline recovery, bounded retries, reconciliation, and policy synchronization.

> **Core invariant:** **database = source of truth; connection = delivery mechanism; Agent =
> execution mechanism.** A network connection is never the only copy of an administrator command.

The original static design files in the repository root are unrelated legacy assets. The Server
project lives in `src/Server` and is the supported deliverable.

## Project map

```text
Server.sln
src/Server/                    ASP.NET Core 8 Server.exe source
  Api/                         authenticated administrator API
  Data/                        EF Core SQLite database model
  Domain/                      durable entities and policy state
  Protocol/                    WebSocket message models and validation
  Security/                    token/password authentication
  Services/                    dispatch, retry, presence, sync, audit event bus
  Transport/                   persistent Agent WebSocket sessions
  wwwroot/                     live administrator UI
  appsettings.json             safe defaults (contains no credentials)
tests/Server.Tests/            12 reliability tests
docs/                          architecture and deployment notes
scripts/publish-win-x64.ps1    self-contained Server.exe publisher
installer/ServerInstaller.iss  Inno Setup template
PROTOCOL_CONTRACT.md           normative Agent interoperability contract
```

## Quick start (development)

Prerequisite: .NET SDK 8.0 or later.

```powershell
# PowerShell, from repository root
$env:BootstrapAdmin__Username = "admin"
$env:BootstrapAdmin__Password = "Use-a-unique-14-character-or-longer-password"
$env:Security__AllowNewDeviceEnrollment = "true"       # bootstrap only; disable after provisioning
$env:Security__AgentEnrollmentToken = "replace-with-a-long-random-enrollment-token"
dotnet run --project .\src\Server\Server.csproj
```

Then open `http://192.168.1.100:8765` from an administrator workstation (or the configured server
address). In the default configuration the Agent WebSocket endpoint is
`ws://192.168.1.100:8765/agent/ws`.

Do **not** put production secrets in `appsettings.json`. Use protected machine configuration,
Windows environment variables, or a secret manager. The server creates no default administrator and
never writes an administrator password or an Agent token in plaintext.

## Production build: `Server.exe`

```powershell
pwsh .\scripts\publish-win-x64.ps1
# output: artifacts\Server\Server.exe
```

The script makes a self-contained, single-file `win-x64` executable. For a Windows service install
and firewall/certificate guidance, see [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md). The included Inno
Setup template is `installer/ServerInstaller.iss`.

## Agent contract

**Read and implement [PROTOCOL_CONTRACT.md](PROTOCOL_CONTRACT.md) before connecting an Agent.**
It specifies the exact endpoint, JSON fields, registration authentication, heartbeats, commands,
acknowledgements, state transitions, retries, and policy reconciliation. It intentionally does not
require the Agent to use .NET or this repository’s internal architecture.

## Reliability behavior

- Selecting **ALL PCs** commits one `CommandJob` per registered/pre-provisioned device—not a socket
  broadcast.
- Offline devices retain `PENDING` jobs and execute after their next authenticated registration.
- `SENT` means a transport delivery attempt, not success. `SUCCESS` means an Agent supplied a
  persisted `COMMAND_RESULT`.
- Server and Agent restart/network loss are reconciled by stable `command_id` values and Agent-side
  duplicate-execution prevention.
- Policy edits and **SYNC ALL** create durable per-device policy assignments; offline assignments
  synchronize automatically after reconnect.

## Validation

```powershell
dotnet restore Server.sln
dotnet test Server.sln --configuration Release
```

The test suite maps directly to the 12 requested reliability cases: presence changes, one/all-PC
fan-out, offline recovery, restart recovery, reconnection, duplicate ID handling, policy revision,
and SYNC ALL.

## Security notes

This controls powerful Windows operations. Keep it LAN-restricted, provision per-device tokens,
create least-privilege administrator roles, use HTTPS/WSS with a trusted certificate in production,
and review the audit trail. This project deliberately does **not** include credential theft, UAC
bypass, privilege/security evasion, or unauthenticated management APIs.
