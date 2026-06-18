# Remote Control During Screen Share — Design

**Date:** 2026-06-17
**Status:** Approved for planning

## Goal

Let a viewer of a live screen share drive the sharer's machine with full mouse and
keyboard control. Available for any share the sharer chose (single window or full
desktop). Control can be initiated from either side, but the sharer's machine only
ever begins injecting input after the **sharer** explicitly confirms. No input
clamping; the sharer is warned clearly and can revoke instantly.

## Decisions (from brainstorming)

- **Control scope:** full mouse + keyboard injection on the sharer's machine.
- **Share scope:** offerable for any active share — window or full desktop.
- **Initiation:** both directions — viewer can *request*, sharer can *offer* — and in
  both cases the session starts only after the sharer's machine consents (sharer
  grants the request, or the viewer accepts the sharer's offer).
- **Containment:** none. All input forwarded as-is. The sharer sees a blunt warning
  at grant time and has instant revoke (button + panic hotkey).
- **Concurrency:** one controller per stream. A new grant is **denied** while a
  controller is already active (no silent take-over).
- **Platform:** input injection is Windows-only (Win32 `SendInput`). The
  request/offer/control UI is gated to Windows for v1.

## Existing pipeline (context)

Screen share already works over SignalR:
- Sharer: `ChatHub.StartStream(channelId)` registers in
  `ActiveStreamers: userId → (channelId, serverId, username)` and joins
  `stream-{channelId}`. Frames pushed via `SendStreamFrame(byte[])` →
  `OthersInGroup(stream-{channelId})` as `ReceiveStreamFrame`. `StopStream()` ends it.
- Viewer: `WatchStream(channelId)` joins the group and receives frames into
  `MainViewModel.StreamFrame` (an `ImageSource` shown in an `Image`, also a
  full-screen stream window).
- Capture (`ScreenStreamService`, Windows): full desktop = `CopyFromScreen` over
  `(0,0)`→`(SM_CXSCREEN, SM_CYSCREEN)`; window = `PrintWindow` over the target
  window's `GetWindowRect`. Frame is then downscaled to `maxWidth` preserving aspect.

The capture source rectangle (screen size, or the window rect) is the coordinate
space the controller's normalized clicks map back into.

## Coordinate contract

The controller sends **normalized** coordinates `x,y ∈ [0,1]` measured against the
rendered frame `Image`'s display size. The sharer maps them to its current capture
rect:
- Full desktop: `screenX = x * SM_CXSCREEN`, `screenY = y * SM_CYSCREEN`.
- Window share: `screenX = rect.Left + x * (rect.Right - rect.Left)`,
  `screenY = rect.Top  + y * (rect.Bottom - rect.Top)`.

These screen coordinates are converted to absolute `SendInput` coordinates
(`0..65535` across the primary screen via `MOUSEEVENTF_ABSOLUTE`). Normalized
coordinates avoid shipping resolution metadata around and survive the downscale.

## Shared DTO

`FatGuysSpeak.Shared/DTOs.cs`:

```csharp
public enum RemoteInputKind { Move, Down, Up, Wheel, KeyDown, KeyUp }

public record RemoteInputDto(
    RemoteInputKind Kind,
    double X = 0, double Y = 0,   // normalized 0..1 (Move/Down/Up/Wheel)
    int Button = 0,               // 0=left,1=right,2=middle (Down/Up)
    int Delta = 0,                // wheel notches *120 (Wheel)
    int KeyCode = 0);             // Win32 virtual-key code (KeyDown/KeyUp)
```

Modifier state is conveyed by separate `KeyDown`/`KeyUp` events for the modifier
virtual-keys (VK_SHIFT/CONTROL/MENU/LWIN), so no separate modifier field is needed.

## Server: ChatHub additions

New static state:
```csharp
// streamerUserId → (controllerUserId, channelId)
private static readonly ConcurrentDictionary<int, (int ControllerId, int ChannelId)> RemoteControlSessions = new();
```

Hub methods (all derive the caller's userId from the JWT claim, as existing methods do):

- `RequestControl(int streamerId)` — caller is a viewer. Verify caller is in
  `stream-{channelId}` for the streamer's active stream. Notify the streamer:
  `Clients.User(streamerId).ControlRequested(callerId, callerUsername)`.
- `OfferControl(int viewerId)` — caller is the streamer of an active stream. Notify
  the viewer: `Clients.User(viewerId).ControlOffered(streamerId, streamerUsername)`.
- `GrantControl(int controllerId)` — caller is the streamer. **Deny if a session
  already exists** for this streamer (notify caller `ControlBusy`). Otherwise set
  `RemoteControlSessions[streamerId] = (controllerId, channelId)` and notify both:
  controller gets `ControlGranted(streamerId)`, streamer UI reflects active control.
- `AcceptControl(int streamerId)` — caller is the viewer accepting an offer. Mirror
  of `GrantControl` but keyed on the streamer who offered; verify an offer is
  outstanding (tracked client-side via the `ControlOffered` event; server simply
  requires no existing session and that the streamer's stream is active). Set the
  session, notify both.
- `DenyControl(int otherUserId)` — decline a request/offer; notify the other party
  `ControlDeclined`.
- `SendRemoteInput(RemoteInputDto dto)` — **the security gate.** Look up the
  caller as a controller: find `streamerId` where
  `RemoteControlSessions[streamerId].ControllerId == callerId`. If none, drop
  silently. Otherwise relay to the streamer only:
  `Clients.User(streamerId).ReceiveRemoteInput(dto)`.
- `StopControl()` — caller is the streamer; remove the session; notify the controller
  `ControlEnded`.
- `ReleaseControl()` — caller is the controller; find and remove the session where
  they are controller; notify the streamer `ControlEnded`.

Automatic teardown:
- `StopStream()` and `OnDisconnectedAsync` must remove any `RemoteControlSessions`
  entry where the disconnecting/stopping user is **either** the streamer or the
  controller, and notify the other party `ControlEnded`.

Client→server events used: `ControlRequested`, `ControlOffered`, `ControlGranted`,
`ControlDeclined`, `ControlBusy`, `ControlEnded`, `ReceiveRemoteInput`.

**Authorization invariants (the core of the feature's safety):**
1. A session only exists after the streamer's machine consents (grant or accept).
2. `SendRemoteInput` is relayed only from the active controller, only to that
   session's streamer. Any other sender is dropped.
3. At most one controller per streamer.
4. Session is cleared on stop/release/stream-end/disconnect of either party.

## Client: RemoteInputService (Windows)

`FatGuysSpeak.Client/Services/RemoteInputService.cs` (`#if WINDOWS`, no-op stub
otherwise), owned alongside `ScreenStreamService` which exposes its current capture
rect:
- `void Inject(RemoteInputDto dto)` — maps normalized coords to the active capture
  rect and calls Win32 `SendInput` (mouse: `MOUSEEVENTF_MOVE|ABSOLUTE`,
  `LEFTDOWN/UP`, `RIGHTDOWN/UP`, `MIDDLEDOWN/UP`, `WHEEL`; keyboard:
  `KEYEVENTF_KEYDOWN/KEYUP` with the virtual-key code, `EXTENDEDKEY` for the keys
  that need it).
- `ScreenStreamService` gains `RECT CurrentCaptureRect` (screen rect for desktop,
  `GetWindowRect` for window) so injection maps to live bounds even if the window
  moves.

Only the sharer side injects. The sharer wires `ReceiveRemoteInput` → `Inject`
**only while a control session is active**; the handler ignores input if no session.

## Client: controller capture + UI

- When the local user is the active controller, attach pointer + key handlers to the
  stream `Image` (and the full-screen stream window): `PointerMoved` (throttled to
  ~30–60/s via a timestamp gate), `PointerPressed`/`Released` (button), wheel, and
  key down/up. Each converts to a `RemoteInputDto` (normalized against the control's
  render size) and calls `hub.SendRemoteInput`.
- Request/offer UI:
  - Viewer watching a stream sees a "Request control" action; sharer sees a
    "Give control" action listing current viewers.
  - Incoming `ControlRequested` → sharer prompt "{user} wants control of your
    screen. They will control your whole PC. [Allow] [Deny]".
  - Incoming `ControlOffered` → viewer prompt "{user} is giving you control of their
    screen. [Accept] [Decline]".
- Active-control indicators:
  - Sharer: a persistent, always-visible banner "{user} is controlling your screen"
    with a prominent **Stop control** button, plus a global **panic hotkey**
    (`Ctrl+Alt+Break`) that calls `StopControl` and tears down injection immediately.
  - Controller: a "You are controlling {user}'s screen — Release" banner.
- `ControlEnded`/`ControlDeclined`/`ControlBusy` update both sides and remove
  capture handlers / injection wiring.

## Error handling

- `SendRemoteInput` with no active session: dropped server-side (no error surfaced).
- Injection failures (`SendInput` returns 0): swallowed per-event; never crash the
  stream.
- A window-share whose window closes mid-control: capture rect read fails → injection
  no-ops; stream/stop flow already handles the window going away.
- Disconnects: covered by `OnDisconnectedAsync` teardown.

## Testing

Hub tests (`[Collection("HubTests")]`, mock `IHubContext`):
- `SendRemoteInput` from a non-controller is dropped (no relay).
- `SendRemoteInput` from the active controller relays only to the streamer.
- `GrantControl` opens a session; a second `GrantControl` while active returns
  `ControlBusy` and does not change the controller.
- `OfferControl`→`AcceptControl` opens a session.
- `StopControl`/`ReleaseControl` clear the session and notify the other party.
- `StopStream` and disconnect clear any session involving the user.

Pure-logic test:
- Normalized→screen coordinate mapping for both desktop and window rects (extract the
  mapping into a static helper so it's testable without Win32).

Runtime verification (not unit-tested): actual `SendInput` injection, captured by
driving two live clients during a share.

## Out of scope (v1)

- Input clamping / escape-shortcut filtering (explicitly declined).
- Non-Windows input injection (UI gated to Windows).
- File transfer, clipboard sync, multi-monitor selection beyond the primary screen
  used by the existing capture.
- Take-over of an in-progress control session (new grants are denied while busy).
