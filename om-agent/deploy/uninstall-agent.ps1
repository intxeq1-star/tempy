#Requires -RunAsAdministrator
[cmdletbinding()]
param(
    [string]$ServiceName = "OMClientAgent",
    [string]$DataDir = "C:\ProgramData\OM\Client",
    [switch]$DeleteData
)

$ErrorActionPreference = "Stop"

Write-Host "==> Stopping service '$ServiceName'" -ForegroundColor Cyan
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
    Write-Host "  -> service removed." -ForegroundColor Green
}
else {
    Write-Host "  -> service not present." -ForegroundColor Yellow
}

if ($DeleteData -and (Test-Path $DataDir)) {
    Write-Host "==> Removing data directory $DataDir" -ForegroundColor Cyan
    Remove-Item -Path $DataDir -Recurse -Force
}

Write-Host "OM Client Agent uninstalled." -ForegroundColor Green
