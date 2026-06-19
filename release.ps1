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
$cl = Get-Content $changelogPath -Raw
if ($cl -notmatch '(?ms)^## \[Unreleased\]\s*(.*?)(?=^## \[|\z)') { throw 'No [Unreleased] section in CHANGELOG.md.' }
$unreleased = $Matches[1].Trim()
if (-not $unreleased) { throw 'CHANGELOG [Unreleased] is empty - add entries before releasing.' }
$today = (Get-Date).ToString('yyyy-MM-dd')
$cl = $cl -replace '(?m)^## \[Unreleased\].*$', "## [Unreleased]`r`n`r`n## [$Version] - $today"
Set-Content $changelogPath $cl -NoNewline -Encoding utf8

# 5. Stamp the landing-page version label (both copies)
foreach ($p in @('website/index.html', 'docs/index.html')) {
    $fp = Join-Path $root $p
    (Get-Content $fp -Raw) -replace 'v\d+\.\d+\.\d+', "v$Version" | Set-Content $fp -NoNewline -Encoding utf8
}

# 6. Build (Release). Abort on any failure.
Write-Host "Building $Version..." -ForegroundColor Cyan
dotnet build (Join-Path $root 'FatGuysSpeak.Server') -c Release --framework net9.0; if ($LASTEXITCODE) { throw 'server net9.0 build failed' }
dotnet build (Join-Path $root 'FatGuysSpeak.Server') -c Release --framework net9.0-windows10.0.19041.0; if ($LASTEXITCODE) { throw 'server windows build failed' }
dotnet build (Join-Path $root 'FatGuysSpeak.Client') -c Release --framework net9.0-windows10.0.19041.0; if ($LASTEXITCODE) { throw 'client build failed' }
dotnet build (Join-Path $root 'FatGuysSpeak.Installer') -c Release --framework net9.0-windows10.0.19041.0; if ($LASTEXITCODE) { throw 'installer build failed' }

# 7. Collect versioned artifacts into release-output/ (gitignored). Fail loudly if an
#    expected artifact is missing (the Release build emits into a RID subfolder that
#    differs per project: installer -> win-x64, client -> win10-x64), so search for it.
$out = Join-Path $root 'release-output'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$setup = Get-ChildItem -Path (Join-Path $root 'FatGuysSpeak.Installer\bin\Release') -Recurse -Filter 'FatGuysSpeak-Server-Setup.exe' -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $setup) { throw 'Installer exe not found under FatGuysSpeak.Installer\bin\Release after build.' }
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
    foreach ($ch in @('win', $verChannel)) {
        vpk upload github --repoUrl $repoUrl --token $env:GITHUB_TOKEN --channel $ch `
            --tag "v$Version" --releaseName "FatGuysSpeak $Version" --publish -o (Join-Path $vpkOut $ch)
        if ($LASTEXITCODE) { throw "vpk upload github ($ch) failed" }
    }
    Write-Host "Velopack assets uploaded to release v$Version (channels: win, $verChannel)." -ForegroundColor Green
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
