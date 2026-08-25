# =============================================================================
# LanAgent uninstaller (run as Administrator)
# Usage: .\uninstall-agent.ps1 [-RemoveData] [-ServiceName LanAgent]
# =============================================================================
[CmdletBinding()]
param(
    [string]$ServiceName = "LanAgent",
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script as Administrator."
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Write-Host "[uninstall] service '$ServiceName' deleted."
}
else {
    Write-Host "[uninstall] service '$ServiceName' not found."
}

if ($RemoveData) {
    Remove-Item "C:\ProgramData\LanAgent" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "C:\Program Files\LanAgent" -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "[uninstall] data and program directories removed (device identity destroyed — the server must forget this device)."
}
else {
    Write-Host "[uninstall] kept C:\ProgramData\LanAgent (device identity + history). Use -RemoveData to wipe."
}
