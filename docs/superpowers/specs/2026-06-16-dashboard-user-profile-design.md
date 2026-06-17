# Server Dashboard — User Profile Popout — Design

**Date:** 2026-06-16
**Scope:** The web/WPF server dashboard (`/dashboard`, served by `MetricsController` + `wwwroot/dashboard.js`). Adds a clickable user profile modal plus the online-time tracking it needs.

**Builds on:** the CSP-compliant dashboard (`fix-dashboard-csp` branch) — external `dashboard.js` with `data-*` event delegation, strict `script-src 'self'`.

## Goal

Clicking a username in the dashboard's Users tab opens a centered modal showing that user's info: identity/status, membership/role, activity (last login, last seen, sessions), and stats (message count, most-used channel, total time on server). "Time on server" requires new tracking, built as part of this work.

## Key Decisions

- **Popout form:** centered modal over a dimmed backdrop (matches "box pop out"); close via × , backdrop click, or Esc.
- **Fields:** all available — identity+status, membership+role, activity, message stats — plus real total online time.
- **Online time:** build wall-clock online-time tracking now (`User.TotalOnlineSeconds` + hub connection accounting). Counts from ship date forward; historical time is unknowable.
- **Data delivery:** one aggregate endpoint `GET /api/admin/users/{id}/profile`, single round-trip per open.
- **CSP:** no policy change — all markup/JS/CSS load from `'self'`; modal uses `data-*` delegation, no inline handlers.

## Online-Time Tracking

### Schema

- New column `User.TotalOnlineSeconds` (`long`, default `0`) — accumulated wall-clock seconds the user has been connected.
- Added to the model and via raw `ALTER TABLE` in both the SQLite and PostgreSQL branches in `Program.cs` (project uses `EnsureCreated` + raw SQL, not EF migrations).

### Tracker (unit-testable)

A small pure unit `OnlineTimeTracker` over a `ConcurrentDictionary<int, (int Count, DateTime Since)>` with an injectable clock:

- `Connect(userId)` — increment the user's connection count; if it went 0→1, set `Since = now`.
- `Disconnect(userId) → long secondsToAdd` — decrement; when count reaches 0, return `(now − Since)` seconds (clamped ≥ 0) and clear the entry; otherwise return 0. An unmatched/extra disconnect returns 0 (never negative).
- `LiveSeconds(userId) → long` — if currently online, `(now − Since)`, else 0 (used by the profile endpoint for the in-progress session).

Overlapping connections (same user, two clients) count once — wall-clock, not summed per connection.

### Hub wiring

- `ChatHub.OnConnectedAsync` calls `tracker.Connect(UserId)`.
- `ChatHub.OnDisconnectedAsync` calls `tracker.Disconnect(UserId)`; if it returns > 0, add that to the user's `TotalOnlineSeconds` in the DB and save.
- The tracker is a singleton/static so the profile endpoint can read `LiveSeconds(userId)`.

### Caveats (surfaced in the UI)

- Counts only from ship date forward.
- A hard crash loses the in-progress (unsaved) session's time; only the going-offline event persists. No periodic checkpointing (out of scope unless requested).

## Profile Endpoint

`GET /api/admin/users/{id}/profile` on `AdminController`, same `IsLocal`/DashboardAdmin gating as the other admin endpoints. Returns one aggregate DTO (record in `Shared/Dtos.cs`, nullable where data may be absent). 404 for unknown id.

Assembled data:

- **Identity/status:** username, email, avatar URL, bio, current status, in-voice flag (from `ChatHub` online/voice snapshots).
- **Membership/role:** `CreatedAt` (join date), role in the current server (`ServerMembers`), mute state (`ServerMember.MutedUntil`), active temp-ban (`TempBans` with `ExpiresAt > now`).
- **Activity:** last login = most recent `UserSession.CreatedAt` for the user (+ its `IpAddress`/`UserAgent`); `LastSeenAt`; active session count (`UserSessions` with `RevokedAt == null`).
- **Stats:** total messages (`COUNT` on `Messages` by `AuthorId`); most-used channel (group-by `ChannelId`, top 1, resolved to name); total online seconds = `TotalOnlineSeconds + tracker.LiveSeconds(id)`.

The current server context is the dashboard's selected server (same `currentServerId` the other tabs use; passed as a query param or resolved server-side to the default server, consistent with existing admin endpoints).

## Dashboard UI

### Trigger

In `dashboard.js` `renderUsers`, the username cell becomes clickable: `<span class="user-link" data-click="profile" data-uid="${u.id}">${escapeHtml(u.username)}</span>` (cursor pointer + hover style). Wired through the existing click delegation → `openProfile(+d.uid)`.

### Modal

- `openProfile(userId)` fetches the endpoint, then builds a `#profileModal` element appended to `<body>` (rebuilt per open): a fixed full-screen dimmed backdrop containing a centered dark card.
- Card layout: header (avatar + username + status badge), then four labeled sections (identity, membership/role, activity, stats).
- Close: × button and backdrop both carry `data-click="closeProfile"` (delegated); Esc handled in the existing keydown delegation. Closing removes `#profileModal`.
- All user-supplied values (username, bio, IP, user-agent, channel name) pass through `escapeHtml`.
- Formatting client-side: online time as `Xd Yh Zm`, timestamps localized, last-seen as relative ("2 hours ago"); "never"/"—"/"0" for absent data.
- Modal CSS added to the dashboard's existing `<style>` block in `MetricsController.cs`.

No CSP change — markup/JS/CSS all from `'self'`, no inline handlers.

## Error Handling

- Unknown user id → 404; modal shows a brief "Couldn't load profile" message (try/catch like other loaders).
- Absent data degrades: never logged in → "Last login: never"; not a server member → role "—"; no messages → "0".
- Live online-time addition applies only when the hub snapshot shows the user connected.
- Tracker is concurrency-safe; decrement clamped ≥ 0 (no negative/double-subtracted time).

## Testing

### Server (xUnit, `TestDb`)

- `OnlineTimeTracker` unit tests (injectable clock): single connect→disconnect adds correct seconds; overlapping connections count once (not doubled); unmatched disconnect adds 0; `LiveSeconds` returns in-progress duration when online and 0 when offline.
- Profile endpoint: returns expected aggregate for a seeded user (join date, role, message count, last login from a seeded `UserSession`, mute/ban state); 404 for unknown id; enforces the `IsLocal`/admin gate.

### Dashboard JS

Not unit-tested (consistent with the rest of the dashboard). Verified in a headless browser: clicking a username opens the modal with the fields populated; × / backdrop / Esc close it; no CSP violations or JS errors.

## Out of Scope

- Periodic online-time checkpointing (crash resilience).
- Opening the profile from the Message Log / Audit tabs (Users tab only for now).
- Editing user fields from the modal (read-only view; existing role/mute/ban controls stay in the table).
