# Client Auto-Update Pinned to Server Version — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** On connect, the client reads the server's version and auto up/downgrades itself to that exact version via Velopack (notifying), falling back to a persistent banner if it can't.

**Architecture:** A pure `UpdateChannel.ForVersion` helper maps a version to a Velopack per-version channel. The Windows client adds Velopack (`VelopackApp.Build().Run()` hook), and an `UpdateService` builds a `GithubSource` `UpdateManager` pinned to the server's channel with `AllowVersionDowngrade`, then `ApplyUpdatesAndRestart`. `MainViewModel` triggers it after login and shows a toast/fallback banner; the old "newer release" client banner is removed. `release.ps1` publishes Velopack packages (default + per-version channel) to the GitHub release.

**Tech Stack:** Velopack 1.2.0 (`vpk` 1.2.0), .NET MAUI (unpackaged, `WindowsPackageType=None`), `FatGuysSpeak.Shared`, xUnit, PowerShell.

**Spec:** `docs/superpowers/specs/2026-06-17-client-server-version-pinning-design.md` (Phase-0 spike already proved the Velopack engine: pack + resolve-downgrade + apply, `2.0.0 -> 1.0.0`).

**Conventions:**
- Run shared tests: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~UpdateChannelTests"`
- Client build: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
- Stop running apps before a client build if a DLL lock appears: `Get-Process FatGuysSpeak.Server,FatGuysSpeak.Client -ErrorAction SilentlyContinue | Stop-Process -Force` (PowerShell).
- The repo is `https://github.com/mgagnon57/FatGuysSpeak`.
- Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- **Reality:** Tasks 2–5 are integration/distribution and largely NOT unit-testable; they are build-verified, then validated by the runtime acceptance at the end. Only Task 1 is TDD.

---

### Task 1: Shared `UpdateChannel.ForVersion` (pure, TDD)

**Files:**
- Create: `FatGuysSpeak.Shared/UpdateChannel.cs`
- Create: `FatGuysSpeak.Tests/Server/UpdateChannelTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `FatGuysSpeak.Tests/Server/UpdateChannelTests.cs`:

```csharp
using FatGuysSpeak.Shared;
using Xunit;

namespace FatGuysSpeak.Tests.Server;

public class UpdateChannelTests
{
    [Theory]
    [InlineData("1.2.0", "v1-2-0")]
    [InlineData("v1.2.0", "v1-2-0")]
    [InlineData("V2.0.3", "v2-0-3")]
    [InlineData("1.0.0", "v1-0-0")]
    public void ForVersion_MapsToPerVersionChannel(string version, string expected)
        => Assert.Equal(expected, UpdateChannel.ForVersion(version));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void ForVersion_NullForUnparseable(string? version)
        => Assert.Null(UpdateChannel.ForVersion(version));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~UpdateChannelTests"`
Expected: FAIL — `UpdateChannel` does not exist.

- [ ] **Step 3: Implement**

Create `FatGuysSpeak.Shared/UpdateChannel.cs`:

```csharp
namespace FatGuysSpeak.Shared;

/// <summary>Maps a product version to its Velopack per-version channel name, used identically
/// by the release publisher and the client so the client can pin to the server's exact version.
/// "1.2.0" -> "v1-2-0". Returns null for anything that isn't a MAJOR.MINOR.PATCH version.</summary>
public static class UpdateChannel
{
    public static string? ForVersion(string? version)
    {
        var norm = SemVer.NormalizeTag(version);   // strips leading v/V, validates X.Y.Z, else null
        return norm is null ? null : "v" + norm.Replace('.', '-');
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~UpdateChannelTests"`
Expected: PASS (all cases).

- [ ] **Step 5: Commit**

```bash
git add FatGuysSpeak.Shared/UpdateChannel.cs FatGuysSpeak.Tests/Server/UpdateChannelTests.cs
git commit -m "$(cat <<'EOF'
Version pinning: UpdateChannel.ForVersion helper + tests

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Add Velopack + startup hook to the client

**Files:**
- Modify: `FatGuysSpeak.Client/FatGuysSpeak.Client.csproj`
- Modify: `FatGuysSpeak.Client/MauiProgram.cs`

- [ ] **Step 1: Add the Velopack package**

In `FatGuysSpeak.Client/FatGuysSpeak.Client.csproj`, add a package reference (a Windows-target `ItemGroup` is fine, or unconditional — the package is cross-TFM but only used under `#if WINDOWS`). Add to the existing `<ItemGroup>` that holds PackageReferences:

```xml
		<PackageReference Include="Velopack" Version="1.2.0" />
```

- [ ] **Step 2: Add the `VelopackApp.Build().Run()` hook (Windows-only) at the very start of CreateMauiApp**

In `FatGuysSpeak.Client/MauiProgram.cs`, make the FIRST statement of `CreateMauiApp()`:

```csharp
    public static MauiApp CreateMauiApp()
    {
#if WINDOWS
        Velopack.VelopackApp.Build().Run();
#endif
        var builder = MauiApp.CreateBuilder();
```

(This runs before MAUI initializes the window. When Velopack relaunches the app with its hook args during install/update, `Run()` handles them and exits; in normal launches it's a no-op. It must be the first line.)

- [ ] **Step 3: Build to verify it compiles and the app still launches**

```
Get-Process FatGuysSpeak.Server,FatGuysSpeak.Client -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0
```
Expected: Build succeeded, 0 errors. Then launch the client (`& C:\FatGuysSpeak\launch.ps1`) and confirm a client window opens normally (the hook no-ops when not invoked by Velopack). The login screen appearing = pass.

- [ ] **Step 4: Commit**

```bash
git add FatGuysSpeak.Client/FatGuysSpeak.Client.csproj FatGuysSpeak.Client/MauiProgram.cs
git commit -m "$(cat <<'EOF'
Version pinning: add Velopack + startup hook to the client

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Client `UpdateService` (pin to server version)

**Files:**
- Create: `FatGuysSpeak.Client/Services/UpdateService.cs`
- Modify: `FatGuysSpeak.Client/MauiProgram.cs` (DI registration)

**Context:** Velopack 1.2.0 API confirmed by the spike: `new UpdateManager(IUpdateSource, UpdateOptions)`, `.IsInstalled`, `.CurrentVersion`, `await CheckForUpdatesAsync() -> UpdateInfo?{ TargetFullRelease, IsDowngrade }`, `await DownloadUpdatesAsync(info)`, `ApplyUpdatesAndRestart(VelopackAsset)`. Source: `new Velopack.Sources.GithubSource(repoUrl, accessToken:null, prerelease:false)`.

- [ ] **Step 1: Create the service (Windows real + non-Windows stub)**

Create `FatGuysSpeak.Client/Services/UpdateService.cs`:

```csharp
#if WINDOWS
using FatGuysSpeak.Shared;
using Velopack;
using Velopack.Sources;

namespace FatGuysSpeak.Client.Services;

public enum UpdateSyncResult { UpToDate, Applying, Unavailable }

/// <summary>Pins the client to the connected server's exact version via Velopack
/// (upgrade or downgrade). Best-effort: failures return Unavailable, never throw.</summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/mgagnon57/FatGuysSpeak";

    /// <summary>The version Velopack reports as installed, or null in dev/unpackaged debug.</summary>
    public string? InstalledVersion
    {
        get
        {
            try { var m = new UpdateManager(RepoUrl); return m.IsInstalled ? m.CurrentVersion?.ToString() : null; }
            catch { return null; }
        }
    }

    public async Task<UpdateSyncResult> SyncToServerVersionAsync(string serverVersion)
    {
        try
        {
            var channel = UpdateChannel.ForVersion(serverVersion);
            if (channel is null) return UpdateSyncResult.Unavailable;

            var mgr = new UpdateManager(
                new GithubSource(RepoUrl, null, false),
                new UpdateOptions { ExplicitChannel = channel, AllowVersionDowngrade = true });

            if (!mgr.IsInstalled) return UpdateSyncResult.UpToDate;   // dev / not Velopack-installed

            var info = await mgr.CheckForUpdatesAsync();
            if (info is null) return UpdateSyncResult.UpToDate;        // already matches the server

            await mgr.DownloadUpdatesAsync(info);
            mgr.ApplyUpdatesAndRestart(info.TargetFullRelease);        // exits + relaunches at server version
            return UpdateSyncResult.Applying;
        }
        catch { return UpdateSyncResult.Unavailable; }
    }
}
#else
namespace FatGuysSpeak.Client.Services;

public enum UpdateSyncResult { UpToDate, Applying, Unavailable }

public sealed class UpdateService
{
    public string? InstalledVersion => null;
    public Task<UpdateSyncResult> SyncToServerVersionAsync(string serverVersion)
        => Task.FromResult(UpdateSyncResult.UpToDate);
}
#endif
```

- [ ] **Step 2: Register in DI**

In `FatGuysSpeak.Client/MauiProgram.cs`, where other services are registered (`builder.Services.AddSingleton<...>()`), add:

```csharp
        builder.Services.AddSingleton<FatGuysSpeak.Client.Services.UpdateService>();
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add FatGuysSpeak.Client/Services/UpdateService.cs FatGuysSpeak.Client/MauiProgram.cs
git commit -m "$(cat <<'EOF'
Version pinning: UpdateService (pin client to server version via Velopack)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Wire the sync into login; replace the old client banner

**Files:**
- Modify: `FatGuysSpeak.Client/Services/ApiService.cs`
- Modify: `FatGuysSpeak.Client/ViewModels/MainViewModel.cs`
- Modify: `FatGuysSpeak.Client/Pages/MainPage.xaml`

**Context:** The previous "newer release" notify lives in `MainViewModel` (lines ~76-78 observable props `_updateAvailable`/`_latestVersion`/`_updateUrl`; line ~593 `_ = CheckForUpdatesAsync();`; ~596-618 `CheckForUpdatesAsync`/`OpenUpdate`/`DismissUpdate`) and `ApiService.GetUpdateStatusAsync` (~94-97) and the `UpdateAvailable` banner in `Pages/MainPage.xaml`. This task removes those and replaces them with the server-pinning flow. READ each file's actual current code before editing.

- [ ] **Step 1: Add a server-version fetch to ApiService; remove the old update-status call**

In `ApiService.cs`, replace `GetUpdateStatusAsync` with a server-version fetch (the client now needs the server's version, not the latest release):

```csharp
    public async Task<string?> GetServerVersionAsync()
    {
        try
        {
            var v = await _http.GetFromJsonAsync<VersionInfo>("api/version");
            return v?.Version;
        }
        catch { return null; }
    }
```

(Delete the old `GetUpdateStatusAsync` method. `VersionInfo` is in `FatGuysSpeak.Shared`, already imported. The server's `/api/version` returns `{version, commit, buildDate}`.)

- [ ] **Step 2: MainViewModel — remove old banner state/commands, add sync state**

In `MainViewModel.cs`:
- DELETE the three observable props `_updateAvailable`, `_latestVersion`, `_updateUrl` (~lines 76-78).
- DELETE `CheckForUpdatesAsync`, `[RelayCommand] OpenUpdate`, `[RelayCommand] DismissUpdate` (~596-618).
- ADD observable state:
```csharp
    [ObservableProperty] private bool _versionSyncing;     // transient toast while applying
    [ObservableProperty] private string? _versionSyncText; // "Updating to v1.3.0…" / "Downgrading to v1.1.0…"
    [ObservableProperty] private bool _versionMismatch;    // persistent fallback banner
    [ObservableProperty] private string? _versionMismatchText;
```
- ADD the `UpdateService` to the VM's primary constructor parameter list (next to the other services; match the file's style) as `updateService`.

- [ ] **Step 3: MainViewModel — replace the post-login trigger**

At line ~593, REPLACE `_ = CheckForUpdatesAsync();` with `_ = SyncClientToServerAsync();` and add the method:

```csharp
    public async Task SyncClientToServerAsync()
    {
        var serverVersion = await api.GetServerVersionAsync();
        if (string.IsNullOrEmpty(serverVersion)) return;

        var mine = updateService.InstalledVersion;
        if (mine is null) return;  // dev / not Velopack-installed -> can't self-update

        if (FatGuysSpeak.Shared.SemVer.Compare(mine, serverVersion) == 0) return;  // already matched

        var downgrade = FatGuysSpeak.Shared.SemVer.Compare(mine, serverVersion) > 0;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            VersionSyncText = downgrade ? $"Downgrading to v{serverVersion}…" : $"Updating to v{serverVersion}…";
            VersionSyncing = true;
        });

        var result = await updateService.SyncToServerVersionAsync(serverVersion);
        // Applying -> the app is about to restart. Otherwise clear the toast and, if it
        // couldn't update, show the persistent mismatch banner.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            VersionSyncing = false;
            if (result == UpdateSyncResult.Unavailable)
            {
                VersionMismatchText = $"Your client (v{mine}) doesn't match the server (v{serverVersion}) "
                    + "and auto-update isn't available — continue, or update manually.";
                VersionMismatch = true;
            }
        });
    }
```

(`api` and `updateService` are the injected field names — match the constructor. `UpdateSyncResult` is in `FatGuysSpeak.Client.Services`; add a `using` if needed.)

- [ ] **Step 4: MainPage.xaml — replace the banner**

In `Pages/MainPage.xaml`, REMOVE the old `IsVisible="{Binding UpdateAvailable}"` banner grid (with its Download/✕ buttons). In the same spot add two:

```xml
                <!-- transient version-sync toast -->
                <Grid IsVisible="{Binding VersionSyncing}" BackgroundColor="#143060" Padding="12,6">
                    <Label TextColor="#cfe0ff" VerticalOptions="Center" Text="{Binding VersionSyncText}" />
                </Grid>
                <!-- persistent mismatch fallback -->
                <Grid IsVisible="{Binding VersionMismatch}" ColumnDefinitions="*,Auto" BackgroundColor="#3a2a00" Padding="12,6">
                    <Label Grid.Column="0" VerticalOptions="Center" TextColor="#e8c060" Text="{Binding VersionMismatchText}" />
                    <Button Grid.Column="1" Text="✕" Command="{Binding DismissMismatchCommand}"
                            BackgroundColor="Transparent" TextColor="#e8c060" Padding="6,2" />
                </Grid>
```

Add the dismiss command to `MainViewModel`:
```csharp
    [RelayCommand]
    private void DismissMismatch() => VersionMismatch = false;
```

(Keep the row layout consistent — the old banner occupied a grid row; reuse that row for these. If the old banner was its own row in the page's outer grid, place both new grids in that row inside a vertical `StackLayout`, or give one its own row. Match the real file's structure.)

- [ ] **Step 5: Build to verify it compiles**

Run: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
Expected: Build succeeded, 0 errors. Confirm no remaining references to the removed members (`UpdateAvailable`, `OpenUpdateCommand`, `DismissUpdateCommand`, `GetUpdateStatusAsync`) anywhere — grep them; build will fail if any remain.

- [ ] **Step 6: Commit**

```bash
git add FatGuysSpeak.Client/Services/ApiService.cs FatGuysSpeak.Client/ViewModels/MainViewModel.cs FatGuysSpeak.Client/Pages/MainPage.xaml
git commit -m "$(cat <<'EOF'
Version pinning: sync client to server version on login; replace old update banner

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Velopack distribution in release.ps1 + landing page

**Files:**
- Modify: `release.ps1`
- Modify: `website/index.html`, `docs/index.html`

**Context:** `release.ps1` already builds + commits + tags. Add Velopack packaging of the client to both the default channel (public download) and the per-version channel (server pinning), uploaded to the `vX.Y.Z` GitHub release. Requires the `vpk` tool (`dotnet tool install -g vpk`, v1.2.0) and a GitHub token in `$env:GITHUB_TOKEN` for upload. The `win10-x64` is the client RID.

- [ ] **Step 1: Add a Velopack publish step to release.ps1**

In `release.ps1`, after the existing client build and BEFORE the git commit/tag step, add (the per-version channel uses the same mapping as `UpdateChannel.ForVersion`):

```powershell
# --- Velopack: package the client and publish to the GitHub release ---
$repoUrl = 'https://github.com/mgagnon57/FatGuysSpeak'
$verChannel = 'v' + ($Version -replace '\.', '-')   # 1.2.0 -> v1-2-0  (matches UpdateChannel.ForVersion)
$clientPub = Join-Path $root 'FatGuysSpeak.Client\bin\Release\net9.0-windows10.0.19041.0\win10-x64'
if (-not (Test-Path $clientPub)) { throw "Client publish output not found at $clientPub." }
$vpkOut = Join-Path $root 'release-output\vpk'
New-Item -ItemType Directory -Force -Path $vpkOut | Out-Null

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
    Write-Host "GITHUB_TOKEN not set — skipped vpk upload. Packages are in $vpkOut." -ForegroundColor Yellow
}
```

(`vpk pack` needs the client published first; the existing release build produces the `win10-x64` output. If a separate `dotnet publish -c Release -r win10-x64` is required to get a complete folder, add it before the `vpk pack` loop.)

- [ ] **Step 2: Point the landing-page "Download Client" at the Velopack Setup.exe**

In `website/index.html`, change the two `FatGuysSpeak-Client-Setup.zip` hrefs to the Velopack default-channel Setup asset (Velopack's GitHub upload names it `FatGuysSpeak.Client-win-Setup.exe`):

```
https://github.com/mgagnon57/FatGuysSpeak/releases/latest/download/FatGuysSpeak.Client-win-Setup.exe
```

Update the adjacent `dl-ver` text from "Extract ZIP · run …" to "Run FatGuysSpeak.Client-win-Setup.exe". Then sync: `cp website/index.html docs/index.html` and confirm `diff -q website/index.html docs/index.html` is empty.

- [ ] **Step 3: Verify the script packs (no upload)**

With no `GITHUB_TOKEN` set, the upload is skipped, so this is safe to run for a packaging smoke once the client builds in Release. Confirm `vpk pack` produces both channels under `release-output/vpk/win` and `release-output/vpk/<verChannel>` (each with `Setup.exe`, `RELEASES-*.json`, `.nupkg`). Do NOT run a full `release.ps1` here (it commits/tags); instead validate the vpk block by extracting it or running it against the current Release client output. (`release-output/` is gitignored.)

- [ ] **Step 4: Commit**

```bash
git add release.ps1 website/index.html docs/index.html
git commit -m "$(cat <<'EOF'
Version pinning: publish Velopack client (default + per-version channel) in release.ps1

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Final verification (runtime acceptance — cannot be unit-tested)

The Phase-0 spike already proved the Velopack engine (pack, resolve-downgrade, apply). Full feature acceptance requires real Velopack releases and a GUI client, performed once by the maintainer:

- [ ] `dotnet test FatGuysSpeak.Tests` — green (incl. `UpdateChannelTests`).
- [ ] Build the client; launch via `launch.ps1`; confirm it starts normally with the Velopack hook in place (dev/unpackaged → `SyncClientToServerAsync` no-ops because `InstalledVersion` is null; no banner, no error).
- [ ] Validate the GitHub channel feed (the one piece not covered by the spike): publish two Velopack releases (e.g. `1.2.0` and a `1.3.0`) to the repo via `release.ps1` (with `GITHUB_TOKEN`), each to both the `win` and per-version channels. Install the Velopack client from the `win` channel Setup.exe.
  - Connect to a server running `1.3.0` → client shows "Updating to v1.3.0…" and relaunches at 1.3.0.
  - Point the same client at a server running `1.2.0` → client shows "Downgrading to v1.2.0…" and relaunches at 1.2.0.
  - Connect to a server whose version has no Velopack release (or go offline) → the persistent "version mismatch / auto-update unavailable" banner shows and the app stays usable.
  - If the GitHub per-version channel feed doesn't resolve as expected, fall back to a self-hosted static HTTP feed (`SimpleWebSource`) — the update engine is identical (proven); only the feed host changes.
- [ ] Dispatch a final code review over the whole branch.
