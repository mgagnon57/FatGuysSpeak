#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$propsPath = Join-Path $root 'Directory.Build.props'
$changelogPath = Join-Path $root 'CHANGELOG.md'

function Parse-SemVer([string]$v) {
    if ($v -notmatch '^\d+\.\d+\.\d+$') { return $null }
    $p = $v.Split('.'); return [version]::new([int]$p[0], [int]$p[1], [int]$p[2])
}

# 1. Validate version format + monotonic increase
$new = Parse-SemVer $Version
if ($null -eq $new) { throw "Version '$Version' is not SemVer (MAJOR.MINOR.PATCH)." }
$propsXml = Get-Content $propsPath -Raw
if ($propsXml -notmatch '<Version>(\d+\.\d+\.\d+)</Version>') { throw 'Could not find <Version> in Directory.Build.props.' }
$current = Parse-SemVer $Matches[1]
# Allow >= so you can publish the current in-development source version (the version is
# pre-set in Directory.Build.props between releases); only a downgrade is rejected.
if ($new -lt $current) { throw "Version $Version must be >= current $($Matches[1]) (no downgrades)." }

# 2. Require a clean working tree
$dirty = (git -C $root status --porcelain)
if ($dirty) { throw "Working tree is dirty. Commit or stash before releasing.`n$dirty" }

# 3. Bump <Version>
(Get-Content $propsPath -Raw) -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$Version</Version>" |
    Set-Content $propsPath -NoNewline -Encoding utf8

# 4. Roll CHANGELOG [Unreleased] into [X.Y.Z] - date
# Read/write as UTF-8 explicitly. Windows PowerShell's Get-Content/Set-Content default to the
# system ANSI codepage, which mangles em-dashes/emoji on round-trip; use .NET with UTF-8 (no BOM).
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$cl = [System.IO.File]::ReadAllText($changelogPath)
if ($cl -notmatch '(?ms)^## \[Unreleased\]\s*(.*?)(?=^## \[|\z)') { throw 'No [Unreleased] section in CHANGELOG.md.' }
$unreleased = $Matches[1].Trim()
if (-not $unreleased) { throw 'CHANGELOG [Unreleased] is empty - add entries before releasing.' }
$today = (Get-Date).ToString('yyyy-MM-dd')
$cl = $cl -replace '(?m)^## \[Unreleased\].*$', "## [Unreleased]`r`n`r`n## [$Version] - $today"
[System.IO.File]::WriteAllText($changelogPath, $cl, $utf8NoBom)

# 5. Stamp the landing-page version label (both copies)
foreach ($p in @('website/index.html', 'docs/index.html')) {
    $fp = Join-Path $root $p
    $txt = [System.IO.File]::ReadAllText($fp) -replace 'v\d+\.\d+\.\d+', "v$Version"
    [System.IO.File]::WriteAllText($fp, $txt, $utf8NoBom)
}

# 6. Build (Release). Abort on any failure.
Write-Host "Building $Version..." -ForegroundColor Cyan
dotnet build (Join-Path $root 'FatGuysSpeak.Server') -c Release --framework net9.0; if ($LASTEXITCODE) { throw 'server net9.0 build failed' }
dotnet build (Join-Path $root 'FatGuysSpeak.Server') -c Release --framework net9.0-windows10.0.19041.0; if ($LASTEXITCODE) { throw 'server windows build failed' }
dotnet build (Join-Path $root 'FatGuysSpeak.Client') -c Release --framework net9.0-windows10.0.19041.0; if ($LASTEXITCODE) { throw 'client build failed' }

# Server installer: publish the server SELF-CONTAINED (bundles the .NET runtime + native SQLite +
# WebView2 loader, so the target machine needs no .NET install), zip it, then publish the installer
# single-file/self-contained — which embeds that bundle. A plain 'dotnet build' here would ship an
# installer with no embedded server, so always go through this publish path.
Write-Host "Publishing self-contained server + installer..." -ForegroundColor Cyan
$serverFiles = Join-Path $root 'release-staging\server-files'
Remove-Item -Recurse -Force $serverFiles -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'FatGuysSpeak.Server') -c Release -f net9.0-windows10.0.19041.0 `
    -r win-x64 --self-contained true -o $serverFiles --nologo; if ($LASTEXITCODE) { throw 'server publish failed' }
$bundleZip = Join-Path $root 'FatGuysSpeak.Installer\server-bundle.zip'
Remove-Item $bundleZip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $serverFiles '*') -DestinationPath $bundleZip -CompressionLevel Optimal
$installerPub = Join-Path $root 'release-output\installer-pub'
Remove-Item -Recurse -Force $installerPub -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'FatGuysSpeak.Installer') -c Release -f net9.0-windows10.0.19041.0 `
    -r win-x64 --self-contained true -o $installerPub --nologo; if ($LASTEXITCODE) { throw 'installer publish failed' }
Remove-Item $bundleZip -ErrorAction SilentlyContinue   # don't leave the bundle lying around

# 7. Collect versioned artifacts into release-output/ (gitignored). Fail loudly if an
#    expected artifact is missing (the Release build emits into a RID subfolder that
#    differs per project: installer -> win-x64, client -> win10-x64), so search for it.
$out = Join-Path $root 'release-output'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$setup = Join-Path $installerPub 'FatGuysSpeak-Server-Setup.exe'
if (-not (Test-Path $setup)) { throw "Self-contained installer exe not found at $setup after publish." }
Copy-Item $setup (Join-Path $out "FatGuysSpeak-Server-Setup-$Version.exe") -Force
$clientDir = Join-Path $root 'FatGuysSpeak.Client\bin\Release\net9.0-windows10.0.19041.0\win10-x64'
if (-not (Test-Path $clientDir)) { throw "Client build output not found at $clientDir." }
Compress-Archive -Path (Join-Path $clientDir '*') -DestinationPath (Join-Path $out "FatGuysSpeak-Client-$Version.zip") -Force

# 7b. Velopack: publish the client and upload to the GitHub release (default + per-version channel)
$repoUrl = 'https://github.com/mgagnon57/FatGuysSpeak'
$verChannel = 'v' + ($Version -replace '\.', '-')        # 1.2.0 -> v1-2-0 (matches UpdateChannel.ForVersion)
$clientPub = Join-Path $root 'release-output\client-pub'
Remove-Item -Recurse -Force $clientPub -ErrorAction SilentlyContinue
# SELF-CONTAINED: bundle the .NET runtime so non-technical users are never prompted to install
# .NET. Notes: use the portable 'win-x64' RID (the net9.0 Shared lib doesn't know legacy
# 'win10-x64'); UseMonoRuntime=false forces the Windows CoreCLR runtime pack (MAUI defaults to
# Mono, whose win-x64 pack doesn't exist); PublishReadyToRun=false avoids flaky crossgen.
dotnet publish (Join-Path $root 'FatGuysSpeak.Client') -c Release -f net9.0-windows10.0.19041.0 `
    -r win-x64 --self-contained true -p:UseMonoRuntime=false -p:PublishReadyToRun=false `
    -p:WindowsPackageType=None -o $clientPub
if ($LASTEXITCODE) { throw 'client publish failed' }

$vpkOut = Join-Path $root 'release-output\vpk'
foreach ($ch in @('win', $verChannel)) {
    vpk pack --packId FatGuysSpeak.Client --packVersion $Version --packDir $clientPub `
        --mainExe FatGuysSpeak.Client.exe --channel $ch -o (Join-Path $vpkOut $ch)
    if ($LASTEXITCODE) { throw "vpk pack ($ch) failed" }
}

if ($env:GITHUB_TOKEN) {
    # 'vpk upload github' CREATES the release for its tag and FATALs if that release already exists,
    # so only the FIRST channel can go through vpk. The client actually updates from the PER-VERSION
    # channel (UpdateChannel.ForVersion -> v0-10-1), so publish that one via vpk to create the
    # release, then attach the 'win' channel's (distinctly-named) assets with gh. Channel asset names
    # are suffixed (…-win-…, …-v0-10-1-…) so they never collide on the release.
    vpk upload github --repoUrl $repoUrl --token $env:GITHUB_TOKEN --channel $verChannel `
        --tag "v$Version" --releaseName "FatGuysSpeak $Version" --publish -o (Join-Path $vpkOut $verChannel)
    if ($LASTEXITCODE) { throw "vpk upload github ($verChannel) failed" }

    $winAssets = Get-ChildItem (Join-Path $vpkOut 'win') -File | ForEach-Object { $_.FullName }
    gh release upload "v$Version" @winAssets --clobber
    if ($LASTEXITCODE) { throw 'gh release upload (win channel) failed' }

    Write-Host "Velopack assets uploaded to release v$Version (channels: $verChannel via vpk, win via gh)." -ForegroundColor Green
} else {
    Write-Host "GITHUB_TOKEN not set - skipped vpk upload. Packages in $vpkOut." -ForegroundColor Yellow
}

# 8. Commit + tag (NO push)
git -C $root add Directory.Build.props CHANGELOG.md website/index.html docs/index.html
git -C $root commit -m "Release $Version"; if ($LASTEXITCODE) { throw 'git commit failed' }
git -C $root tag "v$Version"; if ($LASTEXITCODE) { throw 'git tag failed' }

# 9. Next step
Write-Host "`nReleased $Version locally. Artifacts in release-output/." -ForegroundColor Green
Write-Host "Review, then push:  git push && git push origin v$Version" -ForegroundColor Yellow
