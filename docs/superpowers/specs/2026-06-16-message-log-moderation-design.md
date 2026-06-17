# Message Log Moderation — Design

**Date:** 2026-06-16
**Status:** Approved for planning

## Goal

Turn the dashboard's Message Log tab from a one-message-at-a-time viewer into a
bulk-moderation console: search message content, filter by date/server, page
through full history, select many messages (or whole filters) and delete them in
one action, with deletes auditable and reversible by default and a guarded
permanent-delete escape hatch.

## Current State (baseline)

- `GET /api/admin/messages` (AdminController): filters `author`, `channel`,
  `source`; orders newest-first; hard cap of 500 rows. Projection returns
  `Id, Content, Author (username), Channel, Server, Source, CreatedAt, IsDeleted`.
- `DELETE /api/admin/messages/{id}`: soft-delete one message — sets
  `IsDeleted = true`, **overwrites `Content` with "[deleted by admin]"**, writes a
  per-message AuditLog entry, broadcasts `MessageDeleted` to `channel-{id}`.
- `MessagesController.GetMessages` already filters `!m.IsDeleted`, so normal
  clients never receive soft-deleted messages regardless of the content value.
- Dashboard tab (MetricsController HTML + `dashboard.js`): author/channel text
  filters, source dropdown, "show deleted" checkbox, table
  (Time/Author/Channel/Server/Source/Content[truncated 120]/Action), per-row
  Delete button. No bulk actions, no content search, no date filter, no paging,
  no restore.
- CSP constraint: dashboard JS must be external and use `data-*` event delegation;
  **no inline handlers or scripts** (enforced; violating it silently breaks the tab).
- `openProfile(userId)` already exists in `dashboard.js`, wired via
  `data-click="profile" data-uid`.

## Key Design Decision: preserve content on soft-delete

Because normal clients are already excluded from `IsDeleted` messages, soft-delete
will **stop overwriting `Content`** and rely solely on the `IsDeleted` flag. This:

- makes **Restore** possible (original text survives a delete→restore round-trip),
- lets admins see *what* was removed in the "show deleted" view,
- leaks nothing to normal users (the regular message API/hub still filters
  `IsDeleted`).

No new DB column is required. The existing single-message delete endpoint is
updated to match (no longer overwrites content).

## Server API (AdminController — all loopback + `DashboardAdmin` gated)

### Extended: `GET /api/admin/messages`
New query params (all optional, additive to existing `author`/`channel`/`source`/`limit`):

- `keyword` — case-insensitive substring match on `Content`.
- `serverId` — restrict to one server.
- `from`, `to` — UTC ISO timestamps; filter on `CreatedAt` (`from <= CreatedAt <= to`).
- `beforeId` — cursor for pagination: return only messages with `Id < beforeId`,
  newest-first, up to `limit`. Omitted = first (newest) page.

Projection adds `AuthorId` and returns **full** `Content` (truncation moves to the
client). Ordering: `OrderByDescending(m => m.Id)` (stable cursor; Id is monotonic).

### New: `POST /api/admin/messages/delete`
Body:
```json
{ "ids": [int],            // explicit selection (multi-select / select-all-in-view)
  "filter": {              // OR criteria-based purge (mutually exclusive with ids)
    "author": "string?", "channel": "string?", "serverId": int?,
    "source": "string?", "keyword": "string?", "from": "iso?", "to": "iso?" },
  "mode": "soft" | "hard"  // default "soft"
}
```
- Exactly one of `ids` or `filter` must be present (400 otherwise).
- `soft`: set `IsDeleted = true` on all matches (content preserved).
- `hard`: `RemoveRange` the matched rows (irreversible).
- Returns `{ affected: int, channelIds: [int] }` (distinct affected channels).
- Broadcasts `MessageDeleted(msgId, channelId)` per affected message's channel
  (grouped send per channel group).
- Writes **one** summarizing AuditLog entry: action `MessagesBulkDeleted`
  (or `MessagesBulkPurged` for hard), detail includes count + criteria summary.
- Safety cap: a single call affects at most 5,000 rows; larger filter matches
  return 400 with the count, so the operator narrows the filter (prevents an
  accidental "delete the entire database").

### New: `POST /api/admin/messages/restore`
Same body shape (`ids` or `filter`), no `mode`. Sets `IsDeleted = false` on
soft-deleted matches. Returns `{ affected, channelIds }`. Writes one
`MessagesRestored` AuditLog entry. **Live clients are not re-injected** — restore
is authoritative for the database and dashboard; connected clients see restored
messages on next channel load. (Documented limitation; avoids touching the MAUI
client.)

### New: `GET /api/admin/servers`
Returns `[{ id, name }]` for the server filter dropdown.

### Updated: `DELETE /api/admin/messages/{id}`
Unchanged contract, but no longer overwrites `Content` (consistency with the new
soft-delete semantics). Still writes its per-message audit entry.

## Dashboard UI (MetricsController HTML + dashboard.js)

All new interactive elements use `data-click` / `data-change` / `data-input`
delegation — no inline handlers (CSP).

Filter row additions:
- `keyword` search box (content search).
- Date-range control: preset `<select>` (All / Last 24h / Last 7d / Last 30d /
  Custom). Custom reveals two date inputs (`from`/`to`).
- Server `<select>` populated from `GET /api/admin/servers`.

Table + selection:
- Leading checkbox column; header "select all" checkbox selects all currently
  loaded rows (respecting filters).
- Bulk action bar (shown when ≥1 row selected): "Delete selected (N)", a
  "Permanent" toggle (hard vs soft), and an "X selected — Clear" indicator.
- "Delete all matching filter" button (purge by criteria — drives
  purge-by-user/channel/date by setting the relevant filter first). Operates on
  the whole filter, not just the loaded page.
- Confirmation summary before any bulk/purge action: "Delete N messages from X
  authors across Y channels?" For **hard** deletes, a second explicit
  confirmation (type-to-confirm or a distinct "Permanently delete" button).
- "Load more" button at the table foot using `beforeId` (oldest loaded Id).

Row enhancements:
- Click content cell to expand/collapse full text (client-side; full content now
  available from the API).
- Author rendered as a `user-link` (`data-click="profile" data-uid="{authorId}"`)
  → opens existing profile modal.
- Deleted rows (in "show deleted" view) show **Restore** instead of Delete.

Export:
- "Export CSV" downloads the currently loaded/filtered rows client-side
  (Time, Author, Channel, Server, Source, Content, Deleted). No server round-trip.

## Build Phases (ship + review each)

**Phase 1 — Foundation & low-risk wins**
- Soft-delete preserves content (server); update single-delete endpoint.
- `AuthorId` + full content in `GET messages` projection.
- `POST messages/restore` (ids). Dashboard: Restore button, expandable content,
  author→profile link, Export CSV.

**Phase 2 — Discovery**
- `keyword`, `serverId`, `from`/`to`, `beforeId` on `GET messages`;
  `GET /api/admin/servers`. Dashboard: keyword box, date-range control, server
  dropdown, "Load more".

**Phase 3 — Bulk moderation**
- `POST messages/delete` (ids + filter, soft/hard, 5,000 cap, summarizing audit).
  Dashboard: row checkboxes, select-all, bulk action bar, "Delete all matching
  filter", confirmation summaries, Permanent toggle with extra confirm. Bulk
  restore by filter.

Each phase leaves the tab fully functional.

## Testing (AdminController + TestDb, xUnit)

- Soft bulk-delete by ids flags `IsDeleted`, preserves content.
- Soft-delete then restore round-trips original content intact.
- Bulk-delete by filter matches the same set the GET filter would.
- Hard-delete removes rows from the DB.
- `keyword` filter matches content substring (case-insensitive).
- Date-range (`from`/`to`) bounds inclusive.
- `beforeId` returns the correct next-older page; no overlap with first page.
- Purge-by-author and purge-by-channel affect all matches (not just a page).
- 5,000-row safety cap returns 400 with the count when exceeded.
- `AuthorId` present in projection.
- Each bulk op writes exactly one AuditLog entry; single-delete still writes one.
- `GET /api/admin/servers` returns seeded server(s).

## Out of Scope

- Live client re-injection on restore (clients refresh to see restored messages).
- Editing message content from the dashboard.
- Changes to the MAUI client.
- Hard-delete of audit-log entries themselves.
