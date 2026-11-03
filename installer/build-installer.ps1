param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $PSScriptRoot "output"
New-Item -ItemType Directory -Force -Path $output | Out-Null

$wix = Join-Path $env:USERPROFILE ".dotnet\tools\wix.exe"
if (-not (Test-Path $wix)) {
    throw "WiX CLI not found. Run: dotnet tool install --global wix --version 5.0.2"
}

$app = Join-Path $root "dist-fx\AudioMesh.exe"
if (-not (Test-Path $app)) {
    throw "Missing $app. Publish the framework-dependent build first."
}

$msi = Join-Path $output "AudioMesh-1.1.0-x64.msi"
$setup = Join-Path $output "AudioMeshSetup-1.1.0.exe"

Write-Host "Building MSI..." -ForegroundColor Cyan
& $wix build (Join-Path $PSScriptRoot "Product.wxs") `
    -arch x64 `
    -d SourceRoot="$root" `
    -d OutputDir="$output" `
    -ext WixToolset.Util.wixext `
    -o $msi

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Building setup bootstrapper..." -ForegroundColor Cyan
& $wix build (Join-Path $PSScriptRoot "Bundle.wxs") `
    -arch x64 `
    -d SourceRoot="$root" `
    -d OutputDir="$output" `
    -ext WixToolset.BootstrapperApplications.wixext `
    -ext WixToolset.Util.wixext `
    -o $setup

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Get-ChildItem $output -File | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize


