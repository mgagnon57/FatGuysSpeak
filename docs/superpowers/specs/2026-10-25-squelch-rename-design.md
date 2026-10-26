# Squelch: rename + new icon + tray icon — design

Date: 2026-10-25. Status: draft for approval.

## Intent

FatGuysSpeak becomes **Squelch**. The owner wants the new name to be the real name, all the way
down, not a skin over the old one. The new "VU ring" knob icon (black `#0B0B0D`, red `#E5202E`,
unlit `#3A3A40`) replaces the PorkChop pig as the app's identity. The same icon pack includes
system-tray art, and the owner asked for a tray icon in this round as well.

There is exactly one install to move: the owner's own FatGuysSpeak client on this machine (the
owner confirmed there are no other clients and no installed server copies, so the server
installer needs no upgrade path — it simply installs as Squelch from now on). It must arrive on Squelch without doing
anything: no new download link, no re-login, no lost settings.

## Goals

1. The name Squelch everywhere: UI text, window titles, logo text, installers, website, notifications,
   web dashboard, PorkChop's lines, plus the code (projects, namespaces, solution, assemblies), the
   GitHub repo, the Velopack package ID, the MAUI ApplicationId, the Azure web app and the database file.
2. The knob icon as the app icon: taskbar, window, top bar, login/register screen, both installers,
   website favicon and touch icons.
3. A Windows tray icon showing voice state, with a small menu.
4. A seamless one-time handover from the installed FatGuysSpeak client to Squelch.
5. All existing accounts, messages, uploads, polls, aliases, recaps and digests carried over to the new server.

## Non-goals

- Tray icon on Android/iOS/macCatalyst (Windows only, like every non-chat feature).
- Minimise-to-tray or close-to-tray. **Owner's decision: closing the main window still quits Squelch
  completely**, as today. The tray icon is a status light and a shortcut menu, not a background mode.
- Renaming the community's channel names (`lobby`, `angry-fat-guys`) or users' names.
- Renaming Azure's resource group (`fatguysspeak-rg`) or App Service plan (`fatguysspeak-plan`); Azure
  can't rename them and no one sees them.
- Renaming the local folder `C:\FatGuysSpeak` (the owner can do that by hand afterwards).
- Code signing, a new domain purchase, or any other new feature.

## Anti-goals (would be a failure even if it "works")

- The tester has to download something, log in again, or re-pick audio devices.
- Stray "FatGuysSpeak" text left in anything a person can see after the rename (old CHANGELOG
  entries and historical docs are fine — they describe the past).
- Any data lost in the server move, or the old server shut down before the tester has handed over.
- Voice being captured, transcribed or stored in any new code (standing legal rule — see CLAUDE.md).
  The tray's mic level comes from the existing in-memory `AudioService.MicLevelChanged` peak only.

## Decisions (owner's, as given)

- Rename goes "all the way down" (UI, code, repo, package ID, server address).
- Handover: a final FatGuysSpeak update installs Squelch, carries over login/settings, removes the old app.
- Tray icons are included.
- Website domain: `getsquelch.com` (already registered by the owner).
- The only install to move is the owner's own client on this machine.
- Closing the window quits.

## Calls made by Claude (owner to check while reading)

- **Version:** Squelch starts at **0.14.0**; the bridge FatGuysSpeak release is **0.13.2**. 1.0 stays
  reserved for first production.
- **PorkChop keeps the pig** as his own chat avatar and bot images — he's still PorkChop. Only the
  *app's* icon changes.
- **The seeded server row named "FatGuysSpeak" is renamed to "Squelch"** (idempotent startup
  update in both SQLite and PostgreSQL branches, matched on `OwnerId = 0` and the old name).
- **New Azure web app `squelch-server`** (https://squelch-server.azurewebsites.net) in the existing
  `fatguysspeak-plan` (no extra cost), same region/settings as today (Linux, WebSockets, Always On,
  HTTPS-only, all app settings copied).
- **Tray behaviour:** idle = grey ring; while transmitting, the 8 level frames follow the smoothed
  mic level (frame = round(peak × 7), swap at most ~12 fps, only when the frame changes, idle frame
  below the noise-gate threshold); muted = slashed knob. Tooltip "Squelch — <state>". Double-click
  brings the main window to the front; right-click menu: **Open Squelch**, **Mute / Unmute**, **Quit**.
  Not connected / not in voice shows idle.
- **Stored server URLs are rewritten during the data copy.** Attachment and avatar URLs are saved with
  the full host (`AttachmentsController.cs:59`, `UsersController.cs:67`), so on the copied DB every
  `https://fatguysspeak-server.azurewebsites.net` prefix in server-generated URL fields (message
  attachment fields, `User.AvatarUrl`, any similar) becomes `https://squelch-server.azurewebsites.net`.
  Free text inside messages is not touched. Verification compares row counts before/after and checks
  that no server-generated URL field still holds the old host.
- **History stays history.** Past messages, PorkChop replies, saved recaps, weekly digests and
  aliases that mention "FatGuysSpeak" are left as written; only new content uses Squelch.
- **Rehearse the handover locally before production.** Build a rehearsal 0.13.1 client whose update
  source can be pointed at a local Velopack feed via a temporary, uncommitted override (the proven
  `FATGUYS_UPDATE_FEED` harness pattern), run a local "old" server reporting 0.13.2, and walk the full
  bridge → Squelch handover end to end on this machine. Only then publish 0.13.2 for real.
- **The FatGuysSpeak 0.13.2 bridge release stays in the renamed repo as history.** It must be there
  (the old client looks up the repo name, which GitHub redirects) and deleting it gains nothing.
- **ApplicationId** becomes `com.squelch.client`; MAUI `ApplicationTitle` becomes `Squelch`.
  Publisher folder name: leave whatever MAUI uses by default unless it shows somewhere visible.

## The rename — scope

Current footprint (git grep, case-insensitive "fatguysspeak|fat guys speak|fatguys"): ~290 files
across Client (75), Tests (81), Server (61), Installer (20), ClientInstaller (15), Shared (17),
docs (21), plus sln, scripts, Dockerfile, CI, CHANGELOG, CLAUDE.md and the website.

- Projects/folders/csproj/namespace: `FatGuysSpeak.X` → `Squelch.X` for Server, Client, Shared,
  Tests, Installer, ClientInstaller; `FatGuysSpeak.sln` → `Squelch.sln`.
- Exe/assembly names: `Squelch.Client.exe`, `Squelch-Server-Setup.exe`, `Squelch-Client-Setup.exe`.
- Velopack `--packId Squelch.Client`, `--mainExe Squelch.Client.exe`, release name "Squelch X.Y.Z".
- Default DB file `fatguys.db` → `squelch.db` (local dev: the builder renames the existing file so dev data
  survives; on Azure the copied DB lands under the new name — see Server move).
- `SetApplicationName("FatGuysSpeak")`, HTTP User-Agent, JWT issuer/audience and any other branded
  identifiers → Squelch. Tokens/cookies being invalidated by this is acceptable (tokens aren't persisted).
- `ApiService.DefaultServerUrl` and `UpdateService.RepoUrl` → new Azure URL and new repo URL.
- Scripts: `release.ps1`, `launch.ps1`, `build-*.ps1`, `test-e2e.ps1`, Dockerfile, `.dockerignore`,
  `.gitignore`, workflows (`azure-deploy.yml` app-name `squelch-server` + its publish-profile secret).
- Website (`website/index.html`, synced to `docs/index.html`): name, copy, favicon set, touch icons,
  download link. **Owner's decision: the site moves to `getsquelch.com`** — `docs/CNAME` becomes
  `getsquelch.com` and the GitHub Pages custom domain is updated to match (the owner sets the DNS
  records at their registrar; the builder tells them exactly which records). Any text or links that
  mention `fatguysspeak.com` point to `getsquelch.com`.
- CLAUDE.md and the memory notes are updated to the new names at the end.
- Leave alone: past CHANGELOG entries, past specs/plans under `docs/superpowers/`, channel names.

GitHub repo: rename `mgagnon57/FatGuysSpeak` → `mgagnon57/Squelch`. GitHub redirects the old URL
(git, web, API, release downloads), which the bridge release relies on as a safety net, but the
bridge must use the new URL directly rather than depend on the redirect.

## New icon

Source pack: `C:\ClaudeWork\Squelch\branding\icon` (README there describes it). Copy the needed files
into the repo (client Resources, installers, website); don't reference the external path at build time.

- App/exe/window/taskbar: `ico/squelch.ico`; MAUI AppIcon from `svg/squelch-icon.svg` (small geometry
  `squelch-icon-small.svg` for ≤32 px where MAUI allows a separate source).
- In-app top bar and login/register logo: replace the pig with the knob (PNG from `png/app/`).
- Both installers: `squelch.ico` as window + exe icon.
- Website: `web/favicon.ico`, `favicon.svg`, `apple-touch-icon.png`, `icon-192.png`, `icon-512.png`.
- Tray: `ico/tray-idle.ico`, `tray-muted.ico`, `ico/tray-levels/tray-level-0..7.ico` (preload once).
- PorkChop's avatar and any bot-specific art stay the pig.

## Tray icon

Windows-only service in the client (e.g. `TrayIconService`), using a Win32 `Shell_NotifyIcon` or
WinForms `NotifyIcon` — builder's choice; the client is MAUI/WinUI so pick whatever coexists cleanly
with the WinUI message loop. It listens to existing state only: `AudioService.MicLevelChanged`,
`AudioService.IsMuted` / `MainViewModel.IsMuted`, PTT/transmit state, and voice-channel membership.
Mute/Unmute from the menu calls the same command as the in-app mute button so the UI stays in sync.
Quit performs the same shutdown as closing the window. The icon is removed on exit (including crash
paths where possible) so no ghost icon is left behind.

Testable logic (level → frame mapping, smoothing, frame-change throttling, state → icon choice) goes
in Shared as a pure class with unit tests, per the project's "testable client logic lives in Shared" rule.

## Server move (Azure)

1. Create `squelch-server` in the existing plan; copy every app setting from `fatguysspeak-server`
   (`Jwt__Key`, `Dashboard__*`, `Anthropic__*`, `ElevenLabs__*`, `BootstrapAdminUsername`, etc.);
   enable WebSockets, Always On, HTTPS-only; get a publish profile into a new GitHub secret.
2. Deploy Squelch 0.14.0 to it via the (renamed) Actions workflow.
3. Copy data: stop the old app briefly, copy the SQLite DB from `/home` (as `squelch.db`) and the
   uploads folder to the new app's `/home`, then restart both. Verify user/message/upload counts match.
4. The old server keeps running (on the 0.13.2 bridge build) until the tester has handed over and
   the owner confirms; then it is stopped. Deleting it is a separate, later decision for the owner.

Messages posted to the old server between the copy and the handover would be lost; with one tester
the builder schedules the copy right before releasing the bridge to keep that window near zero.

## The handover

Ordering (each step verified before the next):

1. Ship Squelch 0.14.0: GitHub release under the new repo, Velopack package `Squelch.Client` with
   both channels (`win` + `v0-14-0`), new Azure server live with copied data.
2. Ship FatGuysSpeak 0.13.2 — the bridge — on the OLD package ID `FatGuysSpeak.Client` and deploy it
   to the OLD server, so the tester's client sees server 0.13.2 and auto-updates to it via the existing
   version-sync path (per-version channel `v0-13-2` must exist).
3. The 0.13.2 client, on launch, before or instead of the login screen: shows "FatGuysSpeak is now
   Squelch — moving you over…", downloads the Squelch Setup from the new repo's release, installs it
   silently, launches Squelch, and removes the old FatGuysSpeak install (Velopack uninstall, Start
   menu/desktop shortcuts included). If any step fails it shows the failure and a link to the Squelch
   installer, and the old app keeps working against the old server.
4. Settings carry-over is done by **Squelch on its first launch**: if its own settings are empty and
   the old FatGuysSpeak settings exist (MAUI Preferences + SecureStorage files under the old ApplicationId
   folder in LocalAppData), import them once: server URL list, remembered username/password,
   remember-me, audio devices/gain/volume, noise gate, theme, PorkChop voice, private mode hint,
   PTT consent, stream preferences. `server_url` and `saved_servers` entries equal to the old Azure URL
   are rewritten to the new one; anything else (e.g. localhost) is kept as is. This keeps the bridge
   tiny and also covers anyone who installs Squelch by hand. The mapping logic is pure and tested in Shared.
5. "Saved login" means the remembered username + password (the app doesn't persist tokens). The
   SecureStorage password must survive the move (DPAPI, same Windows user) — the builder verifies this
   on a real install, and if it can't be read, falls back to pre-filling the username only.

## Verification the builder must do

- All tests green; new tests for the tray mapping logic, settings import/URL rewrite, and the
  server-row rename migration.
- Clean builds of every project (Server both TFMs, Client, both installers).
- Local rehearsal first (see calls above), then the real end-to-end handover on this machine: install FatGuysSpeak 0.13.1, let it update to 0.13.2,
  watch it install and launch Squelch, confirm the old app is gone, settings/login carried over and
  Squelch connected to `squelch-server` with the old messages visible.
- Two Squelch clients in voice: tray shows idle/talking-level/muted correctly; menu items work;
  closing the window quits and leaves no tray icon.
- `git grep -i fatguys` afterwards shows only intended leftovers (history, bridge-only code paths,
  the old-settings import path, Azure resource group/plan names).

## Left to the builder

Order of the code rename vs. icon vs. tray work; how to perform the namespace rename mechanically;
the tray implementation library; exact wording of the handover screen; how the bridge locates and
runs the Squelch installer; where the bridge code lives (a short-lived branch off the 0.13.1 tag is
fine, since it must ship as FatGuysSpeak); release.ps1 adjustments for the new names.

## Open questions

None. The builder check's questions were answered by the owner (domain, installs to move) or
settled as calls above (URL rewrite, history, local rehearsal, bridge release kept).
