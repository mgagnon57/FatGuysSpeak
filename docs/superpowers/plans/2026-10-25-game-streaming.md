# Stream Game vs. Share Screen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split streaming into a one-click, auto-detecting "Stream Game" (window capture + game-only sound, Windows 11) and a silent "Share Screen" (today's picker, keeps remote control).

**Architecture:** Pure detection/naming/label logic lives in FatGuysSpeak.Shared and is unit-tested. The server tracks each stream's kind and game name, broadcasts them via a new `StreamDetails` event (old `StreamStarted` unchanged for compatibility), and refuses remote control for game streams. The client gains a `GameCaptureService` (Windows.Graphics.Capture + per-process WASAPI loopback) beside the existing `ScreenStreamService` (which loses its audio), and the bottom bar gets two buttons.

**Tech Stack:** .NET 9, MAUI/WinUI (net9.0-windows10.0.19041.0), Windows.Graphics.Capture via CsWinRT, raw COM interop (d3d11.dll, mmdevapi.dll), NAudio 2.3 + Concentus Opus, ASP.NET Core SignalR, xUnit + Moq.

**Spec:** `docs/superpowers/specs/2026-10-25-game-streaming-design.md`

## Global Constraints

- Stream Game requires Windows 11: `Environment.OSVersion.Version.Build >= 22000`. On Windows 10 the button is disabled with tooltip/text exactly: `Stream Game needs Windows 11 — upgrade to use it. Share Screen still works.`
- Share Screen sends **no audio**. Stream Game sends only the game process tree's audio; never whole-system loopback; never mic or FatGuysSpeak voice. Game sound default **on**, Preference key `stream_game_sound`.
- The game name sent to the server/viewers comes only from `GameNames.Clean(processName)`, never a window title. Server additionally sanitizes: trim, strip control chars, max 40 chars, blank → `a game`.
- Remote control is only for Share Screen streams — enforced in ChatHub, not just hidden.
- Old v0.12.x clients calling `StartStream(int channelId)` must keep working (treated as Share Screen). SignalR hub methods can't be overloaded: add new methods, don't change `StartStream`'s signature.
- Frame transport unchanged: JPEG over `SendStreamFrame` path, `_busy` drop-frame, 4 MB cap, `UpdateQuality` adaptive settings apply to both capture services.
- No new NuGet packages unless COM interop proves impossible (spec: fewest dependencies).
- Hub tests go in `[Collection("HubTests")]`; every task ships tests (owner rule); XAML changes need a clean client build (`Remove-Item -Recurse -Force FatGuysSpeak.Client\bin,FatGuysSpeak.Client\obj`).

## Review Focus

1. **Game is minimized / true-fullscreen and not covering the monitor when the button is clicked** — still detected by library exe-name match and auto-picked. Test in Task 1 (`Detect_MinimizedLibraryGame_IsConfident`).
2. **Game switches borderless ↔ windowed (resolution change) mid-stream** — capture keeps going at the new size; no crash, no stretched/garbled frames. GameCaptureService must `Recreate` the frame pool on `ContentSize` change (Task 5 requirement + manual check in Task 8).
3. **Game produces no audio (menus, muted) or process loopback fails to activate** — video must still stream; audio failure is silent and non-fatal. Task 5 requirement + manual check.
4. **No Steam installed / malformed `libraryfolders.vdf` / huge libraries** — detection returns empty library data instead of throwing, and the scan never blocks the UI thread (runs once on a background task, cached). Test in Task 2 (`ParseLibraryPaths_Malformed_ReturnsEmpty`).
5. **A DM, browser tab, or other sensitive window title** is never what viewers see — names come from process names only and are sanitized server-side. Tests in Task 1 (`Clean_*`) and Task 3 (`StartGameStream_SanitizesGameName`).

---

## File Structure

| File | Responsibility |
|---|---|
| `FatGuysSpeak.Shared/DTOs.cs` (modify) | add `enum StreamKind { Screen, Game }` |
| `FatGuysSpeak.Shared/GameDetection.cs` (create) | `GameWindowFacts`, `GameCandidate`, `GameDetectionResult`, `GameDetector`, `GameNames` |
| `FatGuysSpeak.Shared/GameLibrary.cs` (create) | `SteamLibraryParser` (vdf → library paths), `GameLibraryRoots` (static well-known folder names) |
| `FatGuysSpeak.Shared/StreamText.cs` (create) | `StreamText.ViewerLabel`, `StreamText.SupportsGameStream`, `StreamText.SanitizeGameName` (shared by server + client) |
| `FatGuysSpeak.Server/Hubs/ChatHub.cs` (modify) | stream kind/game in `ActiveStreamers`, `StartGameStream`, `UpdateStreamGame`, `StreamDetails`, control refusal |
| `FatGuysSpeak.Client/Services/ChatHubService.cs` (modify) | new invokes + `StreamDetails` event |
| `FatGuysSpeak.Client/Services/JpegFrameEncoder.cs` (create) | resize-to-max-width + JPEG encode, extracted from ScreenStreamService |
| `FatGuysSpeak.Client/Services/StreamAudioEncoder.cs` (create) | 20 ms Opus pump over an `ISampleProvider`, extracted from ScreenStreamService |
| `FatGuysSpeak.Client/Services/ScreenStreamService.cs` (modify) | Share Screen only, audio removed, uses JpegFrameEncoder |
| `FatGuysSpeak.Client/Services/GameWindowScanner.cs` (create) | Win32 fact gathering + cached library scan → `GameWindowFacts` list |
| `FatGuysSpeak.Client/Services/GameCaptureService.cs` (create) | WGC window capture + process loopback audio |
| `FatGuysSpeak.Client/Services/Interop/D3D11Interop.cs`, `ProcessLoopbackInterop.cs` (create) | COM/P-Invoke declarations only |
| `FatGuysSpeak.Client/Models/AppWindow.cs`, `ViewModels/WindowPickerViewModel.cs`, `Pages/WindowPickerPage.xaml` (modify) | picker game mode |
| `FatGuysSpeak.Client/ViewModels/MainViewModel.cs`, `Pages/MainPage.xaml`, `Pages/StreamViewPage.xaml`, `MauiProgram.cs` (modify) | two buttons, sound switch, Change link, viewer label |
| `FatGuysSpeak.Tests/Client/GameDetectionTests.cs`, `GameLibraryTests.cs`, `StreamTextTests.cs` (create) | Shared logic tests |
| `FatGuysSpeak.Tests/Server/ChatHubGameStreamTests.cs` (create) | hub tests |

---

### Task 1: Game detection + naming (Shared)

**Files:**
- Create: `FatGuysSpeak.Shared/GameDetection.cs`
- Test: `FatGuysSpeak.Tests/Client/GameDetectionTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record GameWindowFacts(long Handle, int ProcessId, string Title, string ProcessName, string? ExePath, int Left, int Top, int Width, int Height, int MonitorLeft, int MonitorTop, int MonitorWidth, int MonitorHeight, bool IsMinimized, bool? UsesGraphicsRuntime);` (`ExePath`/`UsesGraphicsRuntime` null = unreadable)
  - `public sealed record GameCandidate(GameWindowFacts Window, int Score, string GameName);`
  - `public sealed record GameDetectionResult(IReadOnlyList<GameCandidate> Ranked, GameCandidate? ConfidentPick);`
  - `public static class GameDetector { GameDetectionResult Detect(IReadOnlyList<GameWindowFacts> windows, IReadOnlySet<string> libraryExeNames, IReadOnlyList<string> libraryRoots); }` — `libraryExeNames` are file names without extension, case-insensitive.
  - `public static class GameNames { string Clean(string processName); }`

- [ ] **Step 1: Write the failing tests**

Build a `Facts(...)` helper defaulting to a 2560×1440 monitor at (0,0). Wardogs fixture = process `WardogsClient-Win64-Shipping`, title `Wardogs  `, rect 0,0,2560,1440, ExePath null, UsesGraphicsRuntime null; library names contain `WardogsClient-Win64-Shipping`, `WardogsLauncher-Shipping`, `CrashReportClient`, `EpicWebHelper`.

```csharp
[Fact] Detect_Wardogs_IsConfidentPick()            // only Wardogs + an explorer window + a Chrome window
    => ConfidentPick!.Window.ProcessName == "WardogsClient-Win64-Shipping"; GameName == "Wardogs"
[Fact] Detect_MinimizedLibraryGame_IsConfident()   // IsMinimized=true, rect 0,0,160,28 → still ConfidentPick
[Fact] Detect_FullscreenBrowser_IsNeverCandidate() // msedge covering monitor → not in Ranked
[Fact] Detect_ExcludedHelpersInLibrary_AreIgnored() // CrashReportClient / EpicWebHelper windows → not in Ranked
[Fact] Detect_GameAndItsLauncher_PicksGame()       // Wardogs client (covers) + WardogsLauncher (small window) → pick = client
[Fact] Detect_TwoLibraryGamesBothFullscreen_NotConfident() // ConfidentPick null, Ranked.Count == 2
[Fact] Detect_UnknownFullscreenApp_NotConfidentButRanked() // covers monitor, not in library → Ranked has it, ConfidentPick null
[Fact] Detect_NothingGameLike_EmptyAndNull()
[Fact] Detect_ExePathUnderLibraryRoot_CountsAsLibrary() // name not in set, ExePath under a root → confident
[Theory] Clean_StripsEngineSuffixes:
    ("WardogsClient-Win64-Shipping","Wardogs"), ("FortniteClient-Win64-Shipping","Fortnite"),
    ("VALORANT-Win64-Shipping","VALORANT"), ("cs2","cs2"), ("RocketLeague.exe","RocketLeague"),
    ("","a game"), ("   ","a game")
[Fact] Clean_TruncatesTo40Chars()
```

- [ ] **Step 2: Run to verify failure** — `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~GameDetectionTests"` → build FAIL (types missing).

- [ ] **Step 3: Implement `GameDetection.cs`**

Scoring (decided values): covers its monitor (rect equals monitor bounds, ±2 px) **+40**; library match — `libraryExeNames` contains ProcessName, or ExePath starts with any root — **+50** (counted once); `UsesGraphicsRuntime == true` **+20**. Excluded process names (case-insensitive), never ranked: `FatGuysSpeak.Client, explorer, ShellExperienceHost, StartMenuExperienceHost, SearchHost, TextInputHost, ApplicationFrameHost, LockApp, chrome, msedge, firefox, opera, brave, vivaldi, steam, steamwebhelper, EpicGamesLauncher, EpicWebHelper, Battle.net, RiotClientServices, RiotClientUx, EADesktop, GalaxyClient, upc, Discord, obs64, obs32, devenv, Code, WindowsTerminal, powershell, cmd, Teams, ms-teams, Spotify, CrashReportClient, crashpad_handler, UnityCrashHandler64`. Game launchers (e.g. `WardogsLauncher-Shipping`) are not excluded; they score normally and lose to the covering game client. Candidates need Score ≥ 40. Ranked = score desc, then covering-monitor first. Confident = top Score ≥ 50 and (single candidate ≥ 50, or top − second ≥ 30).
`GameNames.Clean`: drop `.exe`; remove suffixes in order `-Win64-Shipping`, `-WinGDK-Shipping`, `-Win32-Shipping`, `-Shipping`; then a trailing `Client` (only if something remains); trim; blank → `a game`; cap 40.

- [ ] **Step 4: Run tests** → all PASS.
- [ ] **Step 5: Commit** — `feat(shared): game window detection and name cleaning`

---

### Task 2: Game library discovery (Shared parser + client scanner)

**Files:**
- Create: `FatGuysSpeak.Shared/GameLibrary.cs`, `FatGuysSpeak.Client/Services/GameWindowScanner.cs`
- Test: `FatGuysSpeak.Tests/Client/GameLibraryTests.cs`

**Interfaces:**
- Consumes: `GameWindowFacts` (Task 1)
- Produces:
  - `public static class SteamLibraryParser { IReadOnlyList<string> ParseLibraryPaths(string? vdfText); }` → each library's `steamapps\common` full path (unescape `\\` → `\`).
  - `public static class GameLibraryRoots { IReadOnlyList<string> RelativeFolders { get; } }` = `Epic Games`, `Program Files\Epic Games`, `XboxGames`, `Riot Games`, `Program Files (x86)\Battle.net`, `Program Files (x86)\GOG Galaxy\Games`, `Program Files\EA Games`, `Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games` (joined with each fixed drive root).
  - Client (Windows only): `public sealed class GameWindowScanner { Task<(IReadOnlySet<string> ExeNames, IReadOnlyList<string> Roots)> GetLibraryAsync(); IReadOnlyList<GameWindowFacts> GetWindowFacts(); }` registered as singleton in `MauiProgram.cs`.

- [ ] **Step 1: Failing tests** — `ParseLibraryPaths_RealFormat_ReturnsCommonFolders` (two `"path"` entries, one `C:\\Program Files (x86)\\Steam`, one `D:\\SteamLibrary` → `...\steamapps\common` each), `ParseLibraryPaths_Null_ReturnsEmpty`, `ParseLibraryPaths_Malformed_ReturnsEmpty` (`"{{{ not vdf"`), `RelativeFolders_IncludesEpicAndXbox`.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement parser** (regex on `"path"\s+"([^"]+)"`, never throws).
- [ ] **Step 4: Implement `GameWindowScanner`** — library: read `%ProgramFiles(x86)%\Steam\steamapps\libraryfolders.vdf` (also `HKCU\Software\Valve\Steam\SteamPath` if present), plus `GameLibraryRoots` on every `DriveInfo` with `DriveType.Fixed`; enumerate `*.exe` file names (no extension) with `EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true }`; run once via `Task.Run`, cache the task; start it from the constructor so it's warm by the first click. Window facts: reuse the visible-titled `EnumWindows` filter from `ScreenStreamService.GetOpenWindows`; `GetWindowRect`, `IsIconic`, `MonitorFromWindow(MONITOR_DEFAULTTONEAREST)` + `GetMonitorInfo`; process name via `Process.GetProcessById(pid).ProcessName`; ExePath via `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageName`, null on failure; `UsesGraphicsRuntime` = any of `d3d11.dll, d3d12.dll, dxgi.dll, vulkan-1.dll, opengl32.dll` in `Process.Modules`, null on any exception.
- [ ] **Step 5: Run tests + client build** — tests PASS; `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0` → 0 errors.
- [ ] **Step 6: Commit** — `feat: discover installed game libraries and window facts`

---

### Task 3: Server — stream kind, game name, control refusal

**Files:**
- Modify: `FatGuysSpeak.Shared/DTOs.cs` (add `public enum StreamKind { Screen, Game }`)
- Create: `FatGuysSpeak.Shared/StreamText.cs`
- Modify: `FatGuysSpeak.Server/Hubs/ChatHub.cs` (ActiveStreamers line 23, JoinChannel replay ~line 98, StartStream ~410, StopStream ~455, RequestControl/OfferControl/GrantControl/AcceptControl ~521-545)
- Test: `FatGuysSpeak.Tests/Client/StreamTextTests.cs`, `FatGuysSpeak.Tests/Server/ChatHubGameStreamTests.cs` (copy the mock-clients/`Hub(...)`/`ClearHubStaticState` scaffolding from `ChatHubRemoteControlTests.cs`)

**Interfaces:**
- Produces:
  - `StreamText.SanitizeGameName(string? name) -> string`; `StreamText.ViewerLabel(StreamKind kind, string streamer, string? game) -> string` = `"🎮 {streamer} is streaming {game}"` / `"🖥 {streamer} is sharing their screen"`; `StreamText.SupportsGameStream(int osBuild) -> bool` (`>= 22000`).
  - Hub: `Task StartGameStream(int channelId, string gameName)`, `Task UpdateStreamGame(string gameName)`; existing `StartStream(int)` = Screen. Client event `"StreamDetails"(int streamerId, int channelId, int kind, string? gameName)` sent to `server-{serverId}` after every `StreamStarted`, on `UpdateStreamGame`, and to `Caller` in the JoinChannel replay.
  - `ActiveStreamers` value becomes `(int ChannelId, int ServerId, string Username, StreamKind Kind, string? GameName)`.
  - Notifications: start game `"🎮 {user} started streaming {game} in #{channel}"`; start screen `"🖥 {user} started sharing their screen in #{channel}"`; switch `"🎮 {user} switched to {game}"`; stop game `"⏹ {user} stopped streaming {game} in #{channel}"`; stop screen keeps `"⏹ {user} ended screen share in #{channel}"`.

- [ ] **Step 1: Failing tests**
  - StreamText: `SanitizeGameName` → (`null`→`a game`), (`"  Wardogs \n"`→`Wardogs`), 60-char → length 40; `ViewerLabel` both kinds exact strings; `SupportsGameStream(19045)` false, `(22000)` true, `(26200)` true.
  - Hub: `StartGameStream_SendsStreamStartedAndDetailsWithGame` (args `[streamerId, channelId, (int)StreamKind.Game, "Wardogs"]`), `StartStream_Legacy_IsScreenKind` (details kind 0, game null), `StartGameStream_SanitizesGameName` (`"x\u0007" + new string('y',80)` → no control chars, length ≤ 40), `StartGameStream_NotificationNamesGameAndChannel`, `JoinChannel_ReplaysStreamDetailsToCaller`, `UpdateStreamGame_UpdatesDetailsAndNotifies`, `UpdateStreamGame_WhenNotStreaming_DoesNothing`, `RequestControl_OnGameStream_IsIgnored`, `GrantControl_OnGameStream_DoesNotOpenSession` (no `ControlGranted`), `GrantControl_OnScreenStream_StillWorks`.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** — private `BeginStreamAsync(int channelId, StreamKind kind, string? gameName)` holds the current `StartStream` body; `StartStream(c)` → `BeginStreamAsync(c, Screen, null)`; `StartGameStream(c, n)` → `BeginStreamAsync(c, Game, StreamText.SanitizeGameName(n))`. The four control methods return early when the relevant streamer's `Kind == StreamKind.Game`.
- [ ] **Step 4: Run** `dotnet test FatGuysSpeak.Tests` → all PASS (existing stream/control tests unchanged).
- [ ] **Step 5: Commit** — `feat(server): game vs screen streams, game name, no remote control for games`

---

### Task 4: Client plumbing — hub calls, shared encoders, silent Share Screen

**Files:**
- Modify: `FatGuysSpeak.Client/Services/ChatHubService.cs` (events ~line 29, handlers ~104, invokes ~193)
- Create: `FatGuysSpeak.Client/Services/JpegFrameEncoder.cs`, `FatGuysSpeak.Client/Services/StreamAudioEncoder.cs`
- Modify: `FatGuysSpeak.Client/Services/ScreenStreamService.cs`

**Interfaces:**
- Produces:
  - `ChatHubService`: `Task StartGameStreamAsync(int channelId, string gameName)`, `Task UpdateStreamGameAsync(string gameName)`, `event Action<int, int, StreamKind, string?>? StreamDetails` (streamerId, channelId, kind, gameName).
  - `internal static class JpegFrameEncoder { byte[] Encode(System.Drawing.Bitmap raw, int maxWidth, int jpegQuality); }` (owns resize + dispose of `raw`; logic moved verbatim from `CaptureFrameAsJpeg`).
  - `internal sealed class StreamAudioEncoder : IDisposable { StreamAudioEncoder(ISampleProvider mono48k, Action<byte[]> onPacket); void Start(); void Stop(); }` (the 20 ms timer + Opus code moved from ScreenStreamService).
  - `ScreenStreamService`: same public surface **minus audio** — `StreamAudioCaptured` event removed, no loopback.
- [ ] **Step 1: Move code, delete loopback from ScreenStreamService** (non-Windows stub updated to match).
- [ ] **Step 2: Build** client → 0 errors; `dotnet test FatGuysSpeak.Tests` → PASS (XamlCommandBindingTests still green).
- [ ] **Step 3: Commit** — `refactor(client): extract frame/audio encoders; Share Screen sends no audio`

---

### Task 5: GameCaptureService — window capture + game-only sound

**Files:**
- Create: `FatGuysSpeak.Client/Services/GameCaptureService.cs`, `FatGuysSpeak.Client/Services/Interop/D3D11Interop.cs`, `FatGuysSpeak.Client/Services/Interop/ProcessLoopbackInterop.cs`
- Modify: `FatGuysSpeak.Client/MauiProgram.cs` (singleton)

**Interfaces:**
- Consumes: `JpegFrameEncoder`, `StreamAudioEncoder` (Task 4)
- Produces (Windows; non-Windows stub with same members):
  ```csharp
  public sealed class GameCaptureService : IDisposable {
      event Action<byte[]>? FrameCaptured; event Action<byte[]>? StreamAudioCaptured;
      event Action? TargetClosed;                 // game window closed
      bool IsCapturing { get; }
      void Start(IntPtr hwnd, int processId, bool gameSound, int fps = 20);
      void SwitchTarget(IntPtr hwnd, int processId); // keeps stream, swaps window + audio source
      void SetGameSound(bool on);
      void UpdateQuality(int fps, int jpegQuality, int maxWidth);
      void Stop();
  }
  ```
- Decided approach:
  - D3D device: `D3D11CreateDevice` (BGRA support flag) → QI `IDXGIDevice` → `CreateDirect3D11DeviceFromDXGIDevice` → `WinRT.MarshalInterface<IDirect3DDevice>.FromAbi`.
  - Item: `IGraphicsCaptureItemInterop` (IID `3628E81B-3CAC-4C60-B7F4-23CE0E0C3356`) `CreateForWindow(hwnd, IID_IGraphicsCaptureItem)` via `GraphicsCaptureItem` activation factory.
  - `Direct3D11CaptureFramePool.CreateFreeThreaded(device, B8G8R8A8UIntNormalized, 2, size)`; session `IsCursorCaptureEnabled = false`; border off via QI to `IGraphicsCaptureSession3` (IID `F2CDD966-22AE-5EA1-9596-3A289344C3BE`, `put_IsBorderRequired(false)`) — ignore failure (keeps border). No TFM bump.
  - FrameArrived: throttle to `fps` (skip frames whose timestamp is < 1000/fps ms after the last emitted) and honor the `_busy` drop; convert with `SoftwareBitmap.CreateCopyFromSurfaceAsync` → copy BGRA bytes into a `System.Drawing.Bitmap` → `JpegFrameEncoder.Encode`. On `ContentSize` change, `framePool.Recreate(...)` (Review Focus 2).
  - `GraphicsCaptureItem.Closed` → `Stop()` then `TargetClosed`.
  - Audio: `ActivateAudioInterfaceAsync("VAD\\Process_Loopback", IID_IAudioClient, PROPVARIANT(VT_BLOB → AUDIOCLIENT_ACTIVATION_PARAMS{ ProcessLoopback, TargetProcessId = pid, PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE }))`, completion handler implementing `IActivateAudioInterfaceCompletionHandler` + `IAgileObject`; `IAudioClient.Initialize(SHARED, LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM, …, WAVEFORMATEX 48000 Hz / 16-bit / 2 ch)`; read `IAudioCaptureClient` packets on a dedicated thread into a `BufferedWaveProvider` → `StereoToMonoSampleProvider` → `StreamAudioEncoder`. Any activation/initialize failure: log once, continue video-only (Review Focus 3). `SetGameSound(false)` stops audio only.
- [ ] **Step 1: Implement interop + service.**
- [ ] **Step 2: Build** client → 0 errors.
- [ ] **Step 3: Live smoke test with Wardogs running** — add nothing to the product; use a throwaway console project in the scratchpad referencing the client's interop files, or a temporary `#if DEBUG` hook, to start capture on the `WardogsClient-Win64-Shipping` window for 5 s and write `frame-1.jpg` plus a count of audio packets. Expected: JPEG shows the game (not black), ≥ 50 frames, audio packets > 0 while the game makes sound. Remove the hook afterwards.
- [ ] **Step 4: Commit** — `feat(client): Windows.Graphics.Capture game capture with game-only audio`

---

### Task 6: Picker game mode

**Files:**
- Modify: `FatGuysSpeak.Client/Models/AppWindow.cs`, `FatGuysSpeak.Client/ViewModels/WindowPickerViewModel.cs`, `FatGuysSpeak.Client/Pages/WindowPickerPage.xaml`

**Interfaces:**
- Consumes: `GameDetectionResult` (Task 1)
- Produces: `AppWindow` gains `int ProcessId = 0, bool IsLikelyGame = false, string? GameName = null` (optional trailing record params so existing `new AppWindow(h, t, p)` calls compile); `WindowPickerViewModel.LoadForGame(GameDetectionResult detection, IReadOnlyList<GameWindowFacts> all)` — likely games first with `🎮` icon and subtitle "Likely game", then other windows; **no "Entire Screen" row**; header text "Choose your game". Existing `Load()` (Share Screen) unchanged.
- [ ] **Step 1: Implement**; `Icon` returns `🎮` when `IsLikelyGame`.
- [ ] **Step 2: Clean client build** → 0 errors; tests PASS.
- [ ] **Step 3: Commit** — `feat(client): game-first picker for Stream Game`

---

### Task 7: Main window — two buttons, sound switch, Change link, viewer labels

**Files:**
- Modify: `FatGuysSpeak.Client/ViewModels/MainViewModel.cs` (ToggleStreamAsync ~3137, OnStreamStarted ~3269, `CanRequestControl` line 161, `StreamButtonLabel` line 262, event wiring ~659/~2240), `FatGuysSpeak.Client/Pages/MainPage.xaml` (bottom bar button ~1583, viewer label ~815), `FatGuysSpeak.Client/Pages/StreamViewPage.xaml` (label line 21)

**Interfaces:**
- Consumes: everything above.
- Produces (MainViewModel): `[RelayCommand] StreamGameAsync()` (toggle), `[RelayCommand] ShareScreenAsync()` (toggle, today's flow with `hub.StartStreamAsync`), `[RelayCommand] ChangeGameAsync()`; `[ObservableProperty] StreamKind? _currentStreamKind`, `string? _streamingGameName`, `bool _gameSoundEnabled` (init `Preferences.Get("stream_game_sound", true)`, persisted on change and pushed to `gameCapture.SetGameSound`), `StreamKind _activeStreamKind`, `string? _activeStreamGameName`; computed `StreamGameButtonLabel` (`"🎮 Stream Game"` / `"⏹ Stop Streaming"`), `ShareScreenButtonLabel` (`"🖥 Share Screen"` / `"⏹ Stop Sharing"`), `CanStreamGame` (Win11 via `StreamText.SupportsGameStream` && CurrentStreamKind != Screen), `CanShareScreen` (CurrentStreamKind != Game), `StreamGameTooltip` (Win10 copy from Global Constraints, else "Stream the game you're playing"), `StreamingGameText` (`$"Streaming {StreamingGameName} · Change"`), `ActiveStreamLabel` (`StreamText.ViewerLabel(...)`); `CanRequestControl` adds `&& ActiveStreamKind == StreamKind.Screen`. Remove `ToggleStreamCommand`/`StreamButtonLabel`.
- StreamGameAsync flow: guard server/connection like today → `scanner.GetLibraryAsync()` + `GetWindowFacts()` (off UI thread) → `GameDetector.Detect` → ConfidentPick or picker (`LoadForGame`; cancel = nothing) → `gameCapture.Start(hwnd, pid, GameSoundEnabled)` → `hub.StartGameStreamAsync(channelId, GameNames.Clean(processName))`. `TargetClosed` → stop stream (`hub.StopStreamAsync`) + `toast.Show("Stream ended", $"{game} closed — your stream has stopped.")`. ChangeGameAsync → picker → `SwitchTarget` + `hub.UpdateStreamGameAsync`. Viewer: `StreamDetails` sets `ActiveStreamKind/ActiveStreamGameName` for the matching streamer.
- XAML: bottom bar gets two buttons + a compact `🔊 Game sound` switch (visible while `CanStreamGame`) and the Change link label (visible while CurrentStreamKind == Game) bound to `ChangeGameCommand`; viewer labels bind `ActiveStreamLabel` (MainPage keep suffix " — stream in separate window").
- [ ] **Step 1: Implement VM + XAML.**
- [ ] **Step 2: Clean client build** → 0 errors; `dotnet test FatGuysSpeak.Tests` → PASS (XamlCommandBindingTests proves `StreamGameCommand`, `ShareScreenCommand`, `ChangeGameCommand` exist).
- [ ] **Step 3: Commit** — `feat(client): Stream Game and Share Screen buttons`

---

### Task 8: End-to-end verification + docs

**Files:**
- Modify: `CHANGELOG.md` ([Unreleased] → Added/Changed), `CLAUDE.md` (Voice + Media Pipeline: GameCaptureService/WGC, process loopback, Share Screen silent)

- [ ] **Step 1: Run everything** — `dotnet test FatGuysSpeak.Tests` all PASS; server builds both TFMs; clean client build 0 errors.
- [ ] **Step 2: Live check with the owner** (local server + 2 clients, Wardogs running): owner clicks 🎮 Stream Game in client A → no picker, viewer client B shows "🎮 BillyBong is streaming Wardogs" and non-black game frames (screenshot B), game sound audible on B, FatGuysSpeak voice not echoed; switch Wardogs between borderless/windowed → B keeps updating; toggle Game sound off/on; click Change → picker shows Wardogs first; close Wardogs → stream ends with toast; 🖥 Share Screen → B shows "sharing their screen", no audio, Request Control available; during a game stream Request Control is hidden on B.
- [ ] **Step 3: Commit** — `docs: Stream Game in changelog and CLAUDE.md`
