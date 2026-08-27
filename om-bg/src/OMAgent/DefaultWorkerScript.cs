namespace OMAgent;

public static class DefaultWorkerScript
{
    public const string Content = """
# ============================================================
#  OM Background Worker - default script
# ============================================================
$ErrorActionPreference = "Continue"
$me = "OMAgent worker (PID $PID)"
Write-Output "$me started at $(Get-Date -Format o)"
Write-Output ("Computer        : " + $env:COMPUTERNAME)
Write-Output ("User            : " + [System.Security.Principal.WindowsIdentity]::GetCurrent().Name)
Write-Output ("OS              : " + [System.Environment]::OSVersion.VersionString)

# App blocking check
$appControlPath = "$env:ProgramData\OMAgent\appcontrol.json"
if (Test-Path $appControlPath) {
    try {
        $cfg = Get-Content $appControlPath -Raw | ConvertFrom-Json
        $blocked = @($cfg.rules | Where-Object { $_.action -eq "BLOCK" -and $_.enabled -ne $false })
        Write-Output ("Active app control rules: {0} blocked" -f $blocked.Count)
    } catch {}
}

$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty LoadPercentage)
$mem = (Get-CimInstance Win32_OperatingSystem | Select-Object -First 1)
$ramF = ($mem.FreePhysicalMemory / 1MB).ToString("N1"); $ramT = ($mem.TotalVisibleMemorySize / 1MB).ToString("N1")
Write-Output ("CPU load        : {0}%" -f $cpu)
Write-Output ("RAM free        : {0} GB of {1} GB" -f $ramF, $ramT)
Write-Output "$me finished cycle at $(Get-Date -Format o)"
""";
}
