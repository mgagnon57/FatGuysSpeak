# Remote Control During Screen Share Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a viewer of a live screen share drive the sharer's mouse + keyboard (any window or full-desktop share), with the sharer always consenting, no input clamping, and instant revoke.

**Architecture:** A reverse input channel over the existing `stream-{channelId}` SignalR pipeline. The controller serializes pointer/key events as `RemoteInputDto` with normalized (0–1) coordinates; the server relays them only from the active controller to the sharer (`SendRemoteInput` is the single authorization gate, backed by a one-controller-per-stream session dictionary); the sharer maps coordinates to its live capture rect and injects via Win32 `SendInput`.

**Tech Stack:** ASP.NET Core 9 SignalR (`ChatHub`, static `ConcurrentDictionary` state), .NET MAUI Windows client, `FatGuysSpeak.Shared` DTOs + pure helpers, xUnit + Moq hub tests, Win32 `SendInput`.

**Spec:** `docs/superpowers/specs/2026-06-17-remote-control-during-screen-share-design.md`

**Conventions for every task:**
- Server build (headless): `dotnet build FatGuysSpeak.Server --framework net9.0`
- Run the new hub tests: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~ChatHubRemoteControlTests"`
- Run the mapper tests: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~RemoteInputMapperTests"`
- Client build (Windows): `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
- A live server may lock the net9.0-windows server build; `dotnet test`/headless build use net9.0 and are unaffected. Stop running instances before a Windows client build only if the build reports a file lock.
- Commit messages end with: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`

---

## PHASE A — Shared DTO, coordinate mapper, server (fully test-driven)

### Task 1: RemoteInputDto + RemoteInputMapper (pure logic)

**Files:**
- Modify: `FatGuysSpeak.Shared/DTOs.cs`
- Create: `FatGuysSpeak.Shared/RemoteInputMapper.cs`
- Create: `FatGuysSpeak.Tests/Server/RemoteInputMapperTests.cs`

- [ ] **Step 1: Add the DTO + enum**

In `FatGuysSpeak.Shared/DTOs.cs`, add near the other DTOs:

```csharp
public enum RemoteInputKind { Move, Down, Up, Wheel, KeyDown, KeyUp }

public record RemoteInputDto(
    RemoteInputKind Kind,
    double X = 0, double Y = 0,   // normalized 0..1 (Move/Down/Up/Wheel)
    int Button = 0,               // 0=left, 1=right, 2=middle (Down/Up)
    int Delta = 0,                // wheel notches * 120 (Wheel)
    int KeyCode = 0);             // Win32 virtual-key code (KeyDown/KeyUp)
```

- [ ] **Step 2: Write the failing test**

Create `FatGuysSpeak.Tests/Server/RemoteInputMapperTests.cs`:

```csharp
using FatGuysSpeak.Shared;
using Xunit;

namespace FatGuysSpeak.Tests.Server;

public class RemoteInputMapperTests
{
    [Fact]
    public void Center_OfFullDesktop_MapsToScreenCenter()
    {
        var (x, y) = RemoteInputMapper.ToScreenPixel(0.5, 0.5, 0, 0, 1920, 1080);
        Assert.Equal(960, x);
        Assert.Equal(540, y);
    }

    [Fact]
    public void Origin_OfWindowRect_MapsToWindowTopLeft()
    {
        var (x, y) = RemoteInputMapper.ToScreenPixel(0, 0, 100, 200, 800, 600);
        Assert.Equal(100, x);
        Assert.Equal(200, y);
    }

    [Fact]
    public void FarCorner_OfWindowRect_MapsToWindowBottomRight()
    {
        var (x, y) = RemoteInputMapper.ToScreenPixel(1, 1, 100, 200, 800, 600);
        Assert.Equal(900, x);   // 100 + 800
        Assert.Equal(800, y);   // 200 + 600
    }

    [Fact]
    public void OutOfRange_IsClampedToRect()
    {
        var (x, y) = RemoteInputMapper.ToScreenPixel(1.5, -0.3, 0, 0, 1000, 1000);
        Assert.Equal(1000, x);
        Assert.Equal(0, y);
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~RemoteInputMapperTests"`
Expected: FAIL — `RemoteInputMapper` does not exist.

- [ ] **Step 4: Implement the mapper**

Create `FatGuysSpeak.Shared/RemoteInputMapper.cs`:

```csharp
namespace FatGuysSpeak.Shared;

/// <summary>
/// Maps a controller's normalized (0..1) click position within the streamed frame
/// to an absolute screen pixel inside the sharer's capture rectangle. Pure logic so
/// it is unit-testable without Win32 (the SendInput conversion lives in the client).
/// </summary>
public static class RemoteInputMapper
{
    public static (int X, int Y) ToScreenPixel(
        double normX, double normY, int rectLeft, int rectTop, int rectWidth, int rectHeight)
    {
        var nx = Math.Clamp(normX, 0.0, 1.0);
        var ny = Math.Clamp(normY, 0.0, 1.0);
        var x = rectLeft + (int)Math.Round(nx * rectWidth);
        var y = rectTop + (int)Math.Round(ny * rectHeight);
        return (x, y);
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~RemoteInputMapperTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add FatGuysSpeak.Shared/DTOs.cs FatGuysSpeak.Shared/RemoteInputMapper.cs FatGuysSpeak.Tests/Server/RemoteInputMapperTests.cs
git commit -m "$(cat <<'EOF'
Remote control: RemoteInputDto + normalized->screen coordinate mapper

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: ChatHub remote-control session + methods + authorization gate

**Files:**
- Modify: `FatGuysSpeak.Server/Hubs/ChatHub.cs`
- Create: `FatGuysSpeak.Tests/Server/ChatHubRemoteControlTests.cs`

**Context:** `ChatHub` already has `ActiveStreamers: userId → (ChannelId, ServerId, Username)`, derives the caller via the private `UserId`/`Username` properties, and sends to individual users with `Clients.User(id.ToString()).SendAsync(...)`. Add a new session dictionary and the control methods. The existing hub-test harness (`ChatHubStreamTests`) does NOT set up `Clients.User`; the new test file adds that.

- [ ] **Step 1: Add the session state + private helpers**

In `ChatHub.cs`, next to the other static dictionaries (near `ActiveStreamers` ~line 23), add:

```csharp
    // streamerUserId → (controllerUserId, channelId). At most one controller per stream.
    private static readonly ConcurrentDictionary<int, (int ControllerId, int ChannelId)> RemoteControlSessions = new();
```

In the Screen Sharing region (after `StopWatching`, ~line 406), add these helpers + methods:

```csharp
    // ─── Remote Control ─────────────────────────────────────────────────────────

    private async Task OpenControlSessionAsync(int streamerId, int controllerId, int channelId)
    {
        RemoteControlSessions[streamerId] = (controllerId, channelId);
        var controllerName = OnlineUsers.TryGetValue(controllerId, out var cn) ? cn : "";
        var streamerName   = OnlineUsers.TryGetValue(streamerId,   out var sn) ? sn : "";
        await Clients.User(streamerId.ToString()).SendAsync("ControlActive", controllerId, controllerName);
        await Clients.User(controllerId.ToString()).SendAsync("ControlGranted", streamerId, streamerName);
    }

    private async Task CloseControlSessionAsync(int streamerId)
    {
        if (!RemoteControlSessions.TryRemove(streamerId, out var s)) return;
        await Clients.User(streamerId.ToString()).SendAsync("ControlEnded", s.ControllerId);
        await Clients.User(s.ControllerId.ToString()).SendAsync("ControlEnded", streamerId);
    }

    public async Task RequestControl(int streamerId)
    {
        if (!ActiveStreamers.ContainsKey(streamerId)) return;       // no live stream to control
        await Clients.User(streamerId.ToString()).SendAsync("ControlRequested", UserId, Username);
    }

    public async Task OfferControl(int viewerId)
    {
        if (!ActiveStreamers.ContainsKey(UserId)) return;           // caller isn't streaming
        await Clients.User(viewerId.ToString()).SendAsync("ControlOffered", UserId, Username);
    }

    public async Task GrantControl(int controllerId)               // caller is the streamer
    {
        if (!ActiveStreamers.TryGetValue(UserId, out var info)) return;
        if (RemoteControlSessions.ContainsKey(UserId))
        {
            await Clients.Caller.SendAsync("ControlBusy");
            return;
        }
        await OpenControlSessionAsync(UserId, controllerId, info.ChannelId);
    }

    public async Task AcceptControl(int streamerId)                // caller is the viewer accepting an offer
    {
        if (!ActiveStreamers.TryGetValue(streamerId, out var info)) return;
        if (RemoteControlSessions.ContainsKey(streamerId))
        {
            await Clients.Caller.SendAsync("ControlBusy");
            return;
        }
        await OpenControlSessionAsync(streamerId, UserId, info.ChannelId);
    }

    public Task DenyControl(int otherUserId) =>                    // decline a request or offer
        Clients.User(otherUserId.ToString()).SendAsync("ControlDeclined", UserId);

    public Task StopControl() => CloseControlSessionAsync(UserId); // caller is the streamer

    public async Task ReleaseControl()                             // caller is the controller
    {
        var entry = RemoteControlSessions.FirstOrDefault(kv => kv.Value.ControllerId == UserId);
        if (entry.Value.ControllerId == UserId && RemoteControlSessions.ContainsKey(entry.Key))
            await CloseControlSessionAsync(entry.Key);
    }

    public async Task SendRemoteInput(FatGuysSpeak.Shared.RemoteInputDto dto)
    {
        // Authorization gate: only the active controller of a live session may send,
        // and input is relayed only to that session's streamer.
        var entry = RemoteControlSessions.FirstOrDefault(kv => kv.Value.ControllerId == UserId);
        if (entry.Value.ControllerId != UserId) return;            // not a controller → drop
        await Clients.User(entry.Key.ToString()).SendAsync("ReceiveRemoteInput", dto);
    }
```

Note: `FirstOrDefault` on the dictionary returns `default((int,(int,int)))` (key 0, controller 0) when not found; the `entry.Value.ControllerId != UserId` check is the guard. A real controller's `UserId` is never 0.

- [ ] **Step 2: Write the failing tests**

Create `FatGuysSpeak.Tests/Server/ChatHubRemoteControlTests.cs`:

```csharp
using System.Reflection;
using System.Security.Claims;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace FatGuysSpeak.Tests.Server;

[Collection("HubTests")]
public class ChatHubRemoteControlTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly Mock<IHubCallerClients> _mockClients;
    private readonly Mock<IGroupManager> _mockGroups;
    private readonly Dictionary<string, List<(string Method, object[] Args)>> _sent = new();

    public ChatHubRemoteControlTests()
    {
        _testDb = new TestDb();
        _mockClients = new Mock<IHubCallerClients>();
        _mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns<string>(g => Proxy($"group:{g}"));
        _mockClients.Setup(c => c.OthersInGroup(It.IsAny<string>())).Returns<string>(g => Proxy($"others:{g}"));
        _mockClients.Setup(c => c.Caller).Returns(Single("caller"));
        _mockClients.Setup(c => c.User(It.IsAny<string>())).Returns<string>(u => Single($"user:{u}"));

        _mockGroups = new Mock<IGroupManager>();
        _mockGroups.Setup(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockGroups.Setup(g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    public void Dispose() { ClearHubStaticState(); _testDb.Dispose(); }

    private IClientProxy Proxy(string target)
    {
        var p = new Mock<IClientProxy>();
        p.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((m, a, _) => Track(target, m, a)).Returns(Task.CompletedTask);
        return p.Object;
    }
    private ISingleClientProxy Single(string target)
    {
        var p = new Mock<ISingleClientProxy>();
        p.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((m, a, _) => Track(target, m, a)).Returns(Task.CompletedTask);
        return p.Object;
    }
    private void Track(string t, string m, object[] a) { if (!_sent.TryGetValue(t, out var l)) _sent[t] = l = []; l.Add((m, a)); }
    private bool Sent(string target, string method) => _sent.TryGetValue(target, out var l) && l.Any(x => x.Method == method);

    private ChatHub Hub(int userId, string username, string conn = "conn")
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Name, username) };
        var ctx = new Mock<HubCallerContext>();
        ctx.Setup(c => c.ConnectionId).Returns(conn);
        ctx.Setup(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
        return new ChatHub(_testDb.Db, new FatGuysSpeak.Server.Services.OnlineTimeTracker())
        { Context = ctx.Object, Clients = _mockClients.Object, Groups = _mockGroups.Object };
    }

    private static void ClearHubStaticState()
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        foreach (var name in new[] { "ActiveStreamers", "RemoteControlSessions", "OnlineUsers", "VoiceChannelMap", "UserTextChannelMap", "ChannelOccupants" })
            (typeof(ChatHub).GetField(name, flags)?.GetValue(null) as System.Collections.IDictionary)?.Clear();
    }

    // Seeds a server, makes `streamer` start a stream, returns (server, streamer, viewer).
    private async Task<(GuildServer server, User streamer, User viewer)> SeedStreamAsync(string prefix)
    {
        var (server, streamer) = await TestHelpers.SeedServerAsync(_testDb.Db, prefix);
        var viewer = new User { Username = prefix + "-viewer", Email = prefix + "-v@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(viewer);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = viewer.Id, Role = ServerRole.Member });
        await _testDb.Db.SaveChangesAsync();
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");
        // Register both in OnlineUsers (names) + start the stream.
        await Hub(viewer.Id, viewer.Username, "conn-v").OnConnectedAsync();
        await Hub(streamer.Id, streamer.Username, "conn-s").StartStream(channel.Id);
        return (server, streamer, viewer);
    }

    [Fact]
    public async Task GrantControl_OpensSession_NotifiesBothParties()
    {
        var (_, streamer, viewer) = await SeedStreamAsync("grant");
        _sent.Clear();
        await Hub(streamer.Id, streamer.Username, "conn-s").GrantControl(viewer.Id);

        Assert.True(Sent($"user:{viewer.Id}", "ControlGranted"), "controller should be told control was granted");
        Assert.True(Sent($"user:{streamer.Id}", "ControlActive"), "streamer should be told control is now active");
    }

    [Fact]
    public async Task SendRemoteInput_FromActiveController_RelaysToStreamerOnly()
    {
        var (_, streamer, viewer) = await SeedStreamAsync("relay");
        await Hub(streamer.Id, streamer.Username, "conn-s").GrantControl(viewer.Id);
        _sent.Clear();

        await Hub(viewer.Id, viewer.Username, "conn-v")
            .SendRemoteInput(new RemoteInputDto(RemoteInputKind.Move, 0.5, 0.5));

        Assert.True(Sent($"user:{streamer.Id}", "ReceiveRemoteInput"), "input must reach the streamer");
    }

    [Fact]
    public async Task SendRemoteInput_FromNonController_IsDropped()
    {
        var (_, streamer, viewer) = await SeedStreamAsync("drop");
        // No grant issued — viewer is not a controller.
        _sent.Clear();

        await Hub(viewer.Id, viewer.Username, "conn-v")
            .SendRemoteInput(new RemoteInputDto(RemoteInputKind.Move, 0.5, 0.5));

        Assert.False(_sent.Any(kv => kv.Value.Any(m => m.Method == "ReceiveRemoteInput")),
            "input from a non-controller must be dropped");
    }

    [Fact]
    public async Task GrantControl_WhenAlreadyControlled_ReturnsBusy_AndKeepsController()
    {
        var (server, streamer, viewer) = await SeedStreamAsync("busy");
        var viewer2 = new User { Username = "busy-v2", Email = "busy-v2@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(viewer2);
        await _testDb.Db.SaveChangesAsync();

        await Hub(streamer.Id, streamer.Username, "conn-s").GrantControl(viewer.Id);
        _sent.Clear();
        await Hub(streamer.Id, streamer.Username, "conn-s").GrantControl(viewer2.Id);

        Assert.True(Sent("caller", "ControlBusy"), "second grant while busy must report ControlBusy");
        Assert.False(Sent($"user:{viewer2.Id}", "ControlGranted"), "second viewer must not be granted control");
    }

    [Fact]
    public async Task OfferThenAccept_OpensSession()
    {
        var (_, streamer, viewer) = await SeedStreamAsync("offer");
        await Hub(streamer.Id, streamer.Username, "conn-s").OfferControl(viewer.Id);
        _sent.Clear();
        await Hub(viewer.Id, viewer.Username, "conn-v").AcceptControl(streamer.Id);

        Assert.True(Sent($"user:{viewer.Id}", "ControlGranted"));
        Assert.True(Sent($"user:{streamer.Id}", "ControlActive"));
    }

    [Fact]
    public async Task StopControl_EndsSession_NotifiesController()
    {
        var (_, streamer, viewer) = await SeedStreamAsync("stop");
        await Hub(streamer.Id, streamer.Username, "conn-s").GrantControl(viewer.Id);
        _sent.Clear();
        await Hub(streamer.Id, streamer.Username, "conn-s").StopControl();

        Assert.True(Sent($"user:{viewer.Id}", "ControlEnded"), "controller must be told control ended");
        // session cleared: subsequent input is dropped
        _sent.Clear();
        await Hub(viewer.Id, viewer.Username, "conn-v").SendRemoteInput(new RemoteInputDto(RemoteInputKind.Move));
        Assert.False(_sent.Any(kv => kv.Value.Any(m => m.Method == "ReceiveRemoteInput")));
    }

    [Fact]
    public async Task ReleaseControl_ByController_EndsSession()
    {
        var (_, streamer, viewer) = await SeedStreamAsync("release");
        await Hub(streamer.Id, streamer.Username, "conn-s").GrantControl(viewer.Id);
        _sent.Clear();
        await Hub(viewer.Id, viewer.Username, "conn-v").ReleaseControl();

        Assert.True(Sent($"user:{streamer.Id}", "ControlEnded"), "streamer must be told control ended");
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~ChatHubRemoteControlTests"`
Expected: FAIL — the hub methods don't exist yet.

- [ ] **Step 4: (implementation already specified in Step 1)** — ensure the code from Step 1 is in `ChatHub.cs`.

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~ChatHubRemoteControlTests"`
Expected: PASS (7 tests).

- [ ] **Step 6: Commit**

```bash
git add FatGuysSpeak.Server/Hubs/ChatHub.cs FatGuysSpeak.Tests/Server/ChatHubRemoteControlTests.cs
git commit -m "$(cat <<'EOF'
Remote control: hub session, request/offer/grant/accept + SendRemoteInput gate

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Automatic teardown on stream-stop and disconnect

**Files:**
- Modify: `FatGuysSpeak.Server/Hubs/ChatHub.cs` (`StopStream` ~377, `OnDisconnectedAsync` ~283)
- Modify: `FatGuysSpeak.Tests/Server/ChatHubRemoteControlTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `ChatHubRemoteControlTests`:

```csharp
    [Fact]
    public async Task StopStream_EndsAnyActiveControlSession()
    {
        var (_, streamer, viewer) = await SeedStreamAsync("ss-end");
        await Hub(streamer.Id, streamer.Username, "conn-s").GrantControl(viewer.Id);
        _sent.Clear();

        await Hub(streamer.Id, streamer.Username, "conn-s").StopStream();

        Assert.True(Sent($"user:{viewer.Id}", "ControlEnded"), "ending the stream must end control");
        _sent.Clear();
        await Hub(viewer.Id, viewer.Username, "conn-v").SendRemoteInput(new RemoteInputDto(RemoteInputKind.Move));
        Assert.False(_sent.Any(kv => kv.Value.Any(m => m.Method == "ReceiveRemoteInput")));
    }

    [Fact]
    public async Task StreamerDisconnect_EndsControlSession()
    {
        var (_, streamer, viewer) = await SeedStreamAsync("disc-s");
        await Hub(streamer.Id, streamer.Username, "conn-s").GrantControl(viewer.Id);
        _sent.Clear();

        await Hub(streamer.Id, streamer.Username, "conn-s").OnDisconnectedAsync(null);

        Assert.True(Sent($"user:{viewer.Id}", "ControlEnded"), "streamer disconnect must end control");
    }

    [Fact]
    public async Task ControllerDisconnect_EndsControlSession()
    {
        var (_, streamer, viewer) = await SeedStreamAsync("disc-c");
        await Hub(streamer.Id, streamer.Username, "conn-s").GrantControl(viewer.Id);
        _sent.Clear();

        await Hub(viewer.Id, viewer.Username, "conn-v").OnDisconnectedAsync(null);

        Assert.True(Sent($"user:{streamer.Id}", "ControlEnded"), "controller disconnect must end control");
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~ChatHubRemoteControlTests"`
Expected: FAIL — teardown not wired into `StopStream`/`OnDisconnectedAsync`.

- [ ] **Step 3: Add an end-by-participant helper**

In `ChatHub.cs`, add near the other remote-control helpers:

```csharp
    // Ends any control session in which `userId` is the streamer OR the controller.
    private async Task EndControlSessionsInvolvingAsync(int userId)
    {
        if (RemoteControlSessions.ContainsKey(userId))           // user is a streamer
        {
            await CloseControlSessionAsync(userId);
            return;
        }
        var asController = RemoteControlSessions.FirstOrDefault(kv => kv.Value.ControllerId == userId);
        if (asController.Value.ControllerId == userId && RemoteControlSessions.ContainsKey(asController.Key))
            await CloseControlSessionAsync(asController.Key);
    }
```

- [ ] **Step 4: Call it from StopStream and OnDisconnectedAsync**

In `StopStream`, after `if (!ActiveStreamers.TryRemove(UserId, out var info)) return;` add as the next line:

```csharp
        await EndControlSessionsInvolvingAsync(UserId);
```

In `OnDisconnectedAsync`, immediately after `OnlineUsers.TryRemove(UserId, out _);` add:

```csharp
        await EndControlSessionsInvolvingAsync(UserId);
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~ChatHubRemoteControlTests"`
Expected: PASS (10 tests in this class).

- [ ] **Step 6: Run the full suite (no regressions)**

Run: `dotnet test FatGuysSpeak.Tests`
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add FatGuysSpeak.Server/Hubs/ChatHub.cs FatGuysSpeak.Tests/Server/ChatHubRemoteControlTests.cs
git commit -m "$(cat <<'EOF'
Remote control: end session on stream-stop and on disconnect

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## PHASE B — Client (Windows; runtime-verified)

> These tasks touch MAUI/Win32 code that isn't unit-tested. Verification is: it compiles (`dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`), and the end-to-end behavior is checked by driving two live clients during a share (see Final verification). The reusable pure logic (coordinate mapping) is already covered by Task 1.

### Task 4: Expose the live capture rectangle from ScreenStreamService

**Files:**
- Modify: `FatGuysSpeak.Client/Services/ScreenStreamService.cs`

**Context:** Injection must map normalized coords to wherever the sharer is currently capturing. The Windows branch already knows: full desktop = `(0,0, SM_CXSCREEN, SM_CYSCREEN)`; window = `GetWindowRect(_targetWindow)`.

- [ ] **Step 1: Add a CurrentCaptureRect property (Windows branch)**

In the `#if WINDOWS` `ScreenStreamService`, add a public readonly accessor:

```csharp
    /// <summary>Live capture bounds in screen pixels: (left, top, width, height).
    /// Full-desktop share = the primary screen; window share = the window's current rect.</summary>
    public (int Left, int Top, int Width, int Height) CurrentCaptureRect
    {
        get
        {
            if (_targetWindow != IntPtr.Zero && GetWindowRect(_targetWindow, out var r))
                return (r.Left, r.Top, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
            return (0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
        }
    }
```

- [ ] **Step 2: Add a matching stub to the non-Windows branch**

In the `#else` `ScreenStreamService`, add:

```csharp
    public (int Left, int Top, int Width, int Height) CurrentCaptureRect => (0, 0, 1, 1);
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add FatGuysSpeak.Client/Services/ScreenStreamService.cs
git commit -m "$(cat <<'EOF'
Remote control: expose live capture rect from ScreenStreamService

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: RemoteInputService — Win32 SendInput injection

**Files:**
- Create: `FatGuysSpeak.Client/Services/RemoteInputService.cs`
- Modify: `FatGuysSpeak.Client/MauiProgram.cs` (DI registration)

**Context:** The sharer constructs `RemoteInputDto`s arriving over the hub into actual OS input. Uses `RemoteInputMapper.ToScreenPixel` (Task 1) to map into the capture rect, then converts pixels to `SendInput` absolute units (`0..65535` across the primary screen) and injects. `ScreenStreamService` provides the live rect.

- [ ] **Step 1: Create the service (Windows + stub)**

Create `FatGuysSpeak.Client/Services/RemoteInputService.cs`:

```csharp
#if WINDOWS
using System.Runtime.InteropServices;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.Services;

/// <summary>Injects a controller's RemoteInputDto into the local machine via Win32 SendInput.
/// Only the sharer uses this, and only while a control session is active.</summary>
public sealed class RemoteInputService(ScreenStreamService screen)
{
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int cb);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int n);
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public void Inject(RemoteInputDto dto)
    {
        try
        {
            switch (dto.Kind)
            {
                case RemoteInputKind.Move: SendMouse(dto, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE); break;
                case RemoteInputKind.Down: SendMouse(dto, ButtonFlag(dto.Button, down: true) | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE); break;
                case RemoteInputKind.Up:   SendMouse(dto, ButtonFlag(dto.Button, down: false) | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE); break;
                case RemoteInputKind.Wheel: SendWheel(dto.Delta); break;
                case RemoteInputKind.KeyDown: SendKey((ushort)dto.KeyCode, up: false); break;
                case RemoteInputKind.KeyUp:   SendKey((ushort)dto.KeyCode, up: true); break;
            }
        }
        catch { /* never let a bad input event break the stream */ }
    }

    private static uint ButtonFlag(int button, bool down) => button switch
    {
        1 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
        2 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
        _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
    };

    private void SendMouse(RemoteInputDto dto, uint flags)
    {
        var rect = screen.CurrentCaptureRect;
        var (px, py) = RemoteInputMapper.ToScreenPixel(dto.X, dto.Y, rect.Left, rect.Top, rect.Width, rect.Height);
        int sw = Math.Max(1, GetSystemMetrics(SM_CXSCREEN));
        int sh = Math.Max(1, GetSystemMetrics(SM_CYSCREEN));
        int ax = (int)(px * 65535.0 / sw);
        int ay = (int)(py * 65535.0 / sh);
        var input = new INPUT { type = INPUT_MOUSE, U = { mi = new MOUSEINPUT { dx = ax, dy = ay, dwFlags = flags } } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private void SendWheel(int delta)
    {
        var input = new INPUT { type = INPUT_MOUSE, U = { mi = new MOUSEINPUT { mouseData = (uint)delta, dwFlags = MOUSEEVENTF_WHEEL } } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private void SendKey(ushort vk, bool up)
    {
        var input = new INPUT { type = INPUT_KEYBOARD, U = { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }
}
#else
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.Services;

public sealed class RemoteInputService(ScreenStreamService screen)
{
    public void Inject(RemoteInputDto dto) { _ = screen; }
}
#endif
```

- [ ] **Step 2: Register in DI**

In `FatGuysSpeak.Client/MauiProgram.cs`, find where `ScreenStreamService` is registered (a `builder.Services.AddSingleton<ScreenStreamService>();` line) and add immediately after it:

```csharp
        builder.Services.AddSingleton<RemoteInputService>();
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add FatGuysSpeak.Client/Services/RemoteInputService.cs FatGuysSpeak.Client/MauiProgram.cs
git commit -m "$(cat <<'EOF'
Remote control: RemoteInputService (Win32 SendInput injection)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: ChatHubService wiring for control methods + events

**Files:**
- Modify: `FatGuysSpeak.Client/Services/ChatHubService.cs`

**Context:** `ChatHubService` exposes hub events as C# `event Action<...>` fields, registers `_connection.On<...>` handlers, and wraps server methods as `InvokeAsync` calls. Follow that established pattern (see the existing `StreamStarted`/`StreamFrameReceived` etc.).

- [ ] **Step 1: Add the event fields**

Near the other stream events (~line 30), add:

```csharp
    public event Action<int, string>? ControlRequested;   // (controllerId, controllerName) — to streamer
    public event Action<int, string>? ControlOffered;     // (streamerId, streamerName) — to viewer
    public event Action<int, string>? ControlActive;      // (controllerId, controllerName) — to streamer
    public event Action<int, string>? ControlGranted;     // (streamerId, streamerName) — to controller
    public event Action<int>? ControlDeclined;            // (byUserId)
    public event Action? ControlBusy;
    public event Action<int>? ControlEnded;               // (otherUserId)
    public event Action<RemoteInputDto>? RemoteInputReceived;
```

- [ ] **Step 2: Register the On handlers**

In the method where other `_connection.On<...>(...)` handlers are registered (search for `_connection.On<int, string, int>("StreamStarted"`), add alongside them:

```csharp
        _connection.On<int, string>("ControlRequested", (id, n) => ControlRequested?.Invoke(id, n));
        _connection.On<int, string>("ControlOffered",   (id, n) => ControlOffered?.Invoke(id, n));
        _connection.On<int, string>("ControlActive",    (id, n) => ControlActive?.Invoke(id, n));
        _connection.On<int, string>("ControlGranted",   (id, n) => ControlGranted?.Invoke(id, n));
        _connection.On<int>("ControlDeclined",          id => ControlDeclined?.Invoke(id));
        _connection.On("ControlBusy",                   () => ControlBusy?.Invoke());
        _connection.On<int>("ControlEnded",             id => ControlEnded?.Invoke(id));
        _connection.On<RemoteInputDto>("ReceiveRemoteInput", dto => RemoteInputReceived?.Invoke(dto));
```

- [ ] **Step 3: Add the invoke wrappers**

Near the other stream invoke wrappers (search for `public Task StartStream` / `SendStreamFrame`), add:

```csharp
    public Task RequestControl(int streamerId) => _connection.InvokeAsync("RequestControl", streamerId);
    public Task OfferControl(int viewerId) => _connection.InvokeAsync("OfferControl", viewerId);
    public Task GrantControl(int controllerId) => _connection.InvokeAsync("GrantControl", controllerId);
    public Task AcceptControl(int streamerId) => _connection.InvokeAsync("AcceptControl", streamerId);
    public Task DenyControl(int otherUserId) => _connection.InvokeAsync("DenyControl", otherUserId);
    public Task StopControl() => _connection.InvokeAsync("StopControl");
    public Task ReleaseControl() => _connection.InvokeAsync("ReleaseControl");
    public Task SendRemoteInput(RemoteInputDto dto) => _connection.SendAsync("SendRemoteInput", dto);
```

(Use `SendAsync` for `SendRemoteInput` — fire-and-forget, high frequency, no need to await server completion. Confirm `FatGuysSpeak.Shared` is in `using`s; it is, since other DTOs are used here.)

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add FatGuysSpeak.Client/Services/ChatHubService.cs
git commit -m "$(cat <<'EOF'
Remote control: ChatHubService events + invoke wrappers

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: MainViewModel + UI — flows, indicators, input capture, panic hotkey

**Files:**
- Modify: `FatGuysSpeak.Client/ViewModels/MainViewModel.cs`
- Modify: `FatGuysSpeak.Client/MainPage.xaml` and/or the full-screen stream view (wherever the stream `Image` lives)

**Context:** `MainViewModel` already holds stream state (`IsStreaming`, `ActiveStreamerId`, `StreamFrame`, etc.) and is constructed with the services via DI; add `RemoteInputService` to its constructor. It subscribes to `ChatHubService` events in its wiring. The stream `Image` is the input-capture surface for the controller. Injection is wired to `RemoteInputReceived` only while a session is active. Windows-gate the request/offer UI with `DeviceInfo.Platform == DevicePlatform.WinUI`.

- [ ] **Step 1: Inject RemoteInputService + add control state**

Add `RemoteInputService remoteInput` to the `MainViewModel` primary constructor parameter list (alongside `ScreenStreamService screen`), and store it. Add observable state:

```csharp
    [ObservableProperty] private bool _isBeingControlled;     // I am the sharer, someone controls me
    [ObservableProperty] private string? _controllerName;     // who controls me
    [ObservableProperty] private bool _isControlling;         // I am the controller of a remote stream
    [ObservableProperty] private string? _controlledName;     // whose screen I drive
    public bool CanOfferControl => IsStreaming && DeviceInfo.Platform == DevicePlatform.WinUI;
    public bool CanRequestControl => ActiveStreamerId > 0 && !IsStreaming && DeviceInfo.Platform == DevicePlatform.WinUI;
```

- [ ] **Step 2: Subscribe to control events**

In the constructor/wiring where other `hub.StreamStarted += ...` subscriptions are set up, add:

```csharp
        hub.ControlRequested += OnControlRequested;
        hub.ControlOffered   += OnControlOffered;
        hub.ControlActive    += OnControlActive;
        hub.ControlGranted   += OnControlGranted;
        hub.ControlEnded     += OnControlEnded;
        hub.ControlDeclined  += _ => MainThread.BeginInvokeOnMainThread(() =>
            Application.Current!.MainPage!.DisplayAlert("Remote control", "Request was declined.", "OK"));
        hub.ControlBusy      += () => MainThread.BeginInvokeOnMainThread(() =>
            Application.Current!.MainPage!.DisplayAlert("Remote control", "Someone already has control of that screen.", "OK"));
        hub.RemoteInputReceived += dto => { if (IsBeingControlled) remoteInput.Inject(dto); };
```

- [ ] **Step 3: Implement the event handlers**

Add to `MainViewModel`:

```csharp
    private void OnControlRequested(int controllerId, string controllerName) =>
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var ok = await Application.Current!.MainPage!.DisplayAlert(
                "Remote control request",
                $"{controllerName} wants to control your screen.\n\nThey will be able to control your WHOLE PC — not just the shared window — until you stop it.",
                "Allow", "Deny");
            if (ok) await hub.GrantControl(controllerId);
            else await hub.DenyControl(controllerId);
        });

    private void OnControlOffered(int streamerId, string streamerName) =>
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var ok = await Application.Current!.MainPage!.DisplayAlert(
                "Remote control offered",
                $"{streamerName} is giving you control of their screen. Accept?",
                "Accept", "Decline");
            if (ok) await hub.AcceptControl(streamerId);
            else await hub.DenyControl(streamerId);
        });

    private void OnControlActive(int controllerId, string controllerName) =>
        MainThread.BeginInvokeOnMainThread(() => { IsBeingControlled = true; ControllerName = controllerName; });

    private void OnControlGranted(int streamerId, string streamerName) =>
        MainThread.BeginInvokeOnMainThread(() => { IsControlling = true; ControlledName = streamerName; });

    private void OnControlEnded(int _) =>
        MainThread.BeginInvokeOnMainThread(() =>
        { IsBeingControlled = false; ControllerName = null; IsControlling = false; ControlledName = null; });
```

- [ ] **Step 4: Add commands for the request/offer/stop/release actions**

```csharp
    [RelayCommand] private Task RequestControl() => ActiveStreamerId > 0 ? hub.RequestControl(ActiveStreamerId) : Task.CompletedTask;
    [RelayCommand] private Task StopControl() => hub.StopControl();
    [RelayCommand] private Task ReleaseControl() => hub.ReleaseControl();
    // OfferControl targets a chosen viewer; wire to a viewer-picker in the UI or default to a passed id.
    [RelayCommand] private Task OfferControl(int viewerId) => hub.OfferControl(viewerId);
```

- [ ] **Step 5: Capture controller input over the stream Image (code-behind)**

In the page hosting the stream `Image` (e.g. `MainPage.xaml.cs` or the stream window), add pointer + key handlers that fire only when `vm.IsControlling`, converting to normalized coords against the `Image`'s render size and calling `hub.SendRemoteInput`. Throttle moves. Example handler skeleton (WinUI gestures via MAUI `PointerGestureRecognizer` for move + a tap/right-tap mapping, and key handling on the page):

```csharp
    // Pseudocode placement — attach in the stream Image's loaded handler when controlling.
    long _lastMoveTicks;
    void OnStreamPointerMoved(double localX, double localY, double w, double h)
    {
        if (!_vm.IsControlling) return;
        var now = Environment.TickCount64;
        if (now - _lastMoveTicks < 25) return;     // ~40/s throttle
        _lastMoveTicks = now;
        _vm.Hub.SendRemoteInput(new RemoteInputDto(RemoteInputKind.Move, localX / w, localY / h));
    }
```

Because MAUI's cross-platform pointer/keyboard coverage is limited, implement the actual capture in the `#if WINDOWS` partial using WinUI events on the `Image`'s `FrameworkElement`: `PointerMoved`/`PointerPressed`/`PointerReleased`/`PointerWheelChanged` (use `e.GetCurrentPoint(image).Position` and `image.ActualWidth/ActualHeight` for normalization; map `Properties.IsRightButtonPressed`/`MouseWheelDelta`), and `KeyDown`/`KeyUp` on the hosting page (map `VirtualKey` to its integer value for `KeyCode`). Each event, when `_vm.IsControlling`, calls `_vm.Hub.SendRemoteInput(...)`. Expose `Hub` from the VM (or route through a VM method `SendRemoteInput`).

- [ ] **Step 6: Add the panic hotkey + indicator UI**

In the `#if WINDOWS` page code-behind, register a global accelerator so the sharer can always bail: `Ctrl+Alt+Break` → `_vm.StopControlCommand.Execute(null)`. Use a WinUI `KeyboardAccelerator` (Ctrl+Menu modifiers + `VirtualKey.Pause`) on the root element, or a low-level hook if an app-global accelerator is insufficient.

In the stream view XAML, add two banners:
- Visible when `IsBeingControlled`: red bar "{ControllerName} is controlling your screen" with a prominent **Stop control** button bound to `StopControlCommand`.
- Visible when `IsControlling`: bar "You are controlling {ControlledName}'s screen" with a **Release** button bound to `ReleaseControlCommand`.

Add the entry points:
- A "Request control" button shown when `CanRequestControl`, bound to `RequestControlCommand`.
- A "Give control" affordance shown when `CanOfferControl` (a viewer picker invoking `OfferControlCommand` with the chosen viewer's id).

- [ ] **Step 7: Build to verify it compiles**

Run: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
Expected: Build succeeded.

- [ ] **Step 8: Commit**

```bash
git add FatGuysSpeak.Client/ViewModels/MainViewModel.cs FatGuysSpeak.Client/MainPage.xaml FatGuysSpeak.Client/MainPage.xaml.cs
git commit -m "$(cat <<'EOF'
Remote control: client flows, indicators, input capture, panic hotkey

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Final verification (after all tasks)

- [ ] `dotnet test FatGuysSpeak.Tests` — all green (mapper + hub control tests + existing suite).
- [ ] Two-client live run via `launch.ps1`: client A shares its full desktop; client B watches. From A, "Give control" to B (or from B, "Request control" → A allows). Confirm B's mouse moves and clicks land on A's screen, B's keystrokes type on A, the red "being controlled" banner shows on A, and A's "Stop control" button **and** `Ctrl+Alt+Break` both instantly end control (B's input stops affecting A). Repeat with a single-window share to confirm control is offered there too.
- [ ] Confirm that after A stops the stream (or either client disconnects), control ends and further input from B has no effect.
- [ ] Dispatch a final code review over the whole branch.
