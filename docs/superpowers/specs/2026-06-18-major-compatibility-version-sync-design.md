# Major-Compatibility Version Sync — Design

**Date:** 2026-06-18
**Status:** Approved for planning
**Supersedes the runtime behavior of:** `2026-06-17-client-server-version-pinning-design.md`
(the Velopack pinning engine and `UpdateChannel` from that feature are reused unchanged;
this redesigns *when* a sync fires and *how* it is presented).

## Goal

The server owns the version; the client keeps itself **compatible** with whichever backend
it is connected to. On every connect (and whenever the client is pointed at a different
backend), the client compares **major versions** with the server:

- **Same major** → the wire protocol is compatible → connect as-is. No download, no restart,
  even across minor/patch differences (e.g. client 1.2 ↔ server 1.7).
- **Different major** → breaking gap → the client **syncs itself to the server's exact
  version** (up or down) via Velopack, showing in-app progress and a graceful restart, then
  reconnects on the matched version.

A backend here means a distinct server URL the client points at, one at a time; each backend
has its own version. Whichever backend you land on, you and it end up in lockstep — but a
restart is only ever paid when the gap is actually breaking.

## Why this shape (decisions from brainstorming)

- **Compatibility = same SemVer major.** This is the SemVer convention (major = breaking
  change) and needs **no server-side code or config** — the client decides off the version the
  server already reports at `GET /api/version`.
- **Connection is never blocked by the server.** The server already accepts any client that
  speaks its API; the sync is a client-side convenience that makes the client match the backend
  it chose to connect to.
- **Re-evaluate per connect, not once per session.** The current once-per-session guard is
  replaced by a check keyed to the server's version, so switching backends re-syncs.
- **Sync only across majors.** Within a major, nothing interrupts the user — this is the fix
  for the "clunky, restarted for no reason" feedback.
- **In-app progress + graceful restart** replaces the abrupt silent-download/close-relaunch.

## Compatibility & direction rules

Let `mine` = the client's Velopack-installed version, `srv` = the server's `/api/version`.

| `mine` | `srv` | majors | Action |
|--------|-------|--------|--------|
| 1.0.0  | 1.7.3 | 1 = 1  | **Connect as-is** (compatible, no update) |
| 1.5.0  | 1.2.0 | 1 = 1  | **Connect as-is** (no needless downgrade) |
| 1.0.0  | 3.0.0 | 1 ≠ 3  | **Upgrade** to 3.0.0, restart, reconnect |
| 3.0.0  | 1.0.0 | 3 ≠ 1  | **Downgrade** to 1.0.0, restart, reconnect |

Direction (for the message wording) is the existing `SemVer.Compare(mine, srv)`:
`< 0` → "Updating", `> 0` → "Downgrading". The compatibility gate is purely the major
comparison; once a sync is decided, it always targets the server's **exact** version via the
per-version Velopack channel (`UpdateChannel.ForVersion(srv)`).

## Architecture / flow

On each successful connect to a backend (post-login, and again after a backend switch):

1. `serverVersion = ApiService.GetServerVersionAsync()` (existing `GET /api/version`). Null →
   stop (can't evaluate; connect as-is).
2. `mine = UpdateService.InstalledVersion` (Velopack `CurrentVersion`). Null → dev/unpackaged →
   stop (can't self-update in dev).
3. `VersionCompat.SameMajor(mine, serverVersion)` true → **done, connect as-is.** No UI.
4. False (cross-major):
   - Guard: if a sync was already attempted this session for *this* `serverVersion`, stop
     (prevents re-showing a dismissed banner on a server-list refresh; a successful sync exits
     the process so it never loops).
   - Show the **blocking sync overlay**: title "Updating to v{srv}…" / "Downgrading to
     v{srv}…", a progress bar, stage text.
   - `outcome = await UpdateService.PrepareAsync(serverVersion, progress)`:
     - **Prepared** (downloaded, ready): set stage "Installing…", then a short countdown
       ("Restarting to apply v{srv} in 3… 2… 1…"), then `UpdateService.ApplyAndRestart()` —
       process exits, files swap, relaunches at `srv`, reconnects, now same-major → no UI.
     - **Unavailable** (no matching build published for `srv`, or offline, or error): hide the
       overlay, show the **persistent mismatch banner** ("This server needs client v{srv}.
       Couldn't update automatically — it'll retry next time you connect."). App stays usable.
     - **Compatible** (defensive; e.g. not actually installed): hide overlay, no-op.

## Components

### Shared (pure, unit-tested)
- **`VersionCompat`** (new): `SameMajor(string? a, string? b) -> bool` — true iff both parse to
  a valid major and the majors are equal; tolerant of `v` prefixes and junk (junk/unparseable →
  `false`, i.e. "treat as incompatible / let the existing flow decide"). Built on a new
  `SemVer.Major(string?) -> int?` helper (returns null when not parseable).
- **`SemVer`** (extend): add `Major(string?) -> int?`. Reuse existing `Compare` for direction.
- **`UpdateChannel.ForVersion`** — reused unchanged (exact-version pinning channel).

### Client
- **`UpdateService`** (`#if WINDOWS`; stub elsewhere) — split the current
  `SyncToServerVersionAsync` into:
  - `Task<UpdateSyncOutcome> PrepareAsync(string serverVersion, IProgress<int>? downloadProgress)`
    — builds the pinned `UpdateManager` (channel = `UpdateChannel.ForVersion(serverVersion)`,
    `AllowVersionDowngrade = true`); `!IsInstalled` → `Compatible`; `CheckForUpdatesAsync()` null
    → `Unavailable` (no build for that version); else `DownloadUpdatesAsync(info, p =>
    downloadProgress?.Report(p))`, stash the info+manager, return `Prepared`; any exception →
    `Unavailable`. **Does not restart.**
  - `void ApplyAndRestart()` — `ApplyUpdatesAndRestart(stashed.TargetFullRelease)`. No-op if
    nothing stashed.
  - `InstalledVersion` — unchanged (cached Velopack `CurrentVersion`).
  - New enum `UpdateSyncOutcome { Compatible, Prepared, Unavailable }`. The stub returns
    `Compatible` from `PrepareAsync` and no-ops `ApplyAndRestart`.
- **`MainViewModel`** — replace `SyncClientToServerAsync`'s logic with the major-gated flow
  above. State changes:
  - Remove the `bool _versionSyncAttempted` once-per-session guard; add
    `string? _versionSyncCheckedFor` (the server version last handled this session).
  - Add observable state for the overlay: `VersionSyncInProgress` (bool), `VersionSyncTitle`
    (string), `VersionSyncStage` (string), `VersionSyncProgress` (int 0–100).
  - Keep the existing `_versionMismatch`/`VersionMismatchText` banner + `DismissMismatch`
    for the Unavailable case (re-message it for the new wording).
  - Remove the old `VersionSyncing` simple toast (replaced by the overlay).
  - The download `IProgress<int>` marshals to `VersionSyncProgress` on the main thread; the
    countdown uses `Task.Delay` on a background path with `MainThread` updates.
- **`MainPage.xaml`** — replace the `VersionSyncing` toast with a **blocking overlay**: a
  full-bleed semi-transparent `Grid` (covers the chat area, `InputTransparent="False"` so it
  blocks interaction) holding a centered card with the title, a `ProgressBar`
  (`Progress="{Binding VersionSyncProgress}"` ÷100 via converter or 0–1 binding) and the stage
  text, all gated on `VersionSyncInProgress`. Keep the mismatch banner.

### Server
- **No change.** Major-compatibility is computed client-side from the existing `/api/version`.

## Error handling

- `GetServerVersionAsync` null or `InstalledVersion` null → silently connect as-is (no UI).
- Cross-major but no build / offline / Velopack error → `Unavailable` → persistent banner,
  app usable, retried on next connect (the per-version guard only suppresses repeats for the
  *same* server version this session).
- `PrepareAsync` never throws (try/catch → `Unavailable`); `ApplyAndRestart` is the only
  disruptive call and runs only after a successful `Prepared`.
- The overlay blocks chat during a cross-major sync by design (the client can't use that
  backend until matched); on the matched relaunch nothing fires.

## Testing

Pure unit tests (no network/GUI):
- `SemVer.Major`: `"1.2.3"`→1, `"v3.0.0"`→3, `null`/junk→null.
- `VersionCompat.SameMajor`: `(1.0.0, 1.7.3)`→true, `(1.5.0, 1.2.0)`→true, `(1.0.0, 3.0.0)`
  →false, `(3.0.0, 1.0.0)`→false, junk/null→false.
- The upgrade-vs-downgrade *wording* decision via `SemVer.Compare` (e.g. a small pure
  `UpdateDirection(mine, srv)` if it keeps the VM clean) — `<0` Upgrade, `>0` Downgrade.

Runtime/manual (Velopack + real installs — cannot be unit-tested):
- The Velopack engine (pack, resolve up/down, apply) is already proven by the Phase-0 spike
  **and** by live runtime acceptance on 2026-06-18 (an installed 1.1.0 client auto-upgraded to
  1.2.0 against a 1.2.0 server and relaunched). *Note: that acceptance used same-major 1.1→1.2,
  which under THIS design would no longer trigger a sync* — so the cross-major path and the new
  overlay/countdown must be re-validated against two builds whose majors differ (e.g. a 1.x and
  a 2.x), using a local Velopack feed, confirming: cross-major upgrade syncs with visible
  progress + countdown + reconnect; cross-major downgrade likewise; same-major connect does
  nothing; and the Unavailable banner shows when the target build is absent.

## Out of scope (v1)

- Server-side enforcement / blocking incompatible clients (notify + client-side sync only).
- Explicit server-advertised `minClientVersion` / protocol number (major convention suffices;
  revisit if majors prove too coarse).
- Non-Windows client auto-update (Velopack is Windows here; stub no-ops).
- Background/staged updates within the same major (same-major simply connects; no silent
  in-major updating in v1).
- Connecting to multiple backends simultaneously (one backend at a time).
