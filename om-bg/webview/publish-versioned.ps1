# Publishes OMClient.exe with its version in the filename (reads <Version> from the .csproj).
# Usage:  .\publish-versioned.ps1
# Output: out\OMClient_<Version>.exe
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csproj = Join-Path $root 'OMAgent.csproj'

$version = (Select-String -Path $csproj -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1).Matches[0].Groups[1].Value
if (-not $version) { $version = '1.0.0' }

$out = Join-Path $root 'out'
Write-Host "==> Publishing OMClient v$version (win-x64, self-contained, single-file)" -ForegroundColor Cyan
dotnet publish $csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $out

$src = Join-Path $out 'OMClient.exe'
$dest = Join-Path $out "OMClient_$version.exe"
if (Test-Path $dest) { Remove-Item $dest }
Copy-Item $src $dest
Remove-Item $src
Write-Host "`nBuilt: $dest" -ForegroundColor Green
