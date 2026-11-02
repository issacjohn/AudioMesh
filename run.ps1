param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Prefer the self-contained build: it needs no installed .NET runtime.
$standalone = Join-Path $root "dist\AudioMesh.exe"
if (Test-Path $standalone) {
    Start-Process -FilePath $standalone
    return
}

& (Join-Path $root "build.ps1") -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $root "src\AudioMesh\bin\$Configuration\net8.0-windows\AudioMesh.exe"
Start-Process -FilePath $exe
