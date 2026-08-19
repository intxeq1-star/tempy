<#
.SYNOPSIS
  Take Prefect off a laptop. Run as Administrator.
#>
$ErrorActionPreference = "SilentlyContinue"
Unregister-ScheduledTask -TaskName "PrefectAgent" -Confirm:$false
Remove-Item -Recurse -Force "$env:ProgramData\Prefect"
Write-Host "Prefect is off this laptop."
