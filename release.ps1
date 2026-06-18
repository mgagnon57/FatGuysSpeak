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
if ($new -le $current) { throw "Version $Version must be greater than current $($Matches[1])." }

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

# 7. Collect versioned artifacts into release-output/ (gitignored)
$out = Join-Path $root 'release-output'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$setup = Join-Path $root 'FatGuysSpeak.Installer\bin\Release\net9.0-windows10.0.19041.0\FatGuysSpeak-Server-Setup.exe'
if (Test-Path $setup) { Copy-Item $setup (Join-Path $out "FatGuysSpeak-Server-Setup-$Version.exe") -Force }
$clientDir = Join-Path $root 'FatGuysSpeak.Client\bin\Release\net9.0-windows10.0.19041.0\win10-x64'
if (Test-Path $clientDir) { Compress-Archive -Path (Join-Path $clientDir '*') -DestinationPath (Join-Path $out "FatGuysSpeak-Client-$Version.zip") -Force }

# 8. Commit + tag (NO push)
git -C $root add Directory.Build.props CHANGELOG.md website/index.html docs/index.html
git -C $root commit -m "Release $Version"; if ($LASTEXITCODE) { throw 'git commit failed' }
git -C $root tag "v$Version"; if ($LASTEXITCODE) { throw 'git tag failed' }

# 9. Next step
Write-Host "`nReleased $Version locally. Artifacts in release-output/." -ForegroundColor Green
Write-Host "Review, then push:  git push && git push origin v$Version" -ForegroundColor Yellow
