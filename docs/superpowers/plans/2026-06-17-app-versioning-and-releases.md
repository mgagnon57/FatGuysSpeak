# App Versioning & Releases Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One SemVer version (1.0.0) defined in a single place, flowing into server/client/installer, surfaced at runtime (API, dashboard, client, landing page) with the build's git commit + date, plus a CHANGELOG, `vX.Y.Z` tags, and a local `release.ps1`.

**Architecture:** A repo-root `Directory.Build.props` holds `<Version>` and stamps git SHA + UTC date into `InformationalVersion` on every build. A pure `VersionInfo.Parse` helper in `FatGuysSpeak.Shared` turns that string into `{version, commit, buildDate}`, consumed by an anonymous `GET /api/version`, a server-rendered dashboard footer, the client SettingsPage, and a landing-page label. Releases are cut by `release.ps1` (bump + changelog + stamp + build + commit + tag; no push).

**Tech Stack:** MSBuild (`Directory.Build.props`), ASP.NET Core 9 minimal API, .NET MAUI client, `FatGuysSpeak.Shared`, xUnit, PowerShell.

**Spec:** `docs/superpowers/specs/2026-06-17-app-versioning-and-releases-design.md`

**Conventions:**
- Headless build: `dotnet build FatGuysSpeak.Server --framework net9.0`
- Run helper tests: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~VersionInfoTests"`
- Windows builds may hit an MSB3027/3021 DLL-copy lock if an app is running; stop with `Get-Process FatGuysSpeak.Server,FatGuysSpeak.Client -ErrorAction SilentlyContinue | Stop-Process -Force` (PowerShell). A lock is not a compile error.
- Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`

---

### Task 1: Single source of truth — Directory.Build.props + client version

**Files:**
- Create: `Directory.Build.props` (repo root)
- Modify: `FatGuysSpeak.Client/FatGuysSpeak.Client.csproj` (lines 30-31)

- [ ] **Step 1: Create `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <Version>1.0.0</Version>
  </PropertyGroup>

  <!-- Stamp the git short SHA + UTC build date into InformationalVersion on every
       build. Falls back to "nogit" when git is unavailable (clean tarball / CI). -->
  <Target Name="StampGitVersion" BeforeTargets="GetAssemblyVersion;CoreCompile">
    <Exec Command="git rev-parse --short HEAD" ConsoleToMSBuild="true"
          ContinueOnError="true" StandardOutputImportance="low">
      <Output TaskParameter="ConsoleOutput" PropertyName="GitShaRaw" />
      <Output TaskParameter="ExitCode" PropertyName="GitExit" />
    </Exec>
    <PropertyGroup>
      <GitSha Condition="'$(GitExit)' == '0'">$(GitShaRaw.Trim())</GitSha>
      <GitSha Condition="'$(GitSha)' == ''">nogit</GitSha>
      <BuildDateUtc>$([System.DateTime]::UtcNow.ToString('yyyy-MM-dd'))</BuildDateUtc>
      <InformationalVersion>$(Version)+g$(GitSha).$(BuildDateUtc)</InformationalVersion>
    </PropertyGroup>
  </Target>
</Project>
```

- [ ] **Step 2: Derive the client display version from `$(Version)`**

In `FatGuysSpeak.Client/FatGuysSpeak.Client.csproj`, change the two version lines (currently `<ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>` and `<ApplicationVersion>1</ApplicationVersion>`) to:

```xml
		<ApplicationDisplayVersion>$(Version)</ApplicationDisplayVersion>
		<ApplicationVersion>1</ApplicationVersion>
```

(`ApplicationVersion` stays the integer `1` — Windows packaging requires an integer build number; the human-facing string is `ApplicationDisplayVersion`, now `1.0.0`.)

- [ ] **Step 3: Build the server and verify the stamp landed**

```
dotnet build FatGuysSpeak.Server --framework net9.0
```
Then (PowerShell) read the built assembly's product version:
```
(Get-Item "C:\FatGuysSpeak\FatGuysSpeak.Server\bin\Debug\net9.0\FatGuysSpeak.Server.dll").VersionInfo.ProductVersion
```
Expected: a string starting `1.0.0+g` followed by a short sha and the date, e.g. `1.0.0+g2b16624.2026-06-17`. (`ProductVersion` reflects `InformationalVersion`.)

- [ ] **Step 4: Commit**

```bash
git add Directory.Build.props FatGuysSpeak.Client/FatGuysSpeak.Client.csproj
git commit -m "$(cat <<'EOF'
Versioning: Directory.Build.props single source of truth + git/date stamp

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: VersionInfo parsing helper (pure, TDD)

**Files:**
- Create: `FatGuysSpeak.Shared/VersionInfo.cs`
- Create: `FatGuysSpeak.Tests/Server/VersionInfoTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `FatGuysSpeak.Tests/Server/VersionInfoTests.cs`:

```csharp
using FatGuysSpeak.Shared;
using Xunit;

namespace FatGuysSpeak.Tests.Server;

public class VersionInfoTests
{
    [Fact]
    public void NullOrEmpty_DefaultsToZero()
    {
        var v = VersionInfo.Parse(null);
        Assert.Equal("0.0.0", v.Version);
        Assert.Equal("", v.Commit);
        Assert.Equal("", v.BuildDate);
    }

    [Fact]
    public void NoSuffix_PassesVersionThrough()
    {
        var v = VersionInfo.Parse("1.2.3");
        Assert.Equal("1.2.3", v.Version);
        Assert.Equal("", v.Commit);
        Assert.Equal("", v.BuildDate);
    }

    [Fact]
    public void FullStamp_ParsesAllParts()
    {
        var v = VersionInfo.Parse("1.2.3+gabc1234.2026-06-17");
        Assert.Equal("1.2.3", v.Version);
        Assert.Equal("abc1234", v.Commit);
        Assert.Equal("2026-06-17", v.BuildDate);
    }

    [Fact]
    public void NogitSha_YieldsEmptyCommit()
    {
        var v = VersionInfo.Parse("1.2.3+gnogit.2026-06-17");
        Assert.Equal("1.2.3", v.Version);
        Assert.Equal("", v.Commit);
        Assert.Equal("2026-06-17", v.BuildDate);
    }

    [Fact]
    public void MalformedMetadata_DoesNotThrow_KeepsVersion()
    {
        var v = VersionInfo.Parse("1.2.3+weird-metadata");
        Assert.Equal("1.2.3", v.Version);
        // metadata kept as a best-effort commit fallback; never throws
        Assert.Equal("weird-metadata", v.Commit);
        Assert.Equal("", v.BuildDate);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~VersionInfoTests"`
Expected: FAIL — `VersionInfo` does not exist.

- [ ] **Step 3: Implement the helper**

Create `FatGuysSpeak.Shared/VersionInfo.cs`:

```csharp
namespace FatGuysSpeak.Shared;

/// <summary>Parsed build version. Produced from an assembly's InformationalVersion
/// (format "MAJOR.MINOR.PATCH+g{sha}.{yyyy-MM-dd}"). Pure + tolerant: never throws.</summary>
public record VersionInfo(string Version, string Commit, string BuildDate)
{
    public static VersionInfo Parse(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
            return new VersionInfo("0.0.0", "", "");

        var plus = informational.IndexOf('+');
        if (plus < 0)
            return new VersionInfo(informational, "", "");

        var version = informational[..plus];
        var meta = informational[(plus + 1)..];

        // Expected meta: "g{sha}.{date}"
        if (meta.StartsWith('g'))
        {
            var dot = meta.IndexOf('.');
            if (dot > 1)
            {
                var sha = meta[1..dot];
                var date = meta[(dot + 1)..];
                if (sha == "nogit") sha = "";
                return new VersionInfo(version, sha, date);
            }
        }

        // Unrecognized metadata: keep it as a best-effort commit, no date.
        return new VersionInfo(version, meta, "");
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~VersionInfoTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add FatGuysSpeak.Shared/VersionInfo.cs FatGuysSpeak.Tests/Server/VersionInfoTests.cs
git commit -m "$(cat <<'EOF'
Versioning: VersionInfo.Parse helper + tests

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Anonymous GET /api/version endpoint

**Files:**
- Modify: `FatGuysSpeak.Server/Program.cs` (line 1 usings; before `app.MapHub<ChatHub>` ~line 845)

- [ ] **Step 1: Add the using**

At the top of `Program.cs`, add after the existing `using System.Text;` (line 1):

```csharp
using System.Reflection;
```

- [ ] **Step 2: Add the endpoint**

In `Program.cs`, immediately BEFORE the line `app.MapHub<ChatHub>("/hubs/chat");` add:

```csharp
app.MapGet("/api/version", () =>
{
    var info = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    return Results.Ok(FatGuysSpeak.Shared.VersionInfo.Parse(info));
});
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build FatGuysSpeak.Server --framework net9.0`
Expected: Build succeeded. (Runtime check of the JSON happens in Final Verification.)

- [ ] **Step 4: Commit**

```bash
git add FatGuysSpeak.Server/Program.cs
git commit -m "$(cat <<'EOF'
Versioning: anonymous GET /api/version endpoint

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Dashboard footer version (server-side render)

**Files:**
- Modify: `FatGuysSpeak.Server/Controllers/MetricsController.cs`

**Context:** The dashboard HTML is a `private const string Html = """..."""` served by `Dashboard()` (`=> Content(Html, "text/html");`). A const can't interpolate a runtime value, so add a `{{VERSION}}` token to the template and `String.Replace` it at serve time. The status-bar `<div class="status-bar">` (~line 506) is the footer.

- [ ] **Step 1: Add the usings**

At the top of `MetricsController.cs`, add:

```csharp
using System.Reflection;
using FatGuysSpeak.Shared;
```

- [ ] **Step 2: Add the `{{VERSION}}` token to the status bar**

In the `Html` constant, change the `status-bar` block (the `<span id="refreshNote" ...>` is the last child) to add a version span right after it:

```html
        <div class="status-bar">
          <span id="serverUrl" title="The address this server is currently listening on">http://localhost:5238</span>
          <span id="refreshNote" title="Overview and rate-limit charts update every 2–5 s automatically; open tabs refresh every 5 s">auto-refreshes every 2s</span>
          <span id="appVersion" title="Running build" style="color:#555">{{VERSION}}</span>
        </div>
```

- [ ] **Step 3: Replace the token at serve time**

Change the `Dashboard()` method from the expression body to:

```csharp
    [HttpGet("/dashboard")]
    public ContentResult Dashboard()
    {
        var v = VersionInfo.Parse(typeof(MetricsController).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        var label = $"FatGuysSpeak v{v.Version}"
            + (v.Commit.Length > 0 ? $" · {v.Commit}" : "")
            + (v.BuildDate.Length > 0 ? $" · {v.BuildDate}" : "");
        return Content(Html.Replace("{{VERSION}}", System.Net.WebUtility.HtmlEncode(label)), "text/html");
    }
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build FatGuysSpeak.Server --framework net9.0`
Expected: Build succeeded. (Visual check in Final Verification.)

- [ ] **Step 5: Commit**

```bash
git add FatGuysSpeak.Server/Controllers/MetricsController.cs
git commit -m "$(cat <<'EOF'
Versioning: show running build version in dashboard footer

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Client SettingsPage "About" version

**Files:**
- Modify: `FatGuysSpeak.Client/ViewModels/SettingsViewModel.cs`
- Modify: `FatGuysSpeak.Client/Pages/SettingsPage.xaml`

**Context:** `SettingsPage.xaml` is a `VerticalStackLayout` of sections, each a `VerticalStackLayout` with a header `<Label Text="APPEARANCE"/>` etc., bound to `SettingsViewModel`. Add an "ABOUT" section showing the version. READ `SettingsViewModel.cs` first to confirm the class name/namespace and match its property style (it likely uses `CommunityToolkit.Mvvm` `[ObservableProperty]` or plain properties).

- [ ] **Step 1: Add an `AppVersion` property to `SettingsViewModel`**

Add a read-only computed property (adjust to the file's existing using/style; it needs `System.Reflection` and `FatGuysSpeak.Shared`):

```csharp
    public string AppVersion
    {
        get
        {
            var info = System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var v = FatGuysSpeak.Shared.VersionInfo.Parse(info);
            return v.Commit.Length > 0 ? $"{v.Version} ({v.Commit})" : v.Version;
        }
    }
```

- [ ] **Step 2: Add the ABOUT section to `SettingsPage.xaml`**

Inside the outer `<VerticalStackLayout Padding="24,20" Spacing="28">`, add a new section (place it last, after the existing sections):

```xml
                <VerticalStackLayout Spacing="10">
                    <Label Text="ABOUT" TextColor="#666666" FontSize="10" FontAttributes="Bold" />
                    <Grid ColumnDefinitions="*,Auto">
                        <Label Grid.Column="0" Text="Version" TextColor="#d0d0d0" />
                        <Label Grid.Column="1" Text="{Binding AppVersion}" TextColor="#888888" />
                    </Grid>
                </VerticalStackLayout>
```

(Match the existing rows' styling — copy the `TextColor`/layout from the adjacent "ACCOUNT" → "Username" row if these differ in the real file.)

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
Expected: Build succeeded. (Visual check in Final Verification.)

- [ ] **Step 4: Commit**

```bash
git add FatGuysSpeak.Client/ViewModels/SettingsViewModel.cs FatGuysSpeak.Client/Pages/SettingsPage.xaml
git commit -m "$(cat <<'EOF'
Versioning: show app version in client Settings (About)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: CHANGELOG.md + landing-page version label

**Files:**
- Create: `CHANGELOG.md` (repo root)
- Modify: `website/index.html` and `docs/index.html` (footer, ~line 1315)

- [ ] **Step 1: Create `CHANGELOG.md`**

```markdown
# Changelog

All notable changes to FatGuysSpeak are documented here. This project adheres to
[Semantic Versioning](https://semver.org/) and the [Keep a Changelog](https://keepachangelog.com/) format.

## [Unreleased]

## [1.0.0] - 2026-06-17
### Added
- Real-time text chat with reactions, replies, threads, attachments, and Group DMs.
- Push-to-talk voice (Opus, 48 kHz), screen share and webcam streaming, Whisper STT.
- Remote desktop control during a screen share (request → approve → drive), with a
  server-gated single controller and an instant Stop / panic-key.
- Admin dashboard: live metrics, user management, channel permissions, word filter, audit log.
- Message Log moderation console: content/author/channel/date search, full-history paging,
  multi-select and criteria-based delete, restore, and CSV export.
- Per-server default (Lobby) channel that can be renamed but not deleted.
- Sign in with Google (server-side validation + Windows client loopback OAuth).
- Security hardening: SSRF-safe previews/webhooks, per-IP rate limiting, signature-checked
  uploads, strict dashboard CSP, BCrypt timing-safe auth, JWT session blacklisting.
```

- [ ] **Step 2: Add a version label to the landing-page footer**

In `website/index.html`, inside `<footer>` immediately after the `<div class="footer-logo">...</div>` line (~1316), add:

```html
    <div class="footer-ver" style="color:#555;font-size:12px;margin-top:6px">v1.0.0</div>
```

- [ ] **Step 3: Mirror to `docs/index.html`**

Copy the source page over the published copy so they stay byte-identical:
```
cp website/index.html docs/index.html
```
Then confirm: `diff -q website/index.html docs/index.html` (no output) and `grep -c 'footer-ver' website/index.html docs/index.html` (each 1).

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md website/index.html docs/index.html
git commit -m "$(cat <<'EOF'
Versioning: seed CHANGELOG (1.0.0) + landing-page version label

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: release.ps1

**Files:**
- Create: `release.ps1` (repo root)

- [ ] **Step 1: Create `release.ps1`**

```powershell
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
if (-not $unreleased) { throw 'CHANGELOG [Unreleased] is empty — add entries before releasing.' }
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
```

- [ ] **Step 2: Ensure `release-output/` is gitignored**

Check `.gitignore` contains `release-output/`; if not, append it:
```
release-output/
```
(Commit the `.gitignore` change with the script if you add it.)

- [ ] **Step 3: Verify the validation gates (safe — these abort before mutating)**

Run each and confirm it aborts with the stated message and leaves the tree unchanged (`git status` clean after):
```
pwsh -File release.ps1 abc           # -> "not SemVer"
pwsh -File release.ps1 1.0.0         # -> "must be greater than current 1.0.0"
```
(Use `powershell -File` if `pwsh` is unavailable.) After both, run `git status --porcelain` — expect no output (no files changed).

- [ ] **Step 4: Commit**

```bash
git add release.ps1 .gitignore
git commit -m "$(cat <<'EOF'
Versioning: release.ps1 (bump + changelog + stamp + build + commit + tag)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Final verification (after all tasks)

- [ ] `dotnet test FatGuysSpeak.Tests` — all green (incl. `VersionInfoTests`).
- [ ] Stop running apps, build server (net9.0-windows) + client, relaunch via `launch.ps1`.
- [ ] `curl http://localhost:5238/api/version` (or `Invoke-RestMethod`) → `{ "version": "1.0.0", "commit": "<sha>", "buildDate": "<date>" }`; confirm `commit` matches `git rev-parse --short HEAD`.
- [ ] Open `http://localhost:5238/dashboard` (login) → footer shows `FatGuysSpeak v1.0.0 · <sha> · <date>`.
- [ ] In a client window → Settings → ABOUT shows `Version 1.0.0 (<sha>)`.
- [ ] Open `website/index.html` → footer shows `v1.0.0`.
- [ ] End-to-end release dry run. The release script requires a non-empty `## [Unreleased]`, so first add a throwaway entry and commit it (a release needs a clean tree):
  - Edit `CHANGELOG.md`: under `## [Unreleased]` add `### Added` and `- (release.ps1 smoke test)`.
  - `git add CHANGELOG.md && git commit -m "test: temp changelog entry"`
  - `pwsh -File release.ps1 1.0.1` → confirm it bumps `Directory.Build.props` to 1.0.1, moves the Unreleased entry into `## [1.0.1] - <date>` (leaving a fresh empty Unreleased), stamps the landing pages to `v1.0.1`, builds, writes `release-output/FatGuysSpeak-Server-Setup-1.0.1.exe` + `FatGuysSpeak-Client-1.0.1.zip`, commits "Release 1.0.1", and creates tag `v1.0.1`. Confirm the script printed the "push" next-step and did NOT push.
  - REVERT (nothing was pushed): `git tag -d v1.0.1; git reset --hard HEAD~2` (drops the Release commit and the temp-entry commit).
- [ ] Dispatch a final code review over the whole branch.
