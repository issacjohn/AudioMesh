param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$dotnet = Join-Path $env:LOCALAPPDATA "dotnet-sdk\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

Write-Host "Building AudioMesh ($Configuration)..." -ForegroundColor Cyan
& $dotnet build (Join-Path $root "src\AudioMesh\AudioMesh.csproj") -c $Configuration

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "OK -> src\AudioMesh\bin\$Configuration\net8.0-windows\AudioMesh.exe" -ForegroundColor Green
