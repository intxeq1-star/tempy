namespace OMAgent;

public static class DefaultWorkerScript
{
    public const string Content = """
# ============================================================
#  OM Background Worker - persistent controller script
#  Runs as SYSTEM Windows Service: persists across reboots.
#  Enforces application blocking, collects telemetry, heartbeats.
# ============================================================
$ErrorActionPreference = "Continue"

$me = "OMAgent worker (PID $PID)"
Write-Output "$me started at $(Get-Date -Format o)"
Write-Output ("Computer: " + $env:COMPUTERNAME + " | User: " + [System.Security.Principal.WindowsIdentity]::GetCurrent().Name)

# --- Active App Blocking Enforcement ---
$appControlPath = "$env:ProgramData\OMAgent\appcontrol.json"
if (Test-Path $appControlPath) {
    try {
        $cfg = Get-Content $appControlPath -Raw | ConvertFrom-Json
        $blocked = @($cfg.rules | Where-Object { $_.action -eq "BLOCK" -and $_.enabled -ne $false })
        if ($blocked.Count -gt 0) {
            Write-Output ("Enforcing App Control: {0} active block rule(s)" -f $blocked.Count)
            foreach ($r in $blocked) {
                $exe = $r.application
                if (-not $exe.EndsWith(".exe")) { $exe += ".exe" }
                
                # Verify IFEO Debugger is set
                $ifeoKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\$exe"
                if (-not (Test-Path $ifeoKey)) { New-Item -Path $ifeoKey -Force | Out-Null }
                Set-ItemProperty -Path $ifeoKey -Name "Debugger" -Value "systray.exe" -Force | Out-Null
                Set-ItemProperty -Path $ifeoKey -Name "OM_Blocked" -Value 1 -Type DWord -Force | Out-Null

                # Kill any active process instances
                $procName = [System.IO.Path]::GetFileNameWithoutExtension($exe)
                $procs = Get-Process -Name $procName -ErrorAction SilentlyContinue
                if ($procs) {
                    Write-Output ("Terminating blocked running instance of {0} (PID {1})" -f $exe, ($procs.Id -join ','))
                    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
                }
            }
        }
    } catch {
        Write-Output ("App control enforcement notice: " + $_.Exception.Message)
    }
}

# --- Telemetry & Health ---
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty LoadPercentage)
$mem = (Get-CimInstance Win32_OperatingSystem | Select-Object -First 1)
$ramF = ($mem.FreePhysicalMemory / 1MB).ToString("N1"); $ramT = ($mem.TotalVisibleMemorySize / 1MB).ToString("N1")
Write-Output ("CPU: {0}% | RAM: {1} GB / {2} GB free" -f $cpu, $ramF, $ramT)
Write-Output "$me finished cycle at $(Get-Date -Format o)"
""";
}
