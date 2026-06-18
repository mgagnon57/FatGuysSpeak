# Major-Compatibility Version Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the client connect freely to any same-major server and auto-sync to a server's exact version only across a major-version gap, with in-app download progress and a graceful restart.

**Architecture:** A pure Shared compatibility helper (`SemVer.Major` + `VersionCompat.SameMajor`) decides whether a sync is needed. The client's `UpdateService` splits into a non-restarting `PrepareAsync` (download with progress) and an explicit `ApplyAndRestart`, so `MainViewModel` can drive a blocking progress overlay and a restart countdown. Same major ⇒ no UI, no restart. No server change.

**Tech Stack:** .NET MAUI (Windows) client, CommunityToolkit.Mvvm, Velopack 1.2.0, FatGuysSpeak.Shared, xUnit.

---

## File Structure

- `FatGuysSpeak.Shared/SemVer.cs` — add `Major(string?) -> int?` (pure).
- `FatGuysSpeak.Shared/VersionCompat.cs` — NEW: `SameMajor(string?, string?) -> bool` (pure).
- `FatGuysSpeak.Tests/Server/SemVerTests.cs` — add `Major` tests.
- `FatGuysSpeak.Tests/Server/VersionCompatTests.cs` — NEW.
- `FatGuysSpeak.Client/Services/UpdateService.cs` — replace `SyncToServerVersionAsync`/`UpdateSyncResult` with `PrepareAsync`/`ApplyAndRestart`/`UpdateSyncOutcome` (Windows + stub). This also overwrites the temporary `FATGUYS_UPDATE_FEED` demo override currently in the file, returning it to clean production code.
- `FatGuysSpeak.Client/ViewModels/MainViewModel.cs` — rewrite `SyncClientToServerAsync` (major-gated, progress, countdown); swap the once-per-session guard for a per-server-version one; new overlay observable state.
- `FatGuysSpeak.Client/Pages/MainPage.xaml` — replace the simple "syncing" toast with a blocking, page-spanning progress overlay; keep the mismatch banner.

---

### Task 1: Shared compatibility helpers (pure, TDD)

**Files:**
- Modify: `FatGuysSpeak.Shared/SemVer.cs`
- Create: `FatGuysSpeak.Shared/VersionCompat.cs`
- Modify (test): `FatGuysSpeak.Tests/Server/SemVerTests.cs`
- Create (test): `FatGuysSpeak.Tests/Server/VersionCompatTests.cs`

- [ ] **Step 1: Write failing tests for `SemVer.Major`** — append to `FatGuysSpeak.Tests/Server/SemVerTests.cs` (inside the existing `SemVerTests` class):

```csharp
    [Theory]
    [InlineData("1.2.3", 1)]
    [InlineData("v3.0.0", 3)]
    [InlineData("10.0.0", 10)]
    public void Major_ParsesMajorComponent(string v, int expected)
        => Assert.Equal(expected, SemVer.Major(v));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1.2")]
    public void Major_Unparseable_ReturnsNull(string? v)
        => Assert.Null(SemVer.Major(v));
```

- [ ] **Step 2: Create failing tests for `VersionCompat.SameMajor`** — `FatGuysSpeak.Tests/Server/VersionCompatTests.cs`:

```csharp
using FatGuysSpeak.Shared;
using Xunit;

namespace FatGuysSpeak.Tests.Server;

public class VersionCompatTests
{
    [Theory]
    [InlineData("1.0.0", "1.7.3", true)]   // same major, connect as-is
    [InlineData("1.5.0", "1.2.0", true)]   // same major, no needless downgrade
    [InlineData("v2.1.0", "2.9.9", true)]  // tolerant of v prefix
    [InlineData("1.0.0", "3.0.0", false)]  // cross-major -> sync up
    [InlineData("3.0.0", "1.0.0", false)]  // cross-major -> sync down
    public void SameMajor_ComparesMajorOnly(string a, string b, bool expected)
        => Assert.Equal(expected, VersionCompat.SameMajor(a, b));

    [Theory]
    [InlineData(null, "1.0.0")]
    [InlineData("1.0.0", null)]
    [InlineData("junk", "1.0.0")]
    public void SameMajor_Unparseable_ReturnsFalse(string? a, string? b)
        => Assert.False(VersionCompat.SameMajor(a, b));
}
```

- [ ] **Step 3: Run the tests, verify they fail** —
Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~VersionCompatTests|FullyQualifiedName~SemVerTests.Major"`
Expected: FAIL (compile error: `SemVer.Major` / `VersionCompat` do not exist).

- [ ] **Step 4: Add `SemVer.Major`** — in `FatGuysSpeak.Shared/SemVer.cs`, add inside the `SemVer` class (e.g. after `IsOutdated`):

```csharp
    // Major component of a valid MAJOR.MINOR.PATCH, else null. "v3.0.0" -> 3; junk -> null.
    public static int? Major(string? v)
    {
        var n = NormalizeTag(v);
        if (n is null) return null;
        return int.Parse(n.Split('.')[0]);
    }
```

- [ ] **Step 5: Create `VersionCompat`** — `FatGuysSpeak.Shared/VersionCompat.cs`:

```csharp
namespace FatGuysSpeak.Shared;

/// <summary>Client/server version compatibility. Same SemVer major == wire-compatible
/// (connect as-is); different major == breaking gap (client must sync). Pure, never throws.</summary>
public static class VersionCompat
{
    public static bool SameMajor(string? clientVersion, string? serverVersion)
    {
        var a = SemVer.Major(clientVersion);
        var b = SemVer.Major(serverVersion);
        return a is not null && b is not null && a == b;
    }
}
```

- [ ] **Step 6: Run the tests, verify they pass** —
Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~VersionCompatTests|FullyQualifiedName~SemVerTests.Major"`
Expected: PASS (all new theories green).

- [ ] **Step 7: Commit** —
```bash
git add FatGuysSpeak.Shared/SemVer.cs FatGuysSpeak.Shared/VersionCompat.cs FatGuysSpeak.Tests/Server/SemVerTests.cs FatGuysSpeak.Tests/Server/VersionCompatTests.cs
git commit -m "Add SemVer.Major + VersionCompat.SameMajor (major-based compatibility)"
```

---

### Task 2: UpdateService — Prepare/Apply split (Windows + stub)

No unit test (Velopack needs a real install; validated at runtime in Task 5). Verify by build.

**Files:**
- Modify: `FatGuysSpeak.Client/Services/UpdateService.cs` (full rewrite of both branches)

- [ ] **Step 1: Rewrite the file** — replace the entire contents of `FatGuysSpeak.Client/Services/UpdateService.cs` with:

```csharp
#if WINDOWS
using FatGuysSpeak.Shared;
using Velopack;
using Velopack.Sources;

namespace FatGuysSpeak.Client.Services;

public enum UpdateSyncOutcome { Compatible, Prepared, Unavailable }

/// <summary>Pins the client to the connected server's exact version via Velopack
/// (upgrade or downgrade). PrepareAsync downloads but never restarts; ApplyAndRestart
/// performs the swap+relaunch. Best-effort: failures return Unavailable, never throw.</summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/mgagnon57/FatGuysSpeak";

    /// <summary>The version Velopack reports as installed, or null in dev/unpackaged debug.</summary>
    private string? _installedVersion;
    private bool _installedVersionChecked;
    public string? InstalledVersion
    {
        get
        {
            if (_installedVersionChecked) return _installedVersion;
            _installedVersionChecked = true;
            try
            {
                var m = new UpdateManager(new GithubSource(RepoUrl, null, false));
                _installedVersion = m.IsInstalled ? m.CurrentVersion?.ToString() : null;
            }
            catch { _installedVersion = null; }
            return _installedVersion;
        }
    }

    private UpdateManager? _pendingMgr;
    private UpdateInfo? _pendingInfo;

    /// <summary>Resolve + download (with progress) the build matching the server's exact
    /// version. Does NOT restart. Caller invokes ApplyAndRestart on Prepared.</summary>
    public async Task<UpdateSyncOutcome> PrepareAsync(string serverVersion, IProgress<int>? downloadProgress = null)
    {
        try
        {
            var channel = UpdateChannel.ForVersion(serverVersion);
            if (channel is null) return UpdateSyncOutcome.Unavailable;

            var mgr = new UpdateManager(
                new GithubSource(RepoUrl, null, false),
                new UpdateOptions { ExplicitChannel = channel, AllowVersionDowngrade = true });

            if (!mgr.IsInstalled) return UpdateSyncOutcome.Compatible;  // dev / not Velopack-installed

            var info = await mgr.CheckForUpdatesAsync();
            if (info is null) return UpdateSyncOutcome.Unavailable;      // no published build for that version

            await mgr.DownloadUpdatesAsync(info, p => downloadProgress?.Report(p));

            _pendingMgr = mgr;
            _pendingInfo = info;
            return UpdateSyncOutcome.Prepared;
        }
        catch { return UpdateSyncOutcome.Unavailable; }
    }

    /// <summary>Apply the build downloaded by PrepareAsync and relaunch. Exits the process.</summary>
    public void ApplyAndRestart()
    {
        if (_pendingMgr is null || _pendingInfo is null) return;
        _pendingMgr.ApplyUpdatesAndRestart(_pendingInfo.TargetFullRelease);
    }
}
#else
namespace FatGuysSpeak.Client.Services;

public enum UpdateSyncOutcome { Compatible, Prepared, Unavailable }

public sealed class UpdateService
{
    public string? InstalledVersion => null;
    public Task<UpdateSyncOutcome> PrepareAsync(string serverVersion, IProgress<int>? downloadProgress = null)
        => Task.FromResult(UpdateSyncOutcome.Compatible);
    public void ApplyAndRestart() { }
}
#endif
```

- [ ] **Step 2: Build the client, verify it compiles** —
Run: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
Expected: build error in `MainViewModel.cs` (it still calls the removed `SyncToServerVersionAsync`/`UpdateSyncResult`). That is expected and fixed in Task 3 — confirm the error is ONLY about those symbols in `MainViewModel.cs`, not about `UpdateService.cs` itself.

- [ ] **Step 3: Commit** (the VM is fixed next task; commit UpdateService on its own) —
```bash
git add FatGuysSpeak.Client/Services/UpdateService.cs
git commit -m "UpdateService: split into non-restarting PrepareAsync + ApplyAndRestart"
```

---

### Task 3: MainViewModel — major-gated sync flow

**Files:**
- Modify: `FatGuysSpeak.Client/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Swap the once-per-session guard field** — at line ~16 replace:

```csharp
    private bool _versionSyncAttempted;
```
with:
```csharp
    private string? _versionSyncCheckedFor;   // server version last handled this session
```

- [ ] **Step 2: Replace the version-sync observable state** — replace the block at lines ~75-79:

```csharp
    // Version sync state
    [ObservableProperty] private bool _versionSyncing;
    [ObservableProperty] private string? _versionSyncText;
    [ObservableProperty] private bool _versionMismatch;
    [ObservableProperty] private string? _versionMismatchText;
```
with:
```csharp
    // Version sync state
    [ObservableProperty] private bool _versionSyncInProgress;   // blocking overlay visible
    [ObservableProperty] private string? _versionSyncTitle;     // "Updating to v3.0.0…"
    [ObservableProperty] private string? _versionSyncStage;     // "Downloading…" / "Installing…" / countdown
    [ObservableProperty] private double _versionSyncProgress;   // 0.0–1.0 for ProgressBar
    [ObservableProperty] private bool _versionMismatch;
    [ObservableProperty] private string? _versionMismatchText;
```

- [ ] **Step 3: Rewrite `SyncClientToServerAsync`** — replace the whole method (lines ~597-628) with:

```csharp
    private async Task SyncClientToServerAsync()
    {
        var serverVersion = await api.GetServerVersionAsync();
        if (string.IsNullOrEmpty(serverVersion)) return;          // can't evaluate -> connect as-is

        var mine = updateService.InstalledVersion;
        if (mine is null) return;                                 // dev / not Velopack-installed

        if (FatGuysSpeak.Shared.VersionCompat.SameMajor(mine, serverVersion))
            return;                                               // compatible -> connect, no UI

        if (_versionSyncCheckedFor == serverVersion) return;      // already handled this server this session
        _versionSyncCheckedFor = serverVersion;

        var downgrade = FatGuysSpeak.Shared.SemVer.Compare(mine, serverVersion) > 0;
        var verb = downgrade ? "Downgrading" : "Updating";
        MainThread.BeginInvokeOnMainThread(() =>
        {
            VersionMismatch = false;
            VersionSyncTitle = $"{verb} to v{serverVersion}…";
            VersionSyncStage = "Downloading…";
            VersionSyncProgress = 0;
            VersionSyncInProgress = true;
        });

        var progress = new Progress<int>(p =>
            MainThread.BeginInvokeOnMainThread(() => VersionSyncProgress = p / 100.0));

        var outcome = await updateService.PrepareAsync(serverVersion, progress);

        if (outcome == Services.UpdateSyncOutcome.Prepared)
        {
            MainThread.BeginInvokeOnMainThread(() => VersionSyncStage = "Installing…");
            await Task.Delay(600);
            for (var n = 3; n >= 1; n--)
            {
                var sec = n;
                MainThread.BeginInvokeOnMainThread(() =>
                    VersionSyncStage = $"Restarting to apply v{serverVersion} in {sec}…");
                await Task.Delay(1000);
            }
            updateService.ApplyAndRestart();                      // swaps files + relaunches; process exits
            return;
        }

        // Compatible (defensive) or Unavailable -> tear down the overlay
        MainThread.BeginInvokeOnMainThread(() =>
        {
            VersionSyncInProgress = false;
            if (outcome == Services.UpdateSyncOutcome.Unavailable)
            {
                VersionMismatchText = $"This server needs client v{serverVersion}, but auto-update isn't "
                    + $"available right now (you're on v{mine}). It'll retry next time you connect.";
                VersionMismatch = true;
            }
        });
    }
```

Note: the `_ = SyncClientToServerAsync();` trigger at line ~594 (end of `LoadServersAsync`) is unchanged — it already fires on each connect, and switching the configured backend URL re-runs `LoadServersAsync`. The `DismissMismatch` command at line ~630-631 is unchanged.

- [ ] **Step 4: Build the client, verify it compiles** —
Run: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
Expected: build error ONLY in `MainPage.xaml`/generated bindings is not expected (XAML binds resolve at runtime), so expect a clean build EXCEPT it will still succeed even with the old XAML bindings present (they bind to removed props but XAML binding is late-bound). Confirm: `0 Error(s)`.

- [ ] **Step 5: Commit** —
```bash
git add FatGuysSpeak.Client/ViewModels/MainViewModel.cs
git commit -m "MainViewModel: major-gated version sync with progress + restart countdown"
```

---

### Task 4: MainPage.xaml — blocking progress overlay

**Files:**
- Modify: `FatGuysSpeak.Client/Pages/MainPage.xaml`

- [ ] **Step 1: Remove the old "syncing" toast** — in the `<!-- ═══ VERSION SYNC BANNERS ═══ -->` block (lines ~74-83), delete the first inner `Grid` (the `VersionSyncing` toast) so the `VerticalStackLayout` at `Grid.Row="2"` keeps ONLY the mismatch banner:

```xml
        <!-- ═══ VERSION MISMATCH BANNER ═══ -->
        <VerticalStackLayout Grid.Row="2">
            <Grid IsVisible="{Binding VersionMismatch}" ColumnDefinitions="*,Auto" BackgroundColor="#3a2a00" Padding="12,6">
                <Label Grid.Column="0" VerticalOptions="Center" TextColor="#e8c060" Text="{Binding VersionMismatchText}" />
                <Button Grid.Column="1" Text="✕" Command="{Binding DismissMismatchCommand}"
                        BackgroundColor="Transparent" TextColor="#e8c060" Padding="6,2" />
            </Grid>
        </VerticalStackLayout>
```

- [ ] **Step 2: Add the blocking overlay as the LAST child of the root `<Grid RowDefinitions="Auto,Auto,Auto,*,Auto">`** — immediately before the root grid's closing `</Grid>` (the one that closes the layout opened at line ~38), add:

```xml
        <!-- ═══ VERSION SYNC OVERLAY (blocking; covers the whole page) ═══ -->
        <Grid Grid.RowSpan="5" IsVisible="{Binding VersionSyncInProgress}"
              BackgroundColor="#cc000814">
            <Border Stroke="#2a4a80" StrokeThickness="1" BackgroundColor="#0e1830"
                    Padding="28,22" WidthRequest="360"
                    HorizontalOptions="Center" VerticalOptions="Center"
                    StrokeShape="RoundRectangle 10">
                <VerticalStackLayout Spacing="14">
                    <Label Text="{Binding VersionSyncTitle}" TextColor="#cfe0ff"
                           FontAttributes="Bold" FontSize="15" HorizontalOptions="Center" />
                    <ProgressBar Progress="{Binding VersionSyncProgress}" ProgressColor="#5a8de0" />
                    <Label Text="{Binding VersionSyncStage}" TextColor="#90a8d0"
                           FontSize="12" HorizontalOptions="Center" />
                </VerticalStackLayout>
            </Border>
        </Grid>
```

(Placing it last in the root grid renders it on top; `Grid.RowSpan="5"` makes it cover all rows. Because it has a background and sits above the content, it blocks interaction while visible.)

- [ ] **Step 2b: Verify no stale bindings remain** —
Run: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
Expected: `0 Error(s)`. (The removed `VersionSyncing`/`VersionSyncText` bindings must no longer appear anywhere in the XAML.)

- [ ] **Step 3: Run the full test suite (no regressions in Shared/Server)** —
Run: `dotnet test FatGuysSpeak.Tests`
Expected: all green (pre-existing count + the new Task 1 theories).

- [ ] **Step 4: Commit** —
```bash
git add FatGuysSpeak.Client/Pages/MainPage.xaml
git commit -m "MainPage: blocking version-sync progress overlay; drop simple toast"
```

---

### Task 5: Runtime acceptance (manual — Velopack cross-major, local feed)

Not unit-testable. This proves the cross-major path + new overlay/countdown on real installs. Done by the maintainer; uses a throwaway local feed and a temporary feed override (NOT committed).

- [ ] **Step 1:** Temporarily point `UpdateService.PrepareAsync` + `InstalledVersion` at a local feed for testing — add, only for this session, an env-var override (`FATGUYS_UPDATE_FEED`) around the `new UpdateManager(...)` source (as used in the 2026-06-18 demo). Do NOT commit this.
- [ ] **Step 2:** Publish the client once (`dotnet publish -c Release -f net9.0-windows10.0.19041.0`). `vpk pack` it as a 1.x version on channel `v1-x-y` into a local feed dir, and as a 2.0.0 version on channel `v2-0-0` into the same feed dir.
- [ ] **Step 3:** Install the 1.x build (its Setup.exe). Run a server reporting a 2.0.0 version (temporarily set the server's `<Version>` to 2.0.0 for the test, or stub `/api/version`).
- [ ] **Step 4:** Log in. Confirm: the blocking overlay appears, the title reads "Updating to v2.0.0…", the progress bar advances, stage moves Downloading → Installing → "Restarting to apply v2.0.0 in 3…2…1…", then the app relaunches and reconnects on 2.0.0 with no further prompt (now same major).
- [ ] **Step 5:** Reverse it: install 2.0.0, point at a 1.x server → confirm "Downgrading to v1.x…" syncs down and relaunches.
- [ ] **Step 6:** Same-major check: 1.x client against a different 1.y server → confirm it connects with NO overlay and NO restart.
- [ ] **Step 7:** Unavailable check: cross-major server whose version has no packed build in the feed → confirm the persistent mismatch banner shows and the app stays usable.
- [ ] **Step 8:** Remove the temporary feed override, the `FATGUYS_UPDATE_FEED` env var, revert any temporary server `<Version>` change, and delete the local feed/publish dirs. Confirm the committed `UpdateService.cs` uses only `GithubSource`.

---

## Self-Review

- **Spec coverage:** same-major-connect (Task 1 logic + Task 3), cross-major-sync up/down (Task 2 + 3, Task 5 acceptance), in-app progress + countdown (Task 3 + 4), re-check per connect via per-version guard (Task 3), Unavailable banner (Task 3 + 4), no server change (none present), pure unit tests (Task 1) — all covered.
- **Placeholder scan:** none; every code step is complete.
- **Type consistency:** `UpdateSyncOutcome { Compatible, Prepared, Unavailable }` defined in Task 2 and consumed in Task 3; `PrepareAsync(string, IProgress<int>?)` / `ApplyAndRestart()` / `InstalledVersion` signatures match between Task 2 and Task 3; `VersionSyncInProgress/Title/Stage/Progress` props defined in Task 3 and bound in Task 4; `VersionSyncProgress` is a `double` (0–1) to bind `ProgressBar.Progress` directly (no converter); `SemVer.Major`/`VersionCompat.SameMajor` defined in Task 1 and used in Task 3.
