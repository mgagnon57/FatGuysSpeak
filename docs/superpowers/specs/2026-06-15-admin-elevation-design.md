# Admin Elevation & Channel Creation — Design Spec

**Date:** 2026-06-15

## Summary

Allow in-app admin management: any existing Admin can elevate a non-Admin user to Admin via right-click, and any Admin can create new channels via a "+" button or right-click. The first Admin is designated via the localhost dashboard (existing `AdminController`); subsequent elevation happens inside the app.

---

## Server Changes

### `ServersController.SetMemberRole`

Current behaviour: only the server owner (`OwnerId`) can promote a user to Admin. Since the seeded server has `OwnerId=0` (no real user), this blocks all in-app promotion.

New behaviour:
- Any Admin can promote a Member or Moderator to Admin.
- Promoting a user who is already Admin returns `400 Bad Request` (defence in depth; the UI also prevents this).
- Demoting an existing Admin remains owner-only (`OwnerId` check stays). Since `OwnerId=0`, demotion is only possible via the dashboard — this is intentional.
- All other existing guards stay (`actor must be Admin`, `cannot change own role`).

### `ServersController.GetMembersWithRoles`

Current behaviour: Admin-only (`caller.Role < Admin` → 403).

New behaviour: any server member can call this endpoint. Knowing who holds the Admin role is public information (standard in chat apps) and is required by the client to conditionally show "Make Admin."

No new endpoints are needed. `CreateChannelAsync` (`POST api/servers/{id}/channels`) and `SetMemberRoleAsync` (`PUT api/servers/{id}/members/{userId}/role`) already exist in both the server and `ApiService`.

---

## Client — Role State

### `ApiService`

Add `GetMemberRolesAsync(int serverId)` → `List<ServerMemberDto>?` calling `GET api/servers/{serverId}/members/details`.

### `MainViewModel`

**New state:**
- `private readonly Dictionary<int, ServerRole> _memberRoles` — populated on server join, updated by SignalR events.
- `public ServerRole MyRole` — stored from `ServerDto.MyRole` (already returned by `GetServersAsync`).
- `public bool IsAdmin => MyRole == ServerRole.Admin` — drives all conditional UI visibility.

**Loading roles:** After loading the server on login, call `GetMemberRolesAsync` and populate `_memberRoles`.

**Keeping roles current:** The existing `MemberRoleChanged` SignalR event (already wired in `ChatHubService`) is handled in `MainViewModel` to:
1. Update `_memberRoles[userId]` with the new role.
2. If `userId == CurrentUserId`, update `MyRole` and raise `PropertyChanged` for `IsAdmin`.

**New commands:**

`ElevateToAdminCommand(int userId)`:
- Guard: `IsAdmin && _memberRoles.GetValueOrDefault(userId) != ServerRole.Admin`.
- Calls `PUT api/servers/{serverId}/members/{userId}/role` with `Role = Admin`.
- Shows a toast on success.

`CreateChannelCommand`:
- Guard: `IsAdmin`.
- Calls `DisplayPromptAsync("New Channel", "Enter channel name:")`.
- Returns early if result is null or whitespace.
- Calls `CreateChannelAsync(serverId, new CreateChannelRequest(name, ChannelType.Text))`.
- The existing `ChannelCreated` SignalR event already appends the new channel to the channel list for all clients.

---

## Client — UI (MainPage.xaml)

All context menus use MAUI `MenuFlyout` (Windows-supported). All visibility/enable state is bound to `MainViewModel`.

### Connected Users List (left panel)

Each user item gets `FlyoutBase.ContextFlyout`:

```xml
<FlyoutBase.ContextFlyout>
    <MenuFlyout>
        <MenuFlyoutItem
            Text="Make Admin"
            Command="{Binding Source={x:Reference PageRoot}, Path=BindingContext.ElevateToAdminCommand}"
            CommandParameter="{Binding UserId}"
            IsEnabled="{Binding Source={x:Reference PageRoot}, Path=BindingContext.IsAdmin}" />
    </MenuFlyout>
</FlyoutBase.ContextFlyout>
```

The item is only enabled when `IsAdmin` is true. `ElevateToAdminCommand` internally checks the target's current role and no-ops (or shows a message) if already Admin.

### Voice Strip

Same `MenuFlyout` pattern on each `VoiceParticipantViewModel` item. `VoiceParticipantViewModel` already exposes `UserId`.

### Channel Section

Two entry points for the same `CreateChannelCommand`:

1. A `Button` with text "+" placed inline with the "CHANNELS" section header. `IsVisible` is bound to `IsAdmin`.
2. A `FlyoutBase.ContextFlyout` on the `Border` wrapping the channel `CollectionView`:

```xml
<FlyoutBase.ContextFlyout>
    <MenuFlyout>
        <MenuFlyoutItem
            Text="Create Channel"
            Command="{Binding CreateChannelCommand}"
            IsEnabled="{Binding IsAdmin}" />
    </MenuFlyout>
</FlyoutBase.ContextFlyout>
```

---

## Error Handling

- `ElevateToAdminCommand`: if the API returns non-success, show a toast with "Failed to update role."
- `CreateChannelCommand`: if API returns non-success, show a toast with "Failed to create channel."
- `GetMemberRolesAsync` failure on login: log the error, leave `_memberRoles` empty (user sees no "Make Admin" options but app continues to function).

---

## Tests

- `ServersControllerTests`: Admin can promote Member → Admin; promoting already-Admin returns 400; non-Admin cannot promote.
- `RoleEnforcementTests`: non-Admin cannot create a channel; Admin can.
- `MainViewModel` role map logic is pure enough to test directly if extracted to a helper; otherwise covered by integration path.
