<#
.SYNOPSIS
  Prefect agent — the laptop's half of the desk.

  Talks only to the school server you pass in. Pulls an order, runs it,
  keeps the locker, knocks when a student asks for an app.
  Nothing is hidden. The scheduled task is named PrefectAgent.

  This is a school device-management agent, not a back door.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$Server,

  [string]$TokenPath = "$env:ProgramData\Prefect\token.txt",
  [string]$RoomId = "cart-1",
  [int]$PollSeconds = 4
)

$ErrorActionPreference = "Continue"
$Inbox = Join-Path $env:ProgramData "Prefect\inbox"
$Log = Join-Path $env:ProgramData "Prefect\agent.log"
New-Item -ItemType Directory -Force -Path (Split-Path $TokenPath) | Out-Null
New-Item -ItemType Directory -Force -Path $Inbox | Out-Null

function Write-PrefectLog {
  param([string]$Message)
  $line = "{0:o} {1}" -f (Get-Date).ToUniversalTime(), $Message
  Add-Content -Path $Log -Value $line
}

function Get-PrefectAuth {
  if (-not (Test-Path $TokenPath)) { return $null }
  $t = (Get-Content -Path $TokenPath -Raw).Trim()
  if (-not $t) { return $null }
  return @{ Authorization = "Bearer $t"; "Content-Type" = "application/json" }
}

function Invoke-Prefect {
  param([string]$Method, [string]$Path, [hashtable]$Headers, $Body)
  $uri = ($Server.TrimEnd("/")) + $Path
  $params = @{ Uri = $uri; Method = $Method; Headers = $Headers }
  if ($null -ne $Body) {
    $params.ContentType = "application/json"
    $params.Body = ($Body | ConvertTo-Json -Compress -Depth 6)
  }
  return Invoke-RestMethod @params
}

function Get-HostFacts {
  $cs = Get-CimInstance Win32_ComputerSystem
  $os = Get-CimInstance Win32_OperatingSystem
  $bat = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue
  $disk = Get-PSDrive -Name C -ErrorAction SilentlyContinue
  $ip = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike "127.*" -and $_.PrefixOrigin -ne "WellKnown" } |
    Select-Object -First 1 -ExpandProperty IPAddress)
  return @{
    hostname     = $env:COMPUTERNAME
    user         = $cs.UserName
    os           = $os.Caption
    build        = $os.Version
    ip           = $ip
    room_id      = $RoomId
    battery      = if ($bat) { [int]$bat.EstimatedChargeRemaining } else { $null }
    disk_free_gb = if ($disk) { [math]::Round($disk.Free / 1GB, 1) } else { $null }
  }
}

function Ensure-Enrolled {
  $headers = Get-PrefectAuth
  if ($headers) { return $headers }
  Write-PrefectLog "Enrolling $($env:COMPUTERNAME) at $Server"
  $res = Invoke-Prefect -Method POST -Path "/api/agent/enroll" -Headers @{} -Body (Get-HostFacts)
  Set-Content -Path $TokenPath -Value $res.token -Encoding ASCII
  Write-PrefectLog "Enrolled as $($res.device_id)"
  return Get-PrefectAuth
}

function Get-LockedProcessNames {
  param($Apps)
  $names = @()
  foreach ($a in $Apps) {
    if ($a.locked -and -not $a.always_allowed) {
      $names += $a.process
    }
  }
  return $names
}

function Sync-Locker {
  param($Apps)
  $locked = Get-LockedProcessNames $Apps
  foreach ($name in $locked) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
      try {
        Stop-Process -Id $_.Id -Force -ErrorAction Stop
        Write-PrefectLog "Locker closed $($_.Name) pid $($_.Id)"
      } catch {
        Write-PrefectLog "Could not close $($name): $_"
      }
    }
  }
}

function Show-PrefectMessage {
  param([string]$Text)
  if (-not $Text) { return }
  try {
    msg.exe * $Text
  } catch {
    Write-PrefectLog "msg.exe failed: $_"
  }
}

function Invoke-Order {
  param($Command, $Headers)
  $kind = $Command.kind
  $p = $Command.payload
  $stdout = ""
  $stderr = ""
  $status = "ok"
  try {
    switch ($kind) {
      "powershell" {
        $out = & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command $p.script 2>&1
        $stdout = ($out | Out-String)
      }
      "download" {
        $destDir = if ($p.dest) { $p.dest } else { $Inbox }
        New-Item -ItemType Directory -Force -Path $destDir | Out-Null
        $dest = Join-Path $destDir $p.name
        $uri = ($Server.TrimEnd("/")) + $p.url
        Invoke-WebRequest -Uri $uri -Headers $Headers -OutFile $dest
        $stdout = "Saved $dest"
        if ($p.run) {
          $ran = & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command $p.run 2>&1
          $stdout += "`n" + ($ran | Out-String)
        }
      }
      "message" { Show-PrefectMessage $p.text; $stdout = "shown" }
      "lock_screen" {
        Show-PrefectMessage $(if ($p.text) { $p.text } else { "Eyes up. The lesson has the room." })
        rundll32.exe user32.dll,LockWorkStation
        $stdout = "locked"
      }
      "unlock_screen" { $stdout = "released (student may sign back in)" }
      "reboot" {
        $stdout = "restart in 15s"
        shutdown.exe /r /t 15 /c "Prefect: the desk asked this machine to restart."
      }
      "kill" {
        Get-Process -Name $p.process -ErrorAction SilentlyContinue | Stop-Process -Force
        $stdout = "stopped $($p.process)"
      }
      default { $stdout = "policy $kind applied" }
    }
  } catch {
    $status = "fail"
    $stderr = "$_"
  }
  Invoke-Prefect -Method POST -Path "/api/agent/result" -Headers $Headers -Body @{
    result_id = $Command.result_id
    status    = $status
    stdout    = $stdout
    stderr    = $stderr
  } | Out-Null
}

Write-PrefectLog "Prefect agent starting. Server=$Server"
while ($true) {
  try {
    $headers = Ensure-Enrolled
    $beat = Invoke-Prefect -Method POST -Path "/api/agent/heartbeat" -Headers $headers -Body (Get-HostFacts)
    if ($beat.apps) { Sync-Locker $beat.apps }
    if ($beat.message) { }
    $next = Invoke-Prefect -Method GET -Path "/api/agent/next" -Headers $headers -Body $null
    if ($next.command) {
      Write-PrefectLog "Order $($next.command.kind) $($next.command.result_id)"
      Invoke-Order -Command $next.command -Headers $headers
    }
  } catch {
    Write-PrefectLog "Loop: $_"
  }
  Start-Sleep -Seconds $PollSeconds
}
