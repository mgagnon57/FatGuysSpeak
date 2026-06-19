# Drag-to-Move Users Between Voice Channels — Design

**Date:** 2026-06-18
**Status:** Approved for planning

## Goal

Let a moderator or admin **drag a user from one voice channel's occupant list and drop them
onto another voice channel**, force-moving that user's live voice session — exactly like
Discord's drag-between-voice. The move is real-time: the dragged user's audio re-routes and
everyone's occupant lists update immediately.

## Decisions (from brainstorming)

- **Scope:** voice force-move only. Works on users **currently connected to voice**. No text-
  channel moves; no pulling idle (non-voice) members into a call.
- **Drag source:** the voice channel's occupant list in the left sidebar. **Drop target:** a
  voice channel row in the sidebar.
- **Permission:** flat — any **Moderator or Admin** (or Owner) on that server may move any user.
- **Accountability:** every move writes an audit-log entry.

## Mechanism — hub method that relays to the target

The mover's client calls a new hub method; the server validates and tells the **target's**
client to perform the join itself, reusing the existing, proven `JoinVoiceChannel` path. This
mirrors how "Kick Voice" already force-acts on a user (server → target client → target obeys),
and avoids the server manipulating another user's SignalR group membership directly.

Flow:
1. **Mover client** (mod/admin): drop a dragged user onto a voice channel →
   `ChatHubService.MoveUserToVoiceChannelAsync(targetUserId, channelId)`.
2. **Server `ChatHub.MoveUserToVoiceChannel(targetUserId, channelId)`** validates (see below). On
   success it (a) writes an audit entry and (b) sends
   `Clients.User(targetUserId).SendAsync("ForceMoveToVoice", channelId, moverUsername)`.
3. **Target client** handles `ForceMoveToVoice(channelId, mover)` → runs its normal join-voice
   path (the same code a user-initiated voice join uses), which swaps SignalR groups, re-routes
   audio, updates `VoiceChannelMap`, and broadcasts `UserLeftVoice`/`UserJoinedVoice`. It also
   shows a toast: "Moved to #{channel} by {mover}."
4. **Everyone** in the old and new voice channels (including the mover) receives the
   leave/join broadcasts, so occupant lists refresh live with no extra work.

## Components

### Server
- **`ChatHub.MoveUserToVoiceChannel(int targetUserId, int channelId)`** (new). Validation, all
  silent no-ops on failure (consistent with the other hub voice methods):
  - Resolve the target voice channel; it must exist and be `ChannelType.Voice`… (the app stores
    channels as `Text` but treats them as voice-capable, so the practical check is: the channel
    exists and the **target is currently in voice in some channel on the same server** — i.e.
    `VoiceChannelMap[targetUserId]` exists and that channel's `ServerId` == target channel's
    `ServerId`). Don't move across servers.
  - Caller must be a member of that server with `Role >= ServerRole.Moderator`.
  - Target must be a member of that server and currently in voice (`VoiceChannelMap` contains
    `targetUserId`). If the target is already in `channelId`, no-op.
  - Respect channel read permission: if a `ChannelPermission` with `MinRoleToRead > Member`
    exists, the target's role must satisfy it (don't move someone into a channel they can't access).
  - On success: add an `AuditLog` row and send `ForceMoveToVoice` to the target.
- **Audit entry:** `new AuditLog { ServerId = <channel server>, ActorId = UserId,
  ActorUsername = <mover>, Action = "VoiceMoved", TargetId = targetUserId,
  TargetUsername = <target>, Detail = $"to #{channelName}" }`. (Reuses the existing `AuditLog`
  entity + the dashboard audit-log view; add `"VoiceMoved"` to the dashboard's action-color map
  so it renders cleanly — see `dashboard.js` color map.)

### Client
- **`ChatHubService`**: `Task MoveUserToVoiceChannelAsync(int targetUserId, int channelId)`
  (invokes the hub method) and a `ForceMoveToVoice` event surfaced from the `HubConnection`.
- **`MainViewModel`**:
  - Handle `ForceMoveToVoice(channelId, mover)` → invoke the same logic as a user voice join for
    `channelId`, then `toast.Show($"Moved to #{name} by {mover}")`.
  - `MoveUserToVoiceChannel(int targetUserId, int channelId)` — called from the drop; guards on a
    `CanModerate` flag (current user is mod/admin on the selected server) before calling the hub.
  - `CanModerate` bound property (derive from the existing role info used for `IsServerAdmin` /
    the role map already loaded for member icons).
- **`MainPage.xaml`** (left sidebar voice section):
  - Voice **occupant** item template: add a `DragGestureRecognizer` (`CanDrag="{Binding
    BindingContext.CanModerate, Source=RootPage}"`); `DragStarting` puts the occupant's userId in
    `e.Data.Properties["userId"]`.
  - Voice **channel** row: add a `DropGestureRecognizer` (`AllowDrop="{Binding ...CanModerate...}"`);
    `Drop` reads `userId` and calls a command `MoveUserToVoiceChannel(userId, thisChannelId)`.
  - Optional polish: highlight the channel row on `DragOver` to signal a valid drop.

### Shared
- No new DTO required (hub method takes two ints; the relay event carries `int channelId, string mover`).

## Permissions

Flat: caller's `ServerRole >= Moderator` on the target channel's server is sufficient to move any
user (including other mods/admins). The **server is authoritative**; the client only shows the
drag/drop affordance when `CanModerate` is true. A non-privileged caller invoking the hub method
directly is silently ignored.

## Error handling

All server-side failures are silent no-ops (matching `JoinVoiceChannel`, `KickFromVoice`, etc.):
caller not a mod/admin; target not a member; target not in voice; channel missing / on another
server / target lacks read access; target already in the destination. The success signal is the
live join/leave broadcast; no error is surfaced to the mover (the UI simply doesn't change). The
moved user always gets the toast so the move isn't silent to them.

## Testing

Hub tests in the `[Collection("HubTests")]` collection (static `ConcurrentDictionary` state →
no parallelism), using `TestHelpers.MockHub()` and seeded server/members:
- Moderator/Admin moves an in-voice target to another voice channel on the same server →
  `Clients.User(target).SendAsync("ForceMoveToVoice", channelId, ...)` is invoked **and** an
  `AuditLog` row with `Action="VoiceMoved"` is written.
- Non-privileged caller (Member) → `ForceMoveToVoice` **not** sent, no audit row.
- Target not in voice → not sent.
- Channel on a different server / nonexistent → not sent.
- Target already in the destination channel → not sent (no-op).

The drag-and-drop UI itself is verified manually with two clients (MAUI gesture recognizers are
Windows-native; not unit-tested).

## Out of scope (v1)

- Pulling idle (non-voice) members into a call; text-channel moves.
- Drag from the right-side members panel (only the voice occupant list is a drag source).
- Role-hierarchy restrictions (flat permission by decision).
- Reordering channels/categories by drag (unrelated).
