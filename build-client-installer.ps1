#Requires -Version 5.1
<#
.SYNOPSIS
    Build the FatGuysSpeak client installer.
    Publishes the MAUI client, zips it, embeds the zip, then publishes
    the installer as a single self-extracting exe.

.OUTPUTS
    release-output\FatGuysSpeak-Client-Setup.exe
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root       = $PSScriptRoot
$staging    = Join-Path $root 'release-staging\client-files'
$bundleZip  = Join-Path $root 'FatGuysSpeak.ClientInstaller\client-bundle.zip'
$outputDir  = Join-Path $root 'release-output'

Write-Host "`n=== FatGuysSpeak Client Installer Build ===`n" -ForegroundColor Cyan

# 1. Publish the MAUI client (Windows, self-contained, Release)
Write-Host "[1/4] Publishing client..." -ForegroundColor Yellow
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null

dotnet publish FatGuysSpeak.Client `
    --framework net9.0-windows10.0.19041.0 `
    --configuration Release `
    --output $staging `
    -p:WindowsPackageType=None

if ($LASTEXITCODE -ne 0) { Write-Error "Client publish failed."; exit 1 }
Write-Host "    Client published to $staging" -ForegroundColor Green

# 2. Zip the published output
Write-Host "[2/4] Creating client-bundle.zip..." -ForegroundColor Yellow
if (Test-Path $bundleZip) { Remove-Item $bundleZip -Force }
Compress-Archive -Path "$staging\*" -DestinationPath $bundleZip -CompressionLevel Optimal
$sizeMb = [math]::Round((Get-Item $bundleZip).Length / 1MB, 1)
Write-Host "    Bundle: $bundleZip ($sizeMb MB)" -ForegroundColor Green

# 3. Publish the installer (single file, embeds the zip via EmbeddedResource)
Write-Host "[3/4] Publishing installer..." -ForegroundColor Yellow
if (Test-Path $outputDir) { Remove-Item $outputDir -Recurse -Force }
New-Item -ItemType Directory -Force $outputDir | Out-Null

dotnet publish FatGuysSpeak.ClientInstaller `
    --framework net9.0-windows10.0.19041.0 `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    --output $outputDir

if ($LASTEXITCODE -ne 0) { Write-Error "Installer publish failed."; exit 1 }

# 4. Cleanup embedded zip (keep output clean)
Write-Host "[4/4] Cleaning up..." -ForegroundColor Yellow
Remove-Item $bundleZip -Force

$exePath = Join-Path $outputDir 'FatGuysSpeak-Client-Setup.exe'
$exeSizeMb = [math]::Round((Get-Item $exePath).Length / 1MB, 1)

Write-Host "`n=== Build complete ===" -ForegroundColor Cyan
Write-Host "Output : $exePath ($exeSizeMb MB)" -ForegroundColor Green
Write-Host "To release: Compress-Archive '$exePath' release-output\FatGuysSpeak-Client-Setup.zip" -ForegroundColor DarkGray
