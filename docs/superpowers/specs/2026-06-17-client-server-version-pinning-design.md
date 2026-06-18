# Client Auto-Update Pinned to Server Version — Design

**Date:** 2026-06-17
**Status:** Approved for planning (Phase-0 spike completed)

## Goal

When the client connects, it reads the server's running version and auto-updates
**itself to that exact version** — upgrading or downgrading — via Velopack, showing a
notification ("Updating to v1.3.0…" / "Downgrading to v1.1.0…"). If it can't match
(no matching build published, or offline), it shows a persistent mismatch banner and
keeps working, retrying next launch.

## Decisions (from brainstorming)

- **Yardstick:** the server's running version from `GET /api/version` (not the latest
  GitHub release).
- **Apply behavior:** always auto-apply both directions, with a notification toast.
- **Mechanism:** Velopack (the .NET desktop auto-update framework).
- **Fallback:** when it can't match (no matching build / offline), notify with a
  persistent banner and keep the app usable; retry next launch. Never strand the user.
- **Prior feature:** remove the client-side "newer release available" banner (the client
  now syncs to its server); keep the dashboard "server update available" banner.

## Phase-0 spike result (completed — this is proven, not assumed)

Using `vpk` / `Velopack` **1.2.0** against a throwaway app, end-to-end:
- `vpk pack` packaged a published app into a full Velopack release (Setup.exe + portable
  zip + `.nupkg` + `RELEASES`).
- An installed app at **2.0.0**, pointed at a feed whose only version was **1.0.0** with
  `UpdateOptions { AllowVersionDowngrade = true }`, resolved `CheckForUpdatesAsync()` to
  `target=1.0.0, IsDowngrade=true`.
- `DownloadUpdatesAsync` + `ApplyUpdatesAndRestart(target)` **actually downgraded** the
  installed app: it relaunched reporting `current=1.0.0`.

So the load-bearing risk (pin to an exact, possibly-older version) is de-risked. The
confirmed public API: `UpdateManager(source, UpdateOptions{ AllowVersionDowngrade,
ExplicitChannel })`, `CheckForUpdatesAsync() -> UpdateInfo{ TargetFullRelease, IsDowngrade }`,
`DownloadUpdatesAsync`, `ApplyUpdatesAndRestart(VelopackAsset)`, `CurrentVersion`,
`IsInstalled`.

**Remaining implementation risk (lower):** hooking `VelopackApp.Build().Run()` into MAUI's
Windows entry point before MAUI boots. This is the documented Velopack+MAUI pattern; the
plan validates it early. The client is already `WindowsPackageType=None` (unpackaged) —
exactly what Velopack requires.

## Pinning mechanism (how "match the server's version" maps to Velopack)

The spike proved: *point the UpdateManager at a feed whose "latest" is the target version,
with `AllowVersionDowngrade`, and it resolves up or down to it.* On GitHub the per-version
feed is a **Velopack channel keyed to the version**:
- Channel name is the version, sanitized to Velopack-safe chars: `v` + version with dots →
  hyphens, e.g. `1.2.0` → channel **`v1-2-0`**. (A deterministic `UpdateChannel.ForVersion(version)`
  helper, used identically by the publish step and the client.)
- Each release is published to (a) the **default** channel (`win`) for the public
  "download latest" link, and (b) its **per-version** channel (`v1-2-0`) for server pinning.
- The client pins: `new UpdateManager(new GithubSource(repoUrl, null, prerelease:false),
  new UpdateOptions { ExplicitChannel = UpdateChannel.ForVersion(serverVersion), AllowVersionDowngrade = true })`.
  `CheckForUpdatesAsync()` then resolves to exactly the server's version (the only version
  in that channel), up or down.

(Implementation note: the exact `vpk upload github --channel …` layout — which release holds
each channel's `RELEASES-*.json` — is finalized in the plan; if GitHub channel hosting proves
awkward, the fallback is a static HTTP feed. The update *engine* is proven regardless of feed host.)

## Architecture / flow

1. Client startup (Windows entry, before MAUI): `VelopackApp.Build().Run()` — enables
   install/update/restart hooks. No-ops in dev/unpackaged-debug.
2. After successful login (`MainViewModel`, post `LoadServersAsync`): call `GET /api/version`
   → server `X.Y.Z`. Compare to the client's own version via `SemVer`.
3. Equal → done. Different → `UpdateService.SyncToServerVersionAsync(serverVersion)`:
   - Build the pinned `UpdateManager` (channel = `UpdateChannel.ForVersion(serverVersion)`, downgrade on).
   - `CheckForUpdatesAsync()`. If null (already matches / not installed in dev) → done.
   - Show a toast: `IsDowngrade` ? "Downgrading to v{X}…" : "Updating to v{X}…".
   - `DownloadUpdatesAsync(info)` then `ApplyUpdatesAndRestart(info.TargetFullRelease)` —
     exits, swaps files, relaunches at the server's version.
4. On any failure (no such channel/version in the feed, offline, not installed) → catch,
   show the persistent mismatch banner ("Your client v{A} doesn't match the server v{B};
   auto-update unavailable — continue or update manually"), keep the app usable. Retry on
   next launch/login.

## Components

- **Shared:** reuse `SemVer`; add `UpdateChannel.ForVersion(string version) -> string`
  (`"1.2.0"` → `"v1-2-0"`; tolerant). Pure, unit-tested.
- **Client `UpdateService`** (`#if WINDOWS`, no-op stub elsewhere): owns the Velopack
  `UpdateManager` construction, `SyncToServerVersionAsync(serverVersion)`, and exposes a
  result enum (`UpToDate`, `Applying`, `Unavailable`). Windows-only (Velopack is Windows
  here); the stub returns `UpToDate`.
- **Client Windows entry:** `VelopackApp.Build().Run()` hook (Platforms/Windows).
- **Client `MainViewModel`:** call the sync after login; observable state for the
  "applying" toast and the persistent mismatch banner; **remove** the old
  `UpdateAvailable` "newer release" banner + its `CheckForUpdatesAsync` (latest-release
  notify) — superseded.
- **Client `ApiService`:** reuse `GetVersionAsync()` (the existing `/api/version` shape) —
  add a tolerant wrapper if not present.
- **`MetricsController` dashboard banner:** unchanged (keep "server update available").
- **`release.ps1` + packaging:** `dotnet publish` the client; `vpk pack` to the default
  channel and the per-version channel; `vpk upload github` to the `vX.Y.Z` release. The
  landing-page "Download Client" points at the Velopack `Setup.exe` (default channel
  `releases/latest/download/...`). Replaces the ad-hoc `Client-Setup.zip`.

## Error handling

- Velopack/feed/network failures are caught in `UpdateService`; the app never blocks on
  them. `IsInstalled == false` (dev/unpackaged debug) → skip silently (no update in dev).
- A version with no published per-version channel → `CheckForUpdatesAsync` finds nothing →
  treated as "unavailable" → mismatch banner.
- The toast→restart is the expected disruption when a mismatch is found; on the matched
  relaunch nothing fires.

## Testing

Pure unit tests (no network/GUI):
- `UpdateChannel.ForVersion`: `"1.2.0"`→`"v1-2-0"`, `"v1.2.0"`→`"v1-2-0"`, tolerant of junk.
- `SemVer` equality/compare driving the upgrade-vs-downgrade *message* decision (a pure
  helper `UpdateDirection(client, server)` → `None`/`Upgrade`/`Downgrade`, tested).
Runtime/manual (Velopack + real releases — cannot be unit-tested):
- The Phase-0 spike already proved the Velopack engine (pack, resolve-downgrade, apply).
- Full feature verification requires publishing Velopack releases for two versions to
  GitHub and connecting a client to servers on each, confirming auto up/downgrade + the
  fallback banner. This is a runtime acceptance step, documented in the plan.

## Migration / caveats

- The repo already has **non-Velopack** GitHub releases (`v1.0.0`, `v1.1.0`). Velopack's
  GitHub feed only recognizes releases carrying its assets; future releases become
  Velopack releases. Existing non-Velopack releases are ignored by the feed (a client on
  the current build connecting to a server whose version has no Velopack release simply
  hits the "unavailable" fallback). The first Velopack release establishes the feed.
- Because Velopack changes the client distribution, the first adoption requires users to
  install the new Velopack-packaged client once (from the landing page) to get onto the
  update track. This is a one-time migration, called out in the release notes.

## Out of scope (v1)

- Non-Windows client auto-update (Velopack is Windows here; stub no-ops).
- Delta updates tuning, staged rollouts, signing (use Velopack defaults).
- Server-side enforcement / blocking incompatible clients (notify + auto-sync only).
- Self-hosting the update feed on the server (GitHub releases is the feed).
