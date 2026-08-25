[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\Server')
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repo 'src\Server\Server.csproj'
$outputPath = [IO.Path]::GetFullPath($Output, $repo)

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 8 SDK is required to publish Server.exe.'
}

Remove-Item -LiteralPath $outputPath -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $outputPath

Copy-Item (Join-Path $repo 'src\Server\appsettings.Production.example.json') (Join-Path $outputPath 'appsettings.Production.example.json')
Copy-Item (Join-Path $repo 'PROTOCOL_CONTRACT.md') (Join-Path $outputPath 'PROTOCOL_CONTRACT.md')
Write-Host "Published Server.exe to $outputPath" -ForegroundColor Green
Write-Host 'Before starting production: configure HTTPS, administrator bootstrap credentials, device tokens, and data-directory ACLs.' -ForegroundColor Yellow
