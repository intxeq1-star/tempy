# Build + test helper for LanAgent (PowerShell, run from windows-agent/)
# Requires the .NET 8 SDK. Everything runs on Windows; unit tests also run on Linux/macOS.

param([switch]$RunUnitTests, [switch]$Publish)

dotnet restore LanAgent.sln
if ($LASTEXITCODE -ne 0) { throw "restore failed" }

dotnet build LanAgent.sln -c Release
if ($LASTEXITCODE -ne 0) { throw "build failed" }

if ($RunUnitTests) {
    dotnet test tests/LanAgent.Core.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "unit tests failed" }
}

if ($Publish) {
    dotnet publish src/LanAgent.Service -c Release -r win-x64 --self-contained false -o publish/LanAgent
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
    Write-Host "Published to publish/LanAgent — copy that folder to the PCs and run installer\install-agent.ps1."
}
