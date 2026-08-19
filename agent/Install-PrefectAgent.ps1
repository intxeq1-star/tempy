<#
.SYNOPSIS
  Install Prefect on a school Windows laptop. Run as Administrator.

  Creates C:\ProgramData\Prefect, saves the agent, and registers a
  scheduled task named PrefectAgent that starts at boot as SYSTEM.
#>
[CmdletBinding()]
param(
  [string]$Server = $env:PREFECT_SERVER,
  [string]$RoomId = "cart-1"
)

$ErrorActionPreference = "Stop"
if (-not $Server) {
  throw "Pass -Server https://your-prefect-host  (the desk address)."
}

$root = Join-Path $env:ProgramData "Prefect"
New-Item -ItemType Directory -Force -Path $root, (Join-Path $root "inbox") | Out-Null

$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$src = Join-Path $here "PrefectAgent.ps1"
$dst = Join-Path $root "PrefectAgent.ps1"

if (Test-Path $src) {
  Copy-Item $src $dst -Force
} else {
  $uri = $Server.TrimEnd("/") + "/agent/PrefectAgent.ps1"
  Invoke-WebRequest -Uri $uri -OutFile $dst
}

$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument (
  "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$dst`" -Server `"$Server`" -RoomId `"$RoomId`""
)
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
Register-ScheduledTask -TaskName "PrefectAgent" -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName "PrefectAgent"

Write-Host "Prefect is on this laptop."
Write-Host "  Server : $Server"
Write-Host "  Task   : PrefectAgent (SYSTEM, at startup)"
Write-Host "  Files  : $root"
Write-Host "A student may be told the machine is managed. That is the point."
