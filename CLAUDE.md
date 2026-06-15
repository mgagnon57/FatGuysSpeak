# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

**Build server (Windows, with WPF dashboard):**
```
dotnet build FatGuysSpeak.Server --framework net9.0-windows10.0.19041.0
```

**Build server (headless / Railway):**
```
dotnet build FatGuysSpeak.Server --framework net9.0
```

**Build client (Windows only):**
```
dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0
```

**Run all tests:**
```
dotnet test FatGuysSpeak.Tests
```

**Run a single test class:**
```
dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~AuthControllerTests"
```

**Run a single test method:**
```
dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~AuthControllerTests.Login_Success_ReturnsToken"
```

**Launch server + 2 clients (pre-built binaries):**
```
.\launch.ps1
```
The server binary must be in `FatGuysSpeak.Server\bin\Debug\net9.0\` (not net9.0-windows). Always launch two client instances when manually testing.

**Server runs at:** `http://localhost:5238`

## Architecture

Four projects in one solution:

- **FatGuysSpeak.Server** — ASP.NET Core 9 Web API + SignalR. Targets both `net9.0` (Railway/Linux) and `net9.0-windows10.0.19041.0` (local dev with WPF dashboard window). On Windows the dashboard opens as a WPF window; closing it stops the server.
- **FatGuysSpeak.Client** — .NET MAUI app targeting Windows (primary), Android, iOS, macCatalyst. All features are Windows-only except basic chat.
- **FatGuysSpeak.Shared** — DTOs, enums, and pure-logic helpers referenced by both Server and Client. Client-only logic that must be testable (e.g. `VideoUrlHelper`) lives here instead of in Client.
- **FatGuysSpeak.Tests** — xUnit tests against Server + Shared only (no MAUI dependency). Uses in-memory SQLite via `TestDb` helper.

### Database

SQLite in local dev (`fatguys.db` in the Server directory), PostgreSQL on Railway (detected via `DATABASE_URL` env var). EF Core `EnsureCreated()` handles schema creation; new columns are added via raw SQL `ALTER TABLE` checks at startup (no EF migrations). All new tables must be added to both the SQLite and PostgreSQL branches in `Program.cs`.

Single seeded server named "FatGuysSpeak" (OwnerId=0). All users auto-join on login. Channels: `lobby`, `angry-fat-guys`. All channels are `ChannelType.Text` but support voice too.

### SignalR Hub (`ChatHub`)

The hub (`/hubs/chat`) holds all live state in static `ConcurrentDictionary` fields — no distributed cache. JWT is passed via query string (`?access_token=`) for WebSocket handshakes. Key dictionaries:

- `OnlineUsers`: userId → username
- `VoiceChannelMap`: userId → channelId (voice)
- `UserTextChannelMap`: userId → channelId (text)
- `ChannelOccupants`: channelId → (userId → username)
- `ActiveStreamers`: userId → (channelId, serverId, username)
- `ActiveCameras`: userId → (channelId, username)

SignalR groups follow naming conventions: `channel-{id}`, `voice-{id}`, `stream-{id}`.

Because these dictionaries are static, hub tests are in the `[Collection("HubTests")]` collection (defined in `HubTestCollection.cs`) which disables parallelization to prevent state bleed between tests.

### Voice + Media Pipeline

Voice: NAudio WaveIn (48kHz/16-bit/mono) → Opus encode (Concentus) → `SendVoiceData` hub method (max 8 KB) → server relays to `voice-{channelId}` group → `ReceiveVoiceData` → Opus decode → NAudio playback. PTT via global keyboard hook (`PttService`). Whisper STT auto-posts transcriptions as messages.

Screen share and webcam: MJPEG frames over SignalR. `ScreenStreamService` uses GDI `CopyFromScreen`; `CameraService` uses WinRT `MediaCapture`. Both use an Interlocked `_busy` flag to drop frames when the previous send is still in flight. `MaximumReceiveMessageSize` is set to 4 MB to handle full-res JPEG frames.

### Client Architecture

MAUI MVVM with `CommunityToolkit.Mvvm`. All real-time events flow through `ChatHubService` (event-based wrapper around `HubConnection`) and REST calls through `ApiService`. `MainViewModel` is the central hub connecting both services to the UI. `ApiService.CurrentUserId/CurrentUsername/CurrentAvatarUrl` are set on login and cleared on sign-out.

The server URL defaults to `http://localhost:5238` and is persisted in MAUI `Preferences` (configurable in settings).

### Security

- JWT key must be set in `appsettings.Development.json` (empty string in base config throws at startup)
- Rate limiting: auth endpoints 10 req/min per IP (fixed window); messages 30 req/min per user (sliding window)
- CORS allows only localhost/127.0.0.1
- SSRF prevention in `PreviewController`: DNS + RFC1918 check before fetching link previews
- BCrypt dummy-hash on failed login to prevent timing attacks
- Channel permissions stored in `ChannelPermissions` table; enforced in `JoinChannel` hub method

### Testing Patterns

Tests instantiate controllers directly with a `TestDb` (in-memory SQLite) and `TestHelpers.SetUser()` to inject a claims principal. `TestHelpers.SeedServerAsync()` creates a server + two channels + one admin member. Hub tests use a mock `IHubContext<ChatHub>` from `TestHelpers.MockHub()`. The `BotService` is silenced with `TestHelpers.NullBot()` (empty API key causes it to no-op).
