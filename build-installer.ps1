# build-installer.ps1
# Produces a single self-extracting FatGuysSpeak-Server-Setup.exe
# Output: release-output\FatGuysSpeak-Server-Setup.exe

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

Write-Host "==> Publishing server (self-contained, win-x64)..."
$serverOut = Join-Path $root "release-staging\server-files"
if (Test-Path $serverOut) { Remove-Item -Recurse -Force $serverOut }
dotnet publish "$root\FatGuysSpeak.Server" `
    --framework net9.0-windows10.0.19041.0 `
    --runtime win-x64 `
    --self-contained true `
    --configuration Release `
    -o $serverOut `
    --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Server publish failed" }

Write-Host "==> Compressing server files..."
$bundleZip = Join-Path $root "FatGuysSpeak.Installer\server-bundle.zip"
if (Test-Path $bundleZip) { Remove-Item $bundleZip }
Compress-Archive -Path "$serverOut\*" -DestinationPath $bundleZip -CompressionLevel Optimal
$sizeMB = [math]::Round((Get-Item $bundleZip).Length / 1MB, 1)
Write-Host "    Bundle: ${sizeMB} MB"

Write-Host "==> Publishing installer (single-file, win-x64)..."
$installerOut = Join-Path $root "release-output"
if (Test-Path $installerOut) { Remove-Item -Recurse -Force $installerOut }
dotnet publish "$root\FatGuysSpeak.Installer" `
    --framework net9.0-windows10.0.19041.0 `
    --runtime win-x64 `
    --self-contained true `
    --configuration Release `
    -o $installerOut `
    --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Installer publish failed" }

Write-Host "==> Cleaning up..."
Remove-Item $bundleZip

$exePath = Join-Path $installerOut "FatGuysSpeak-Server-Setup.exe"
$exeMB   = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
Write-Host ""
Write-Host "Done! Single-file installer: $exePath (${exeMB} MB)"
