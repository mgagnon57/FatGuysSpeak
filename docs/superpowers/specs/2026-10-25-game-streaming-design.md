# Stream Game vs. Share Screen — Design

Date: 2026-10-25
Status: draft, awaiting owner approval

## Why

Today there is one "📺 Share Screen" button. Clicking it opens a window picker, and the user has to find and pick
their game by hand every time. The owner (BillyBong) mostly wants to show friends the game he is playing, and
separately wants a way to look at someone else's PC to troubleshoot it. Those are two different jobs with different
needs, so they become two buttons.

## Goals

1. **Stream Game** — one click streams the game you're playing. It works out which window is the game on its own.
2. **Share Screen** — the existing pick-a-window-or-whole-screen flow, kept for troubleshooting. Remote control
   ("request control") stays here.
3. Games actually show up. The current capture (`PrintWindow` / GDI `CopyFromScreen` in `ScreenStreamService`)
   often gives black frames for GPU-rendered games.
4. Viewers stop hearing the voice chat echoed back through a stream.

## Non-goals

- No change to how frames travel (MJPEG over SignalR, `_busy` frame dropping, adaptive quality) — only how frames
  are *captured* and which window is chosen.
- No recording or saving of streams, and no new server storage. (Voice is never recorded or transcribed — see
  CLAUDE.md. Nothing here may capture microphone or voice-chat audio.)
- Not building an OBS: no scenes, overlays, webcam-in-game compositing.

## Anti-goals (would make this a failure even if it "works")

- Stream Game leaking the desktop: if the user alt-tabs to a browser or a DM, viewers must keep seeing only the
  game, never other windows.
- Stream Game sending FatGuysSpeak voice chat, PorkChop, music, or notification sounds. (Voice that comes out of
  the game itself — e.g. a game's built-in voice chat — is part of game sound and IS sent; owner decided this.)
- Auto-picking the wrong thing silently (e.g. a full-screen browser video) with no way to correct it.

## Behavior (decided with the owner)

### Two buttons, bottom controls bar

Replace the single `ToggleStreamCommand` button (MainPage.xaml bottom bar, `StreamButtonLabel`) with:

- **🎮 Stream Game** / ⏹ Stop Streaming
- **🖥 Share Screen** / ⏹ Stop Sharing

Only one stream at a time (server already enforces one `ActiveStreamers` entry per user). While one is running the
other button is disabled.

### Stream Game — detecting the game

Owner's ask: "make it smart". Owner usually plays **borderless fullscreen windowed**, sometimes true fullscreen,
sometimes windowed. Owner has 3 monitors, Windows 11.

**Reference case — Wardogs (what the group plays now), observed live on the owner's PC:** process
`WardogsClient-Win64-Shipping`, window title "Wardogs", window exactly covers the 2560×1440 primary monitor
(borderless fullscreen). Installed at `C:\Program Files (x86)\Steam\steamapps\common\Wardogs\Wardogs\Binaries\Win64\`.
Its exe path and loaded modules are **not readable** from another process (access denied — likely anti-cheat), so
path- and graphics-DLL-based signals are unavailable for exactly this kind of game. Detection must therefore also work
by **process name**: enumerate the game library folders (Steam libraries from `libraryfolders.vdf`, Epic, etc.),
collect the `.exe` file names inside them, and treat a running process whose name matches one of those as a game.
Wardogs must be auto-detected confidently (acceptance test, using these facts as test data).

Detection scores visible top-level windows (reuse `ScreenStreamService.GetOpenWindows()` enumeration) on signals
like:

- window covers an entire monitor (borderless fullscreen) — strong signal
- process name matches an `.exe` found inside a game library folder (works even when the path is unreadable)
- executable path under a known game library folder (when readable): `steamapps\common`, `Epic Games`, `Riot Games`,
  `Battle.net` / Blizzard, `XboxGames` / `WindowsApps` games, `GOG Galaxy\Games`, `EA Games`, `Ubisoft Game Launcher\games`
- process has a 3D graphics runtime loaded (d3d11/d3d12/dxgi/vulkan/opengl)
- exclude: FatGuysSpeak itself, explorer/shell, browsers, launchers (Steam/Epic/Battle.net client windows), Discord,
  OBS, known non-game apps

Outcome:
- **Exactly one confident game** → start streaming it immediately.
- **None or several** → open the picker (existing `WindowPickerPage`) with likely games sorted to the top and
  labeled, so the user picks with one click.
- **Correcting a wrong pick** (my call): while streaming a game, a small "Streaming Wardogs · Change" link sits by
  the button; Change opens the picker and switches the stream to the chosen window without stopping it.

Detection logic must be pure and unit-testable: put the scoring/ranking (input: a list of window facts — title,
process name, exe path, bounds, monitor bounds, loaded-graphics flag; output: ranked candidates + "confident pick or
not") in **FatGuysSpeak.Shared** (CLAUDE.md: client logic that must be testable lives in Shared), with tests in
FatGuysSpeak.Tests. The Win32 fact-gathering stays in the client.

### Stream Game — capture

Capture the chosen game **window** (not the screen region) with **Windows.Graphics.Capture**
(`GraphicsCaptureItem` via `IGraphicsCaptureItemInterop.CreateForWindow`, Direct3D11 frame pool), the API OBS and
Discord use. This handles GPU-rendered borderless/windowed games and most exclusive-fullscreen games, and because it
captures the window itself, alt-tabbing away never shows the desktop (anti-goal 1). Frames are converted to JPEG
and fed into the existing `FrameCaptured` pipeline unchanged.

### Stream Game — sound

Send **only the game process's audio**, using Windows' per-process loopback capture
(`ActivateAudioInterfaceAsync` with `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`, include-target-process-tree,
Windows 10 build 20348 / "2004"-era and later — builder to confirm exact minimum build). Feed it into the existing
Opus encode → `SendStreamAudioAsync` path. Never fall back to whole-system loopback.

Game sound is **on by default** (owner), with a "Game sound" on/off switch next to the Stream Game button that the
streamer can flip at any time; the choice is remembered in Preferences.

### Windows 10

Stream Game requires **Windows 11** (owner). On Windows 10 the Stream Game button is disabled and explains: "Stream
Game needs Windows 11 — upgrade to use it. Share Screen still works." This also removes the need for any Windows 10
fallback for window capture, the yellow capture border (disable it — supported on Windows 11), or per-process audio.

### Stream Game — game exits

If the captured game window closes, the stream stops on its own and the streamer sees a short notice. (My call.)

### Share Screen

Same as today: picker of windows plus whole screen, existing capture code, remote control available.
**Change: no sound at all** (owner: "no sound for share screen") — Share Screen no longer starts loopback audio.

### What viewers see

Viewers see which mode it is (my call): "📺 BillyBong is streaming *<game name>*" vs. "🖥 BillyBong is sharing their
screen". Game name = a cleaned-up name derived from the
process/exe (e.g. `WardogsClient-Win64-Shipping` → "Wardogs"; strip Unreal/Unity suffixes like `-Win64-Shipping`,
`Client`, `.exe`), **never the raw window title**, so a browser tab or DM title picked by mistake can't be broadcast
server-wide (my call). This needs the stream kind + title to reach other
clients: extend the `StartStream` hub method / `StreamStarted` event (ChatHub.cs ~line 410, ChatHubService
`StreamStarted`), the server-wide "started streaming in #channel" notification, and `ActiveStreamers` state so late
joiners get it too (ChatHub.cs ~line 102 replays active streams on connect).

**Remote control is only offered for Share Screen streams** (my call): `CanRequestControl` must be false for game
streams, and the server should refuse `RequestControl`/`GrantControl` for a game stream, not just hide the button.

## Constraints

- Windows-only (`#if WINDOWS` like the existing `ScreenStreamService`); other platforms keep the no-op stubs.
- Client targets `net9.0-windows10.0.19041.0`, min `10.0.17763.0`; Stream Game itself is gated at runtime to
  Windows 11 (build 22000+).
- Keep the 4 MB SignalR message cap and the `_busy` drop-frame behavior.
- Hub tests live in `[Collection("HubTests")]`. Every feature needs tests (owner rule).
- Old v0.12.x clients may briefly talk to the new server before auto-updating; SignalR hub methods can't be
  overloaded, so changing `StartStream`'s signature must not break them (e.g. add a new method, or make the server
  tolerate the old call).

## Left to the builder

- How to get D3D11/WinRT interop in .NET 9 MAUI (raw COM interop vs. a small package such as Vortice) — prefer the
  fewest new dependencies.
- Exact detection weights/thresholds and the exclusion list, as long as the outcomes above hold and are tested.
- Exact hub API shape for stream kind/title, within the compatibility constraint.
- Exact UI styling of the two buttons, matching the existing bottom bar.
