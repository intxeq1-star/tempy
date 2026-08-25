# =============================================================================
# Build the total working .exe: a SINGLE-FILE, self-contained LanAgent.Service.exe
# (no .NET install needed on the 89 PCs) + the TestServer harness, zipped.
#
# Run on any Windows PC with the .NET 8 SDK installed:
#   cd windows-agent
#   .\build-release.ps1
#
# Output:
#   release\LanAgent\LanAgent.Service.exe   <- THE agent exe (single file, ~40 MB)
#   release\TestServer\LanAgent.TestServer.exe
#   release\lanagent-<version>-win64.zip    <- everything + installer scripts
# =============================================================================
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$version = "1.0.0"
$root = $PSScriptRoot
$out = Join-Path $root "release"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

Write-Host "== 1/4 Restoring ==" -ForegroundColor Cyan
dotnet restore (Join-Path $root 'LanAgent.sln')
if ($LASTEXITCODE -ne 0) { throw "restore failed — is the .NET 8 SDK installed? (winget install Microsoft.DotNet.SDK.8)" }

if (-not $SkipTests) {
    Write-Host "== 2/4 Unit tests ==" -ForegroundColor Cyan
    dotnet test (Join-Path $root 'tests\LanAgent.Core.Tests') -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "unit tests failed" }
} else {
    Write-Host "== 2/4 Unit tests SKIPPED ==" -ForegroundColor Yellow
}

Write-Host "== 3/4 Publishing single-file exes ==" -ForegroundColor Cyan
$common = @(
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=none"
)

dotnet publish (Join-Path $root 'src\LanAgent.Service') @common -o (Join-Path $out 'LanAgent')
if ($LASTEXITCODE -ne 0) { throw "agent publish failed" }

dotnet publish (Join-Path $root 'tests\LanAgent.TestServer') @common -o (Join-Path $out 'TestServer')
if ($LASTEXITCODE -ne 0) { throw "test server publish failed" }

Write-Host "== 4/4 Packaging ==" -ForegroundColor Cyan
Copy-Item (Join-Path $root 'installer') -Destination (Join-Path $out 'installer') -Recurse
$zip = Join-Path $out "lanagent-$version-win64.zip"
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -Force

Write-Host ""
Write-Host "DONE. Total working build produced:" -ForegroundColor Green
Get-ChildItem (Join-Path $out 'LanAgent\LanAgent.Service.exe'), (Join-Path $out 'TestServer\LanAgent.TestServer.exe'), $zip |
    ForEach-Object { Write-Host ("  {0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) }
Write-Host ""
Write-Host "Deploy on a managed PC (as Administrator):"
Write-Host "  .\installer\install-agent.ps1 -EnrollmentKey '<secret>' -SourceDir .\LanAgent"
Write-Host "Run the end-to-end test harness on your admin PC:"
Write-Host "  .\TestServer\LanAgent.TestServer.exe --port=8765 --localhost"
