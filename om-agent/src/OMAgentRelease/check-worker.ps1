# ============================================================
#  OM Background Worker & App Blocker - health check
#  Run in an Administrator PowerShell window.
# ============================================================

$Service  = 'OMAgent'
$DataDir  = "$env:ProgramData\OMAgent"
$Status   = Join-Path $DataDir 'status.json'
$AppCtrl  = Join-Path $DataDir 'appcontrol.json'

Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "  OM Background Worker & App Control Health Check" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan

# 1) Is service installed?
$svc = Get-Service -Name $Service -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host "Service '$Service' is NOT installed." -ForegroundColor Red
    Write-Host "Install via OMClient dashboard: Click 'Install worker'." -ForegroundColor Yellow
} else {
    Write-Host ("Service  : {0,-12} StartType: {1}" -f $svc.Status, $svc.StartType) -ForegroundColor Green
}

# 2) Is worker process running?
$proc = Get-Process -Name 'OMClient' -ErrorAction SilentlyContinue
Write-Host ("Process  : {0}" -f $(if ($proc) { "RUNNING (PID $($proc.Id))" } else { "Not found in user session" })) -ForegroundColor $(if ($proc) { 'Green' } else { 'Yellow' })

# 3) Read status.json
if (Test-Path $Status) {
    $s = Get-Content $Status -Raw | ConvertFrom-Json
    Write-Host "--------------------------------------" -ForegroundColor DarkGray
    Write-Host ("Worker state  : {0}" -f $s.state)
    Write-Host ("Worker PID    : {0}" -f $s.processId)
    Write-Host ("Blocked Apps  : {0} active" -f $s.blockedAppsCount)
    Write-Host ("Last run      : {0}   [{1}]" -f $s.lastRunAtUtc, $s.lastRunStatus)
    Write-Host ("Script runs   : {0}" -f $s.scriptUseCount)

    if ($s.lastHeartbeatUtc) {
        $age = (Get-Date) - [datetime]::Parse($s.lastHeartbeatUtc)
        if ($age.TotalSeconds -lt 150) {
            Write-Host ("Heartbeat     : FRESH ({0:N0}s ago)" -f $age.TotalSeconds) -ForegroundColor Green
        } else {
            Write-Host ("Heartbeat     : STALE ({0:N0}s ago)" -f $age.TotalSeconds) -ForegroundColor Red
        }
    }
}

# 4) Read App Control rules
if (Test-Path $AppCtrl) {
    $ac = Get-Content $AppCtrl -Raw | ConvertFrom-Json
    Write-Host "--------------------------------------" -ForegroundColor DarkGray
    Write-Host "Configured Application Rules:" -ForegroundColor Cyan
    foreach ($r in $ac.rules) {
        $statusStr = if ($r.enabled) { "ENABLED" } else { "DISABLED" }
        $color = if ($r.action -eq "BLOCK" -and $r.enabled) { "Red" } else { "Green" }
        Write-Host ("  [{0,-8}] {1,-16} ({2}) [{3}]" -f $r.action, $r.application, $r.friendlyName, $statusStr) -ForegroundColor $color
    }

    if ($ac.testPhase -and $ac.testPhase.active) {
        Write-Host "`n  *** TEST PHASE IS CURRENTLY ACTIVE on $($ac.testPhase.targetApp) ***" -ForegroundColor Yellow
    }
}

Write-Host "=====================================================" -ForegroundColor Cyan
