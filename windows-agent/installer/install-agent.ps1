# =============================================================================
# LanAgent installer (run as Administrator on the target PC)
#
# Usage:
#   .\install-agent.ps1 -EnrollmentKey "SECRET-FROM-SERVER" `
#                       -SourceDir "C:\Deploy\LanAgent" `        # folder with published agent files
#                       [-ServerIp 192.168.1.100] [-ServerPort 8765] `
#                       [-InstallDir "C:\Program Files\LanAgent"] [-UseTls]
#
# What it does:
#   1. Copies the agent binaries to the install dir
#   2. Writes appsettings.json with the server coordinates + enrollment key
#   3. Creates C:\ProgramData\LanAgent (logs/state) and locks it to Admins/SYSTEM
#   4. Installs the "LanAgent" Windows service (automatic start, LocalSystem,
#      restart-on-failure recovery)
#   5. Starts the service
# =============================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EnrollmentKey,
    [Parameter(Mandatory = $true)][string]$SourceDir,
    [string]$ServerIp = "192.168.1.100",
    [int]$ServerPort = 8765,
    [switch]$UseTls,
    [string]$InstallDir = "C:\Program Files\LanAgent",
    [string]$ServiceName = "LanAgent"
)

$ErrorActionPreference = 'Stop'

# ---- 1. require administrator ------------------------------------------------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script as Administrator (authorized installation only — the agent never self-elevates)."
}

if (-not (Test-Path (Join-Path $SourceDir 'LanAgent.Service.exe'))) {
    throw "LanAgent.Service.exe not found in '$SourceDir'. Publish first: dotnet publish src/LanAgent.Service -c Release -r win-x64 --self-contained false -o <SourceDir>"
}

# ---- 2. copy binaries ---------------------------------------------------------
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallDir -Recurse -Force
Write-Host "[install] binaries copied to $InstallDir"

# ---- 3. write configuration ----------------------------------------------------
$dnsServers = "192.168.1.100"
if ($ServerIp -ne "192.168.1.100" -and $ServerIp -ne "") { $dnsServers = $ServerIp }

$appSettings = @"
{
  "Agent": {
    "ServerIp": "$ServerIp",
    "ServerPort": $ServerPort,
    "UseTls": $($UseTls.IsPresent.ToString().ToLowerInvariant()),
    "WebSocketPath": "/agent/ws",
    "EnrollmentKey": "$EnrollmentKey",
    "HeartbeatIntervalSeconds": 15,
    "ConnectTimeoutSeconds": 10,
    "AuthTimeoutSeconds": 15,
    "SyncResponseTimeoutSeconds": 30,
    "SyncIntervalSeconds": 300,
    "BackoffInitialSeconds": 2,
    "BackoffMaxSeconds": 60,
    "BackoffMultiplier": 2.0,
    "CommandTimeoutSeconds": 600,
    "MaxCommandConcurrency": 1,
    "AdminCommandMaxTimeoutSeconds": 900,
    "AdminCommandAllowlistEnabled": false,
    "AdminCommandAllowlist": [],
    "MaxMessageSizeBytes": 1048576,
    "MaxStdOutKilobytes": 256,
    "Dns": { "Servers": [ "$dnsServers" ], "ApplyToWireless": false },
    "DataDirectory": "C:\\ProgramData\\LanAgent",
    "AgentUpdatePublicKey": ""
  }
}
"@
# Keep the packaged template but override with the install-time values.
$templatePath = Join-Path $InstallDir 'appsettings.json'
if (Test-Path $templatePath) { Copy-Item $templatePath (Join-Path $InstallDir 'appsettings.template.json') -Force }
$appSettings | Out-File -FilePath $templatePath -Encoding utf8
Write-Host "[install] configuration written (server ${ServerIp}:${ServerPort})"

# ---- 4. data directory + ACL ----------------------------------------------------
$dataDir = "C:\ProgramData\LanAgent"
New-Item -ItemType Directory -Force -Path "$dataDir\logs", "$dataDir\updates" | Out-Null
# Restrict state (device token, command history) to Administrators and SYSTEM.
icacls $dataDir /inheritance:r /grant 'BUILTIN\Administrators:(OI)(CI)F' /grant 'NT AUTHORITY\SYSTEM:(OI)(CI)F' | Out-Null
Write-Host "[install] data directory prepared and ACLed: $dataDir"

# ---- 5. service registration -----------------------------------------------------
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Warning "[install] service '$ServiceName' already exists — stopping and reconfiguring it."
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
}

$binPath = '"' + (Join-Path $InstallDir 'LanAgent.Service.exe') + '"'
sc.exe create $ServiceName binPath= $binPath start= auto obj= LocalSystem | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed (exit $LASTEXITCODE)." }

sc.exe description $ServiceName "LAN management background agent (connects to ${ServerIp}:${ServerPort}). Authorized endpoint management software." | Out-Null
# Restart after crash: 5s, 30s, 60s; then reboot-delayed restart; reset counter after a day.
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000/reboot/60000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null
# Start automatically at boot even if delayed due to network.
sc.exe config $ServiceName start= auto | Out-Null

# ---- 6. start ----------------------------------------------------------------------
Start-Service -Name $ServiceName
Write-Host "[install] service started."
Write-Host ""
Write-Host "Done. The agent will:"
Write-Host "  - connect to $ServerIp`:$ServerPort (/agent/ws) and register with its enrollment key"
Write-Host "  - persist a permanent device ID under $dataDir (device-id.txt)"
Write-Host "  - write structured logs to $dataDir\logs"
Write-Host "Check status:  Get-Service $ServiceName   |   tail logs:  Get-Content $dataDir\logs\lanagent-*.log -Tail 40 -Wait"
