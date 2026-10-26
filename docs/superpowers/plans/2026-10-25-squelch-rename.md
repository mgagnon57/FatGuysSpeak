# Squelch Rename Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename FatGuysSpeak to Squelch all the way down, swap in the knob icon, add a voice-state tray icon, move the Azure server, and hand the owner's installed client over to Squelch with no manual steps.

**Architecture:** One mechanical rename commit first, so every later file is born with Squelch names. New logic that must be tested (tray frame choice, old-settings rewrite, stored-URL rewrite) is pure code in Shared/Server with xUnit tests. The bridge release 0.13.2 is built on a separate branch from the `v0.13.1` tag, ships under the OLD names, and does only the handover. Production steps happen last, after a full local rehearsal.

**Tech Stack:** .NET 9, MAUI (WinUI), ASP.NET Core + SignalR, EF Core SQLite, Velopack (`vpk`), Azure App Service (Linux) via `az` CLI and the `azure-deploy.yml` workflow, GitHub Pages, `gh` CLI.

**Spec:** `docs/superpowers/specs/2026-10-25-squelch-rename-design.md`

## Global Constraints

- New name: `Squelch`. Namespaces/projects `Squelch.Server`, `Squelch.Client`, `Squelch.Shared`, `Squelch.Tests`, `Squelch.Installer`, `Squelch.ClientInstaller`; solution `Squelch.sln`.
- Versions: Squelch **0.14.0**; bridge FatGuysSpeak **0.13.2**. 1.0 stays reserved.
- Velopack: `--packId Squelch.Client`, `--mainExe Squelch.Client.exe`, release name `Squelch X.Y.Z`, channels `win` + `vX-Y-Z`.
- MAUI: `ApplicationId` `com.squelch.client`, `ApplicationTitle` `Squelch`. Leave the Publisher/Company default.
- Old server URL `https://fatguysspeak-server.azurewebsites.net`; new `https://squelch-server.azurewebsites.net` (web app `squelch-server` in plan `fatguysspeak-plan`, rg `fatguysspeak-rg`, West US 2).
- Repo: `mgagnon57/FatGuysSpeak` → `mgagnon57/Squelch`. Website domain `getsquelch.com` (owner has registered it).
- Default DB file `squelch.db` (was `fatguys.db`).
- Icon source: `C:\ClaudeWork\Squelch\branding\icon` — copy files into the repo, never reference that path at build time. Colors: black `#0B0B0D`, red `#E5202E`, unlit `#3A3A40`.
- PorkChop keeps the pig art for his avatar. Channel names unchanged. Past CHANGELOG entries and past `docs/superpowers/` files unchanged. Message history and AI text unchanged.
- Closing the main window quits Squelch (no minimise-to-tray).
- Voice is never captured beyond the existing in-memory level; no STT, no storage (CLAUDE.md legal rule).
- Unit tests for every new piece of logic (project rule). Always launch two clients when manually testing; launch hidden; launch the server from the project dir.
- Irreversible outward actions (GitHub repo rename, creating/stopping Azure apps, publishing releases, DNS) are announced to the owner right before they run.

## Review Focus

1. Old prefs with `server_url` written with a trailing slash or different case (`https://FatGuysSpeak-server.azurewebsites.net/`) — must still be rewritten to the new URL. Test in Task 3.
2. `saved_servers` holding both old and new URLs, or duplicates (the owner's real file has `http://localhost:5238` twice) — result must be de-duplicated and keep order. Test in Task 3.
3. Attachment/avatar URLs stored with `http://` (Azure's proxy can make `Request.Scheme` http) — must be rewritten too, scheme preserved. Test in Task 4.
4. Squelch launched when its own settings already exist (second launch, or a fresh install after the move) — the import must not run again and overwrite newer settings. Test in Task 3.
5. Mic level flickering around the noise-gate threshold — the tray must not swap icons more than ~12 times a second. Test in Task 2.

---

### Task 1: Mechanical rename of code, scripts and config

**Files:**
- Rename: every `FatGuysSpeak.*` project folder and `.csproj` to `Squelch.*`; `FatGuysSpeak.sln` → `Squelch.sln`
- Modify: all `.cs`/`.xaml`/`.csproj`/`.props`/`.ps1`/`.yml`/`Dockerfile`/`.dockerignore`/`.gitignore`/`CLAUDE.md` mentions
- Modify: `Squelch.Client/Squelch.Client.csproj` (`RootNamespace`, `ApplicationTitle`, `ApplicationId`)
- Modify: `Squelch.Client/Services/ApiService.cs:15` (`DefaultServerUrl`), `Squelch.Client/Services/UpdateService.cs:15` (`RepoUrl` → `https://github.com/mgagnon57/Squelch`)
- Modify: `Squelch.Server/Program.cs:61` (`Data Source=squelch.db`), `:90` (`SetApplicationName("Squelch")`), `:266` (User-Agent `Squelch`), `Jwt:Issuer`/`Jwt:Audience` values in appsettings*.json
- Modify: `release.ps1` (packId, mainExe, exe/zip/setup names, `$repoUrl`, release name), `launch.ps1`, `build-*.ps1`, `test-e2e.ps1`, `.github/workflows/azure-deploy.yml` (project path, `app-name: squelch-server`)
- Modify: `Directory.Build.props` `<Version>0.14.0</Version>`
- Modify: env var names `FATGUYS_*` → `SQUELCH_*` (e.g. `FATGUYS_PHONE_MODE` in `App.xaml.cs`)
- Rename local dev DB: `Squelch.Server/fatguys.db` → `squelch.db` (gitignored; keep dev data)

**Interfaces:**
- Produces: the Squelch project/namespace names every later task uses.

- [ ] **Step 1:** `git mv` each project folder, csproj and the sln; update ProjectReference paths and sln entries.
- [ ] **Step 2:** Replace identifiers: `FatGuysSpeak` → `Squelch`, `fatguysspeak` → `squelch`, `FatGuys`/`fatguys` → `Squelch`/`squelch` where they name the product, and user-visible "FatGuysSpeak"/"Fat Guys Speak" strings (UI text, window titles, notifications, dashboard, installer copy, PorkChop prompts). Do NOT touch: channel name `angry-fat-guys`, past CHANGELOG sections, `docs/superpowers/` history, `fatguysspeak-rg`/`fatguysspeak-plan`.
- [ ] **Step 3:** Clean build all targets:
  `dotnet build Squelch.Server --framework net9.0`, `--framework net9.0-windows10.0.19041.0`, `dotnet build Squelch.Client --framework net9.0-windows10.0.19041.0`, `dotnet build Squelch.Installer --framework net9.0-windows10.0.19041.0`, `dotnet build Squelch.ClientInstaller`. Expected: 0 errors.
- [ ] **Step 4:** `dotnet test Squelch.Tests` — expected: same pass count as before the rename (record the baseline first with the old name).
- [ ] **Step 5:** `git grep -il "fatguys"` — expected: only the exclusions listed in Step 2 (and the legacy constants added in later tasks don't exist yet).
- [ ] **Step 6:** Commit `refactor: rename FatGuysSpeak to Squelch`.

### Task 2: Tray icon model (Shared, pure)

**Files:**
- Create: `Squelch.Shared/TrayIconModel.cs`
- Test: `Squelch.Tests/Client/TrayIconModelTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public enum TrayVisual { Idle, Muted, Level }
  public readonly record struct TrayIconChoice(TrayVisual Visual, int Level); // Level 1..7 only when Visual == Level, else 0
  public sealed class TrayIconModel
  {
      public const int MinFrameIntervalMs = 80;   // ≈12 fps cap for level-frame swaps
      public TrayIconModel(double smoothing = 0.5);
      // Returns the new icon when it changes, null when unchanged or throttled.
      public TrayIconChoice? Update(bool inVoice, bool muted, bool transmitting,
                                    double micLevel, double gateThreshold, long nowMs);
  }
  ```

Rules: not in voice → Idle; muted → Muted; not transmitting → Idle; smoothed level (EMA: `s += smoothing * (level - s)`, clamped 0..1) below `gateThreshold` → Idle; otherwise Level `max(1, round(s * 7))`. Changes of `Visual` (Idle/Muted/Level) are returned immediately; a change of only the `Level` number is returned only if ≥ `MinFrameIntervalMs` since the last returned choice.

- [ ] **Step 1: Write failing tests**
  - `NotInVoice_IsIdle` — `Update(false, false, true, 1.0, 0.05, 0)` → `(Idle, 0)`.
  - `Muted_IsMuted_EvenWhileTransmitting` — `(true, true, true, 1.0, 0.05, 0)` → `(Muted, 0)`.
  - `NotTransmitting_IsIdle` — `(true, false, false, 1.0, 0.05, 0)` → `(Idle, 0)`.
  - `FullLevel_Converges_To_Level7` — feed level 1.0 for 20 calls 100 ms apart → last non-null is `(Level, 7)`.
  - `BelowGate_IsIdle` — feed 0.02 with gate 0.05 → `(Idle, 0)`.
  - `SameChoice_ReturnsNull` — two identical calls 100 ms apart → second returns null.
  - `LevelChanges_ThrottledTo80ms` (Review Focus 5) — alternate levels 0.3/0.9 every 10 ms for 1000 ms → count of non-null results ≤ 13.
  - `MuteToggle_NotThrottled` — level frame at t=0, mute at t=10 → `(Muted, 0)` returned at t=10.
- [ ] **Step 2:** `dotnet test Squelch.Tests --filter "FullyQualifiedName~TrayIconModelTests"` → FAIL (type missing).
- [ ] **Step 3:** Implement `TrayIconModel` in `Squelch.Shared/TrayIconModel.cs`.
- [ ] **Step 4:** Same command → PASS.
- [ ] **Step 5:** Commit `feat(shared): tray icon state model`.

### Task 3: Old-settings import logic (Shared, pure)

**Files:**
- Create: `Squelch.Shared/LegacySettingsImport.cs`
- Test: `Squelch.Tests/Client/LegacySettingsImportTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static class LegacySettingsImport
  {
      public const string OldServerUrl = "https://fatguysspeak-server.azurewebsites.net";
      public const string NewServerUrl = "https://squelch-server.azurewebsites.net";
      public const string OldAppId = "com.companyname.fatguysspeak.client";
      public static bool ShouldImport(bool newPrefsFileExists, bool oldPrefsFileExists);
      // Input/output: MAUI unpackaged preferences.dat JSON, shape {"": {"key": "value", ...}}.
      public static string RewritePreferencesJson(string json);
  }
  ```

Rules: `ShouldImport` is true only when the new file is absent and the old one exists. `RewritePreferencesJson` keeps every key/value untouched except: `server_url` equal to `OldServerUrl` (compare after `TrimEnd('/')`, ordinal ignore-case) becomes `NewServerUrl`; `saved_servers` (`|`-separated) has each such entry rewritten, then de-duplicated case-insensitively keeping first occurrence order. Use `System.Text.Json`.

- [ ] **Step 1: Write failing tests**
  - `ShouldImport_OnlyWhenNewMissingAndOldPresent` — `(false,true)`→true; `(true,true)`→false (Review Focus 4); `(false,false)`→false.
  - `RewritesServerUrl` — `"server_url":"https://fatguysspeak-server.azurewebsites.net"` → new URL.
  - `RewritesServerUrl_TrailingSlashAndCase` (Review Focus 1) — `"https://FatGuysSpeak-server.azurewebsites.net/"` → new URL.
  - `KeepsLocalhostServerUrl` — `"http://localhost:5238"` unchanged.
  - `SavedServers_RewrittenAndDeduped` (Review Focus 2) — input `"https://fatguysspeak-server.azurewebsites.net|http://localhost:5238|http://localhost:5239|http://localhost:5238|https://squelch-server.azurewebsites.net"` → `"https://squelch-server.azurewebsites.net|http://localhost:5238|http://localhost:5239"`.
  - `OtherKeysPreserved` — `saved_username`, `remember_me`, `PttVirtualKey_1`, `audio_input_device`, `ui_theme` values identical after rewrite.
- [ ] **Step 2:** Run filter `LegacySettingsImportTests` → FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(shared): legacy FatGuysSpeak settings rewrite`.

### Task 4: Server startup rewrite of stored URLs + server name

**Files:**
- Create: `Squelch.Server/Services/LegacyHostRewrite.cs`
- Modify: `Squelch.Server/Program.cs` — call it right after the `VoiceTranscriptPurge.RunAsync(ctx)` call (~line 1051), log the count; change the seed name at ~line 997 to `"Squelch"`
- Test: `Squelch.Tests/Server/LegacyHostRewriteTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static class LegacyHostRewrite
  {
      public const string OldHost = "fatguysspeak-server.azurewebsites.net";
      public const string NewHost = "squelch-server.azurewebsites.net";
      public static string? Rewrite(string? url);          // swaps host for http:// and https:// prefixes, scheme preserved, else unchanged
      public static Task<int> RunAsync(AppDbContext db);    // returns rows changed
  }
  ```
  `RunAsync` rewrites `User.AvatarUrl`, `Message.AttachmentUrl`, `DirectMessage.AttachmentUrl` (not `Webhook.Url`, not message text), and renames the `GuildServer` with `OwnerId == 0 && Name == "FatGuysSpeak"` to `"Squelch"`. Idempotent; same style as `VoiceTranscriptPurge` (`ExecuteUpdateAsync` where it fits).

- [ ] **Step 1: Write failing tests** (TestDb, `TestHelpers.SeedServerAsync`)
  - `Rewrite_Https` — `https://fatguysspeak-server.azurewebsites.net/uploads/a.png` → `https://squelch-server.azurewebsites.net/uploads/a.png`.
  - `Rewrite_Http_SchemePreserved` (Review Focus 3) — `http://fatguysspeak-server…/uploads/a.png` → `http://squelch-server…/uploads/a.png`.
  - `Rewrite_OtherHostAndNull_Unchanged` — `http://localhost:5238/uploads/a.png` and `null` unchanged.
  - `RunAsync_RewritesAvatarAndAttachments_NotText` — seed a user avatar, a message with old-host `AttachmentUrl` and the old URL in `Content`, a DM attachment → after run the three URL fields use the new host, `Content` unchanged, return value 3.
  - `RunAsync_RenamesSeededServer` — server named `FatGuysSpeak` with `OwnerId 0` → `Squelch`; a server with `OwnerId 0` named `Other` untouched.
  - `RunAsync_IsIdempotent` — second run returns 0.
- [ ] **Step 2:** Run filter `LegacyHostRewriteTests` → FAIL.
- [ ] **Step 3:** Implement and wire into `Program.cs`.
- [ ] **Step 4:** Run → PASS; then full `dotnet test Squelch.Tests` → all pass.
- [ ] **Step 5:** Commit `feat(server): rewrite legacy host in stored URLs, rename seeded server`.

### Task 5: New icon and website

**Files:**
- Create: `Squelch.Client/Resources/AppIcon/appicon.svg` (from `svg/squelch-icon.svg`; drop `appiconfg.svg` and the `ForegroundFile` attribute; `Color="#0B0B0D"`)
- Create: `Squelch.Client/Resources/Images/squelch_logo.png` (from `png/app/squelch-256.png`)
- Rename: `Resources/Images/fgs_logo.svg` → `porkchop_avatar.svg` (pig, used by PorkChop only)
- Create: `Squelch.Client/Platforms/Windows/squelch.ico` (window/taskbar icon if the app sets one explicitly), `Squelch.Client/Resources/Tray/` with `tray-idle.ico`, `tray-muted.ico`, `tray-level-0.ico`…`tray-level-7.ico` (copied as content, `CopyToOutputDirectory`)
- Modify: `Pages/LoginPage.xaml:12`, `Pages/RegisterPage.xaml:11`, `Pages/MainPage.xaml:45` → `squelch_logo.png`; `MainPage.xaml:953` and `:1504` (bot avatar) → `porkchop_avatar.png`
- Modify: `Squelch.Installer` and `Squelch.ClientInstaller` — `<ApplicationIcon>` + window `Icon` → `squelch.ico`
- Modify: `website/index.html` (name, copy, `<title>`, favicon links, download link to the new repo's Squelch setup asset), add `website/favicon.ico`, `favicon.svg`, `apple-touch-icon.png`, `icon-192.png`, `icon-512.png` (and the same in `docs/`), `docs/CNAME` → `getsquelch.com`
- Modify: `Squelch.Server` web dashboard favicon/title if it has one

- [ ] **Step 1:** Copy assets and update references as listed.
- [ ] **Step 2:** Clean-build the client (XAML change: delete `bin`/`obj` first per the no-stale-builds rule) → 0 errors; `dotnet test Squelch.Tests --filter "FullyQualifiedName~XamlCommandBindingTests"` → PASS.
- [ ] **Step 3:** Launch server + two clients hidden; screenshot login page, main top bar, a PorkChop message, and the taskbar. Expected: knob everywhere, pig only on PorkChop.
- [ ] **Step 4:** Open `website/index.html` in Playwright; expected: Squelch name, knob favicon, no "FatGuysSpeak" text.
- [ ] **Step 5:** Commit `feat: Squelch knob icon and website rebrand`.

### Task 6: Tray icon in the client

**Files:**
- Create: `Squelch.Client/Platforms/Windows/TrayIconService.cs` (Windows-only, `#if WINDOWS` like `UpdateService`)
- Modify: `Squelch.Client/MauiProgram.cs` (register singleton), `Squelch.Client/ViewModels/MainViewModel.cs` (start it once the VM exists), `Squelch.Client/App.xaml.cs` (dispose in `window.Destroying`)

**Interfaces:**
- Consumes: `TrayIconModel` (Task 2); `AudioService.MicLevelChanged`, `AudioService.NoiseGateThreshold`; `MainViewModel.InVoice`, `IsMuted`, `IsPttActive` (transmitting = PTT held; open mic is not allowed), `ToggleMuteCommand`.
- Produces: `TrayIconService : IDisposable` with `void Start(MainViewModel vm)`.

Use Win32 `Shell_NotifyIcon` with a hidden message-only window, or WinForms `NotifyIcon` if it coexists with the WinUI loop — implementer's choice; preload all 10 icons once. Tooltip `Squelch — Idle|Talking|Muted`. Double-click → restore and activate the main window. Right-click menu: `Open Squelch`, `Mute` / `Unmute` (label follows state; runs `ToggleMuteCommand` on the main thread), `Quit` (same path as closing the window). Feed `Update(...)` on every mic-level event and on `InVoice`/`IsMuted`/`IsPttActive` changes. Remove the icon in `Dispose` and from an `AppDomain.ProcessExit` handler.

- [ ] **Step 1:** Implement and wire.
- [ ] **Step 2:** Clean-build client → 0 errors; full `dotnet test` → PASS.
- [ ] **Step 3:** Manual check with two clients in voice (screenshots of the tray area): idle when not in voice; level frames rise/fall while holding PTT and talking; slashed knob after Mute from the tray menu, and the in-app mute button shows muted too; double-click brings the window forward; Quit and window-close both exit with no tray icon left behind.
- [ ] **Step 4:** Commit `feat(client): voice-state tray icon`.

### Task 7: Import old settings on first Squelch launch

**Files:**
- Create: `Squelch.Client/Platforms/Windows/LegacySettingsMigration.cs`
- Modify: `Squelch.Client/MauiProgram.cs` — call `LegacySettingsMigration.RunOnce()` as the first statement of `CreateMauiApp`, before anything reads `Preferences`/`SecureStorage`

**Interfaces:**
- Consumes: `LegacySettingsImport` (Task 3).
- Produces: `public static class LegacySettingsMigration { public static void RunOnce(); }` (never throws; logs and returns on any error).

Paths: new settings dir = `Path.Combine(Path.GetDirectoryName(FileSystem.AppDataDirectory)!, "Settings")`; old = sibling folder `LegacySettingsImport.OldAppId` + `\Settings` under the same parent (today: `%LOCALAPPDATA%\User Name\com.companyname.fatguysspeak.client\Settings`). Verify both paths on this machine before relying on them. If `ShouldImport`: create the new dir, write `RewritePreferencesJson(old preferences.dat)` to `preferences.dat`, copy `securestorage.dat` byte-for-byte.

- [ ] **Step 1:** Implement and wire.
- [ ] **Step 2:** Clean-build client → 0 errors.
- [ ] **Step 3:** With the owner's real old Settings folder present and no Squelch Settings folder: launch a debug Squelch client. Expected: login screen pre-filled with `BillyBong` and the saved password, server field = new Azure URL, theme Ember, PTT key and audio devices as before. Relaunch: no re-import (file timestamps unchanged). If the password doesn't come through, implement the spec fallback (username only) and note it.
- [ ] **Step 4:** Commit `feat(client): import FatGuysSpeak settings on first launch`.

### Task 8: Bridge release code (FatGuysSpeak 0.13.2)

**Files (branch `bridge/0.13.2` from tag `v0.13.1`, old names throughout):**
- Create: `FatGuysSpeak.Client/Platforms/Windows/SquelchHandover.cs`
- Create: `FatGuysSpeak.Client/Pages/HandoverPage.xaml(.cs)` (or an overlay on LoginPage — implementer's choice)
- Modify: `FatGuysSpeak.Client/App.xaml.cs` — when Velopack-installed, show the handover instead of `AppShell`
- Modify: `Directory.Build.props` `<Version>0.13.2</Version>`, `CHANGELOG.md` entry "0.13.2 — moves you to Squelch"

**Interfaces:**
- Produces: `public static class SquelchHandover { public const string SetupUrl = "https://github.com/mgagnon57/Squelch/releases/download/v0.14.0/Squelch.Client-win-Setup.exe"; public static Task<HandoverResult> RunAsync(IProgress<string> status, CancellationToken ct); }`, `public record HandoverResult(bool Success, string? Error);`

Flow: screen reads "FatGuysSpeak is now Squelch — moving you over…" with status lines → download `SetupUrl` to `%TEMP%` (explicit tag, NOT `/latest`: the 0.13.2 bridge release is published after 0.14.0 and would become "latest") → run it with `--silent` and wait → start `%LOCALAPPDATA%\Squelch.Client\Squelch.Client.exe` → start `%LOCALAPPDATA%\FatGuysSpeak.Client\Update.exe --uninstall` detached → exit. On any failure: show the error and a clickable `SetupUrl` link plus a "Keep using FatGuysSpeak for now" button that continues to the normal `AppShell` (old server still works). Not Velopack-installed (dev run) → skip the handover entirely.

- [ ] **Step 1:** Implement on the branch.
- [ ] **Step 2:** `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0` and full `dotnet test` on the branch → PASS.
- [ ] **Step 3:** Commit on the branch `feat: 0.13.2 bridge hands over to Squelch` (do not merge into main).

### Task 9: Local rehearsal of the whole handover

No committed code. Uses a scratch dir and the proven local-feed pattern (`project-version-sync` memory): a temporary, uncommitted `FATGUYS_UPDATE_FEED` override in the old `UpdateService` (Velopack `SimpleFileSource`), and a temporary `SQUELCH_SETUP_URL` override in `SquelchHandover`, both set as process-scope env vars in the same call that launches the client.

- [ ] **Step 1:** Back up the owner's real `%LOCALAPPDATA%\FatGuysSpeak.Client` and `%LOCALAPPDATA%\User Name\com.companyname.fatguysspeak.client` folders to the scratchpad.
- [ ] **Step 2:** Build local Velopack feeds: old packId `FatGuysSpeak.Client` 0.13.1 (rehearsal build with the feed override) and 0.13.2 (bridge with both overrides), each with `win` + per-version channels; Squelch 0.14.0 setup from main.
- [ ] **Step 3:** Run a local old server (bridge branch, version 0.13.2) on :5239 and a local Squelch server (main) on :5238.
- [ ] **Step 4:** Install the rehearsal 0.13.1, log in against :5239. Expected: update overlay → relaunch as 0.13.2 → handover screen → Squelch installs, opens pre-filled, connects; `%LOCALAPPDATA%\FatGuysSpeak.Client` gone; Start-menu shortcut gone. If `Update.exe --uninstall` hangs, fix the bridge (kill-and-delete fallback) and re-run.
- [ ] **Step 5:** Force a failure (bad setup URL) once. Expected: error + link + "keep using" works.
- [ ] **Step 6:** Uninstall rehearsal installs; restore the owner's backed-up folders; confirm the real 0.13.1 install launches as before.

### Task 10: Production cutover

Announce each outward step to the owner as it starts. Stop and report on any unexpected result.

- [ ] **Step 1: Repo.** `gh repo rename Squelch -R mgagnon57/FatGuysSpeak`; `git remote set-url origin https://github.com/mgagnon57/Squelch.git`; push main and the bridge branch. Check `https://github.com/mgagnon57/FatGuysSpeak` redirects.
- [ ] **Step 2: Azure app.** `az webapp create -g fatguysspeak-rg -p fatguysspeak-plan -n squelch-server --runtime "DOTNETCORE:9.0"`; copy every app setting from `fatguysspeak-server` (`az webapp config appsettings list` → `set`); enable WebSockets, Always On, HTTPS-only, SCM basic auth; save its publish profile as secret `AZURE_WEBAPP_PUBLISH_PROFILE_SQUELCH` and use it in `azure-deploy.yml`.
- [ ] **Step 3: Release 0.14.0.** Add CHANGELOG `[Unreleased]` entry (rename, new icon, tray icon, settings import), `$env:GITHUB_TOKEN` set, `./release.ps1 0.14.0`, check `gh release view v0.14.0` has both channels and `Squelch.Client-win-Setup.exe`, push main + tag. Run `gh workflow run azure-deploy.yml`; poll `https://squelch-server.azurewebsites.net/api/version` until `0.14.0`.
- [ ] **Step 4: Data copy** (right before Step 5, to keep the gap near zero). First confirm the real DB and uploads paths over SSH/Kudu (content root is expected to be `/home/site/wwwroot`). Stop `fatguysspeak-server`; via Kudu/SCM copy `/home/site/wwwroot/fatguys.db` (+ `-wal`/`-shm` if present) to the new app as `/home/site/wwwroot/squelch.db`, and the whole `/home/site/wwwroot/uploads` folder; restart both. Record users/messages/uploads counts on both; expected equal. The new server's startup `LegacyHostRewrite` log line shows the rewritten count; spot-check an old image loads from the new host.
- [ ] **Step 5: Bridge 0.13.2.** On `bridge/0.13.2`, run that branch's `release.ps1 0.13.2` (old packId, uploads to the renamed repo); confirm `v0-13-2` channel assets. Deploy the bridge server to `fatguysspeak-server` with `gh workflow run azure-deploy.yml --ref bridge/0.13.2` (that branch's workflow still targets the old app and secret). Poll the old `/api/version` until `0.13.2`.
- [ ] **Step 6: Real handover.** Launch the owner's installed FatGuysSpeak; it updates to 0.13.2 and hands over. Expected as in Task 9 Step 4, against the live servers, with old messages and images visible in Squelch. Then launch a second Squelch client and run the Task 6 tray check live.
- [ ] **Step 7: Website.** Tell the owner the GitHub Pages DNS records for `getsquelch.com` (apex A records `185.199.108.153`, `.109.153`, `.110.153`, `.111.153`; `www` CNAME `mgagnon57.github.io`), set the Pages custom domain, enable HTTPS once the certificate is issued.
- [ ] **Step 8: Wrap-up.** Stop (don't delete) `fatguysspeak-server` after the owner confirms. Update CLAUDE.md and memory notes to the new names/URLs. `git grep -il fatguys` → only the documented leftovers.
