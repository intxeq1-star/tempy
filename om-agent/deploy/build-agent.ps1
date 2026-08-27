# Builds and publishes the OM Client Agent for win-x64.
# Usage:  .\build-agent.ps1 -Runtime win-x64
[cmdletbinding()]
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [switch]$SelfContained
)

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src\OMClientAgent\OMClientAgent.csproj"
$output = Join-Path $root "output"

Write-Host "==> Restoring" -ForegroundColor Cyan
dotnet restore $project

Write-Host "==> Publishing ($Runtime / $Configuration / self-contained=$SelfContained)" -ForegroundColor Cyan
$scFlag = if ($SelfContained) { "--self-contained true" } else { "--self-contained false" }
dotnet publish $project -c $Configuration -r $Runtime $scFlag -o $output

Write-Host "`nPublished to $output" -ForegroundColor Green
Write-Host "Now run: .\deploy\install-agent.ps1 -PublishDir `"$output`" -EnrollmentToken `<token>`" -ForegroundColor Yellow
