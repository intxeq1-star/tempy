#Requires -RunAsAdministrator
[cmdletbinding()]
param(
    [string]$ServerUrl = "https://om-server.local:8443",
    [string]$AuthKey = "",
    [string]$CaCertPath = "",
    [string]$PublishDir = (Join-Path $PSScriptRoot "..\output"),
    [string]$ServiceName = "OMClientAgent",
    [string]$DataDir = "C:\ProgramData\OM\Client"
)

$ErrorActionPreference = "Stop"
$env:OM_SERVER_URL = $ServerUrl

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

function New-AgentDir {
    Write-Step "Preparing agent directory $DataDir\App"
    $app = Join-Path $DataDir "App"
    New-Item -ItemType Directory -Force -Path $app | Out-Null
    if (-not (Test-Path $PublishDir)) { throw "Publish directory not found: $PublishDir" }
    Get-ChildItem -Path $PublishDir -Recurse -File | Copy-Item -Destination $app -Recurse -Force
    return $app
}

function Write-Config {
    param([string]$AppDir)
    Write-Step "Writing appsettings.json"
    $config = @{
        Agent = @{
            ServerUrl        = $ServerUrl
            SignalRHubPath   = "/omHub"
            AuthKey          = $AuthKey
            CaCertificateFilePath = if ($CaCertPath -and (Test-Path $CaCertPath)) { $CaCertPath } else { "" }
            HeartbeatIntervalSeconds = 30
            OfflineGraceSeconds = 100
            SyncIntervalSeconds = 60
            DataDirectory    = $DataDir
            ServiceName      = $ServiceName
            VerifyServerCertificate = $true
        }
    }
    $json = $config | ConvertTo-Json -Depth 6
    Set-Content -Path (Join-Path $AppDir "appsettings.json") -Value $json -Encoding UTF8
}

function Install-RootCa {
    Write-Step "Installing internal LAN Root CA into machine Trusted Root store"
    if (-not $CaCertPath) { Write-Host "  -> no CA provided; skipping (rely on OS trust)." -ForegroundColor Yellow; return }
    if (-not (Test-Path $CaCertPath)) { throw "CA file not found: $CaCertPath" }
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CaCertPath)
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "LocalMachine")
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    $store.Add($cert)
    $store.Close()
    Write-Host "  -> installed $($cert.Thumbprint)" -ForegroundColor Green
}

function New-Service {
    param([string]$AppDir)
    Write-Step "Creating Windows service '$ServiceName'"
    $binPath = "`"$(Join-Path $AppDir 'OMClientAgent.exe')`""
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Write-Host "  -> service already exists; removing for a clean install." -ForegroundColor Yellow
        & sc.exe stop $ServiceName 2>$null | Out-Null
        & sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Seconds 2
    }
    & sc.exe create $ServiceName binPath= $binPath start= auto error= normal | Out-Null
    & sc.exe failure $ServiceName reset= 86400 actions= restart/15000/restart/30000/restart/60000 | Out-Null
    & sc.exe config $ServiceName start= auto | Out-Null
    Set-Service -Name $ServiceName -StartupType Automatic
    Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name "DelayedAutostart" -Value 1 -Type DWord
    Write-Step "Starting service '$ServiceName'"
    & sc.exe start $ServiceName | Out-Null
}

function Test-Install {
    param([string]$AppDir)
    Write-Step "Verifying installation"
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) { throw "Service '$ServiceName' was not created." }
    if ($svc.Status -ne "Running") { throw "Service '$ServiceName' is not running (status $($svc.Status))." }
    Write-Host "  -> service running: $($svc.Status)" -ForegroundColor Green

    $uri = [uri]$ServerUrl
    $hostName = $uri.Host; $port = if ($uri.IsDefaultPort) { 8443 } else { $uri.Port }
    $tcp = Test-NetConnection -ComputerName $hostName -Port $port -WarningAction SilentlyContinue
    if (-not $tcp.TcpTestSucceeded) { throw "Cannot reach $hostName`:$port — check the OM Server is up and the management port is open." }
    Write-Host "  -> TCP $hostName`:$port reachable" -ForegroundColor Green

    $diagExe = if (Test-Path (Join-Path $AppDir 'OMClientAgent.exe')) { Join-Path $AppDir 'OMClientAgent.exe' } else { 'dotnet OMClientAgent.dll' }
    $diagOut = & $diagExe --diagnostic 2>&1 | Out-String
    Write-Host ($diagOut.Trim())
    if ($diagOut -match "SIGNALR / WEBSOCKET: FAILED" ) {
        Write-Host "  -> WebSocket not reachable; the agent will use HTTPS synchronization fallback." -ForegroundColor Yellow
    }
}

try {
    if (-not (Test-Path $PublishDir)) { Write-Warning "PublishDir '$PublishDir' not present. Using output folder under the repo."; $PublishDir = Join-Path $PSScriptRoot "..\output" }
    $appDir = New-AgentDir
    Write-Config -AppDir $appDir
    Install-RootCa
    New-Service -AppDir $appDir
    Test-Install -AppDir $appDir
    Write-Host "`nOM Client Agent installed successfully." -ForegroundColor Green
} catch {
    Write-Host "`nInstall failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
