# Drag-to-Move Users Between Voice Channels — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a moderator/admin drag a user from a voice channel's occupant list and drop them on another voice channel, force-moving that user's live voice session.

**Architecture:** A new SignalR hub method `MoveUserToVoiceChannel(targetUserId, channelId)` validates the mover's role + target state, writes a `"VoiceMoved"` audit row, and relays a `ForceMoveToVoice` event to the *target's* client, which runs its normal leave-then-join-voice path (reusing existing logic that swaps SignalR groups, re-routes audio, and broadcasts join/leave so all occupant lists refresh live). The client adds MAUI drag/drop gestures gated on the existing `IsAdminOrModerator` flag.

**Tech Stack:** ASP.NET Core SignalR, EF Core, .NET MAUI (Windows) drag/drop gesture recognizers, xUnit + Moq.

---

## File Structure

- `FatGuysSpeak.Server/Hubs/ChatHub.cs` — add `MoveUserToVoiceChannel` (validation + audit + relay).
- `FatGuysSpeak.Tests/Server/ChatHubMoveUserTests.cs` — NEW; hub tests (`HubTests` collection).
- `FatGuysSpeak.Client/Services/ChatHubService.cs` — add `MoveUserToVoiceChannelAsync` + `ForceMoveToVoice` event + its `.On` registration.
- `FatGuysSpeak.Client/ViewModels/MainViewModel.cs` — `OnForceMoveToVoice` handler, `BeginUserDrag`/`DropUserOnChannel` commands, event subscribe/unsubscribe, raise `IsAdminOrModerator` change.
- `FatGuysSpeak.Client/Pages/MainPage.xaml` — `DragGestureRecognizer` on the voice occupant row, `DropGestureRecognizer` on the channel row.
- `FatGuysSpeak.Server/wwwroot/dashboard.js` — add `VoiceMoved` to the audit action-color map.

---

### Task 1: Server hub method + audit + relay (TDD)

**Files:**
- Create: `FatGuysSpeak.Tests/Server/ChatHubMoveUserTests.cs`
- Modify: `FatGuysSpeak.Server/Hubs/ChatHub.cs` (add method after `JoinVoiceChannel`, which ends ~line 155)

- [ ] **Step 1: Write the failing tests.** Create `FatGuysSpeak.Tests/Server/ChatHubMoveUserTests.cs`. This mirrors `CameraRelayTests` harness but adds a `Clients.User(...)` mock so we can assert the relay to a specific user. Seed adds a second voice channel + extra members.

```csharp
using System.Reflection;
using System.Security.Claims;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace FatGuysSpeak.Tests.Server;

[Collection("HubTests")]
public class ChatHubMoveUserTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly Mock<IHubCallerClients> _mockClients;
    private readonly Mock<IGroupManager> _mockGroups;
    private readonly Dictionary<string, List<(string Method, object[] Args)>> _sent = new();

    public ChatHubMoveUserTests()
    {
        _testDb = new TestDb();
        _mockClients = new Mock<IHubCallerClients>();
        _mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns<string>(g => Proxy($"group:{g}"));
        _mockClients.Setup(c => c.OthersInGroup(It.IsAny<string>())).Returns<string>(g => Proxy($"others:{g}"));
        _mockClients.Setup(c => c.Caller).Returns(Single("caller"));
        _mockClients.Setup(c => c.User(It.IsAny<string>())).Returns<string>(id => Single($"user:{id}"));

        _mockGroups = new Mock<IGroupManager>();
        _mockGroups.Setup(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockGroups.Setup(g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    public void Dispose() { ClearHubStaticState(); _testDb.Dispose(); }

    private IClientProxy Proxy(string t)
    {
        var p = new Mock<IClientProxy>();
        p.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((m, a, _) => Track(t, m, a)).Returns(Task.CompletedTask);
        return p.Object;
    }
    private ISingleClientProxy Single(string t)
    {
        var p = new Mock<ISingleClientProxy>();
        p.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((m, a, _) => Track(t, m, a)).Returns(Task.CompletedTask);
        return p.Object;
    }
    private void Track(string t, string m, object[] a) { if (!_sent.TryGetValue(t, out var l)) _sent[t] = l = []; l.Add((m, a)); }
    private bool WasSentTo(string t, string m) => _sent.TryGetValue(t, out var l) && l.Any(x => x.Method == m);
    private (string Method, object[] Args) LastSent(string t, string m) => _sent[t].Last(x => x.Method == m);

    private ChatHub CreateHub(int userId, string username, string conn)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Name, username) };
        var ctx = new Mock<HubCallerContext>();
        ctx.Setup(c => c.ConnectionId).Returns(conn);
        ctx.Setup(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
        return new ChatHub(_testDb.Db, new FatGuysSpeak.Server.Services.OnlineTimeTracker())
        { Context = ctx.Object, Clients = _mockClients.Object, Groups = _mockGroups.Object };
    }

    // Adds a user + ServerMember with the given role; returns the user.
    private async Task<User> AddMemberAsync(int serverId, string name, ServerRole role)
    {
        var u = new User { Username = name, Email = $"{name}@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(u);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = serverId, UserId = u.Id, Role = role });
        await _testDb.Db.SaveChangesAsync();
        return u;
    }
    // Adds a second voice channel to move into.
    private async Task<Channel> AddVoiceChannelAsync(int serverId, string name)
    {
        var ch = new Channel { Name = name, Type = ChannelType.Voice, ServerId = serverId, Position = 9 };
        _testDb.Db.Channels.Add(ch);
        await _testDb.Db.SaveChangesAsync();
        return ch;
    }

    private static void ClearHubStaticState()
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        foreach (var name in new[] { "ActiveStreamers", "VoiceChannelMap", "UserTextChannelMap", "ChannelOccupants", "OnlineUsers", "ActiveCameras" })
            (typeof(ChatHub).GetField(name, flags)?.GetValue(null) as System.Collections.IDictionary)?.Clear();
    }

    [Fact]
    public async Task AdminMovesMemberInVoice_SendsForceMoveToTarget_AndWritesAudit()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_testDb.Db, "mv-admin"); // owner = Admin
        var voiceA = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var voiceB = await AddVoiceChannelAsync(server.Id, "Voice B");
        var target = await AddMemberAsync(server.Id, "mv-target", ServerRole.Member);

        // Put the target into voiceA.
        await CreateHub(target.Id, target.Username, "conn-target").JoinVoiceChannel(voiceA.Id);
        _sent.Clear();

        await CreateHub(owner.Id, owner.Username, "conn-admin").MoveUserToVoiceChannel(target.Id, voiceB.Id);

        Assert.True(WasSentTo($"user:{target.Id}", "ForceMoveToVoice"));
        var (_, args) = LastSent($"user:{target.Id}", "ForceMoveToVoice");
        Assert.Equal(voiceB.Id, (int)args[0]);
        Assert.Equal(owner.Username, (string)args[1]);
        Assert.True(await _testDb.Db.AuditLogs.AnyAsync(a => a.Action == "VoiceMoved" && a.TargetId == target.Id && a.ActorId == owner.Id));
    }

    [Fact]
    public async Task ModeratorMovesMember_IsAllowed()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_testDb.Db, "mv-mod");
        var voiceA = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var voiceB = await AddVoiceChannelAsync(server.Id, "Voice B");
        var mod = await AddMemberAsync(server.Id, "mv-themod", ServerRole.Moderator);
        var target = await AddMemberAsync(server.Id, "mv-tgt2", ServerRole.Member);

        await CreateHub(target.Id, target.Username, "conn-t2").JoinVoiceChannel(voiceA.Id);
        _sent.Clear();
        await CreateHub(mod.Id, mod.Username, "conn-mod").MoveUserToVoiceChannel(target.Id, voiceB.Id);

        Assert.True(WasSentTo($"user:{target.Id}", "ForceMoveToVoice"));
    }

    [Fact]
    public async Task MemberCaller_CannotMove_NoSend_NoAudit()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_testDb.Db, "mv-member");
        var voiceA = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var voiceB = await AddVoiceChannelAsync(server.Id, "Voice B");
        var caller = await AddMemberAsync(server.Id, "mv-lowcaller", ServerRole.Member);
        var target = await AddMemberAsync(server.Id, "mv-tgt3", ServerRole.Member);

        await CreateHub(target.Id, target.Username, "conn-t3").JoinVoiceChannel(voiceA.Id);
        _sent.Clear();
        await CreateHub(caller.Id, caller.Username, "conn-low").MoveUserToVoiceChannel(target.Id, voiceB.Id);

        Assert.False(WasSentTo($"user:{target.Id}", "ForceMoveToVoice"));
        Assert.False(await _testDb.Db.AuditLogs.AnyAsync(a => a.Action == "VoiceMoved"));
    }

    [Fact]
    public async Task TargetNotInVoice_NoSend()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_testDb.Db, "mv-novoice");
        var voiceB = await AddVoiceChannelAsync(server.Id, "Voice B");
        var target = await AddMemberAsync(server.Id, "mv-idle", ServerRole.Member); // never joined voice

        await CreateHub(owner.Id, owner.Username, "conn-a").MoveUserToVoiceChannel(target.Id, voiceB.Id);

        Assert.False(WasSentTo($"user:{target.Id}", "ForceMoveToVoice"));
    }

    [Fact]
    public async Task TargetAlreadyInDestination_NoSend()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_testDb.Db, "mv-same");
        var voiceA = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var target = await AddMemberAsync(server.Id, "mv-tgt4", ServerRole.Member);

        await CreateHub(target.Id, target.Username, "conn-t4").JoinVoiceChannel(voiceA.Id);
        _sent.Clear();
        await CreateHub(owner.Id, owner.Username, "conn-a").MoveUserToVoiceChannel(target.Id, voiceA.Id); // same channel

        Assert.False(WasSentTo($"user:{target.Id}", "ForceMoveToVoice"));
    }
}
```

- [ ] **Step 2: Run the tests, verify they fail.**
Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~ChatHubMoveUserTests"`
Expected: FAIL to compile — `ChatHub` has no `MoveUserToVoiceChannel`.

- [ ] **Step 3: Implement the hub method.** In `FatGuysSpeak.Server/Hubs/ChatHub.cs`, add this method immediately after `JoinVoiceChannel` (after its closing brace, ~line 155):

```csharp
    /// <summary>Moderator/admin force-moves a target user's live voice session into another
    /// voice channel. The server validates + audits, then tells the TARGET's client to switch
    /// (it runs its own join path). Silent no-op on any invalid request.</summary>
    public async Task MoveUserToVoiceChannel(int targetUserId, int channelId)
    {
        var channel = await db.Channels
            .Include(c => c.Server).ThenInclude(s => s.Members)
            .FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return;

        // Caller must be a Moderator+ member of the destination channel's server (flat permission).
        var mover = channel.Server.Members.FirstOrDefault(m => m.UserId == UserId);
        if (mover is null || mover.Role < ServerRole.Moderator) return;

        // Target must be a member of that server.
        var target = channel.Server.Members.FirstOrDefault(m => m.UserId == targetUserId);
        if (target is null) return;

        // Target must currently be in voice, not already in the destination, on the same server.
        if (!VoiceChannelMap.TryGetValue(targetUserId, out var currentVoice)) return;
        if (currentVoice == channelId) return;
        var currentServerId = await db.Channels.Where(c => c.Id == currentVoice)
            .Select(c => (int?)c.ServerId).FirstOrDefaultAsync();
        if (currentServerId != channel.ServerId) return;

        // Respect the destination's read permission for the target.
        var perm = await db.ChannelPermissions.FindAsync(channelId);
        if (perm is not null && perm.MinRoleToRead > ServerRole.Member && target.Role < perm.MinRoleToRead)
            return;

        var targetName = OnlineUsers.TryGetValue(targetUserId, out var tn)
            ? tn
            : (await db.Users.Where(u => u.Id == targetUserId).Select(u => u.Username).FirstOrDefaultAsync() ?? "");

        db.AuditLogs.Add(new FatGuysSpeak.Server.Models.AuditLog
        {
            ServerId = channel.ServerId,
            ActorId = UserId,
            ActorUsername = Username,
            Action = "VoiceMoved",
            TargetId = targetUserId,
            TargetUsername = targetName,
            Detail = $"to #{channel.Name}"
        });
        await db.SaveChangesAsync();

        await Clients.User(targetUserId.ToString()).SendAsync("ForceMoveToVoice", channelId, Username);
    }
```

- [ ] **Step 4: Run the tests, verify they pass.**
Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~ChatHubMoveUserTests"`
Expected: PASS (5/5). Ignore any LSP "Ambiguity between X and X" squiggles — trust the CLI.

- [ ] **Step 5: Commit.**
```bash
git add FatGuysSpeak.Server/Hubs/ChatHub.cs FatGuysSpeak.Tests/Server/ChatHubMoveUserTests.cs
git commit -m "Server: ChatHub.MoveUserToVoiceChannel (mod/admin force-move voice) + audit"
```

---

### Task 2: Client hub-service plumbing

**Files:**
- Modify: `FatGuysSpeak.Client/Services/ChatHubService.cs`

No unit test (thin SignalR plumbing); verified by build + Task 5 runtime.

- [ ] **Step 1: Add the event.** Near the other event declarations (after the `ForceJoinChannel` event, ~line 19) add:
```csharp
    public event Action<int, string>? ForceMoveToVoice;   // (channelId, moverUsername) — target switches voice
```

- [ ] **Step 2: Register the handler.** Next to the existing `_connection.On<int>("ForceJoinChannel", ...)` registration (~line 92) add:
```csharp
        _connection.On<int, string>("ForceMoveToVoice", (channelId, mover) => ForceMoveToVoice?.Invoke(channelId, mover));
```

- [ ] **Step 3: Add the invoke wrapper.** Next to `JoinVoiceChannelAsync` (~line 160) add:
```csharp
    public Task MoveUserToVoiceChannelAsync(int targetUserId, int channelId) =>
        _connection!.InvokeAsync("MoveUserToVoiceChannel", targetUserId, channelId);
```

- [ ] **Step 4: Build.**
Run: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
Expected: `0 Error(s)`.

- [ ] **Step 5: Commit.**
```bash
git add FatGuysSpeak.Client/Services/ChatHubService.cs
git commit -m "Client hub service: ForceMoveToVoice event + MoveUserToVoiceChannelAsync"
```

---

### Task 3: Client ViewModel — handler + drag/drop commands

**Files:**
- Modify: `FatGuysSpeak.Client/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Add a field for the dragged user.** Near the other private fields (e.g. after `private int _voiceChannelId;` ~line 1830, or with the version-sync fields — any class-field location):
```csharp
    private FatGuysSpeak.Shared.UserDto? _draggedUser;
```

- [ ] **Step 2: Add the two commands** (place near the other `[RelayCommand]` voice methods):
```csharp
    [RelayCommand]
    private void BeginUserDrag(FatGuysSpeak.Shared.UserDto user) => _draggedUser = user;

    [RelayCommand]
    private async Task DropUserOnChannel(ChannelViewItem target)
    {
        var user = _draggedUser;
        _draggedUser = null;
        if (user is null || target is null) return;
        if (!IsAdminOrModerator) return;                 // server re-checks; this just gates the UI path
        if (user.Id == api.CurrentUserId) return;         // dragging yourself is a no-op
        await hub.MoveUserToVoiceChannelAsync(user.Id, target.Channel.Id);
    }
```

- [ ] **Step 3: Add the relay handler** (place near `OnForceJoinChannel`, ~line 2374):
```csharp
    private void OnForceMoveToVoice(int channelId, string mover)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var item = Channels.FirstOrDefault(c => c.Channel.Id == channelId);
            if (item is null || _voiceChannelId == channelId) return;
            if (InVoice) await LeaveVoiceAsync();
            await JoinVoiceAsync(item.Channel);
            toast.Show("FatGuysSpeak", $"Moved to #{item.Channel.Name} by {mover}");
        });
    }
```

- [ ] **Step 4: Subscribe + unsubscribe the event.** Where `hub.ForceJoinChannel += OnForceJoinChannel;` appears (~line 503) add:
```csharp
        hub.ForceMoveToVoice += OnForceMoveToVoice;
```
Where `hub.ForceJoinChannel -= OnForceJoinChannel;` appears (~line 1769) add:
```csharp
        hub.ForceMoveToVoice -= OnForceMoveToVoice;
```

- [ ] **Step 5: Raise change notification for the drag gate.** Where the server-switch raises role notifications (the block with `OnPropertyChanged(nameof(IsServerAdmin));`, ~line 275-276) add:
```csharp
        OnPropertyChanged(nameof(IsAdminOrModerator));
```

- [ ] **Step 6: Build.**
Run: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
Expected: `0 Error(s)`.

- [ ] **Step 7: Commit.**
```bash
git add FatGuysSpeak.Client/ViewModels/MainViewModel.cs
git commit -m "Client VM: force-move-to-voice handler + drag/drop move commands"
```

---

### Task 4: Client XAML — drag source + drop target

**Files:**
- Modify: `FatGuysSpeak.Client/Pages/MainPage.xaml`

- [ ] **Step 1: Make the voice occupant row draggable.** In the occupant `DataTemplate` (`x:DataType="shared:UserDto"`, ~line 408), the outer `<Grid Padding="28,3,8,3" BackgroundColor="Transparent">` (line 409) currently contains only `<FlyoutBase.ContextFlyout>` and a `<HorizontalStackLayout>`. Add a gesture-recognizers block as the FIRST child of that Grid (right after the opening `<Grid ...>` tag, before `<FlyoutBase.ContextFlyout>`):
```xml
                                                                        <Grid.GestureRecognizers>
                                                                            <DragGestureRecognizer
                                                                                CanDrag="{Binding BindingContext.IsAdminOrModerator, Source={x:Reference RootPage}}"
                                                                                DragStartingCommand="{Binding BindingContext.BeginUserDragCommand, Source={x:Reference RootPage}}"
                                                                                DragStartingCommandParameter="{Binding .}" />
                                                                        </Grid.GestureRecognizers>
```

- [ ] **Step 2: Make the channel row a drop target.** In the channel `DataTemplate` (`x:DataType="vm:ChannelViewItem"`, ~line 335), the row `<Grid Padding="8,5,8,5" BackgroundColor="{Binding IsSelected, Converter={StaticResource ChannelSelectionConverter}}">` (~line 337-338) already has a `VisualStateManager.VisualStateGroups`, a `FlyoutBase.ContextFlyout`, a `<Grid.GestureRecognizers>` (with the TapGestureRecognizer), a BoxView, and the inner content Grid. Add a `<DropGestureRecognizer>` **inside the existing `<Grid.GestureRecognizers>`** (alongside the TapGestureRecognizer):
```xml
                                                                <DropGestureRecognizer
                                                                    AllowDrop="{Binding BindingContext.IsAdminOrModerator, Source={x:Reference RootPage}}"
                                                                    DropCommand="{Binding BindingContext.DropUserOnChannelCommand, Source={x:Reference RootPage}}"
                                                                    DropCommandParameter="{Binding .}" />
```

- [ ] **Step 3: Build.**
Run: `dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0`
Expected: `0 Error(s)`. (`RootPage` is the page's `x:Name`, declared at the top of MainPage.xaml.)

- [ ] **Step 4: Commit.**
```bash
git add FatGuysSpeak.Client/Pages/MainPage.xaml
git commit -m "Client XAML: drag voice occupant -> drop on channel to move (mod/admin)"
```

---

### Task 5: Dashboard color + runtime acceptance

**Files:**
- Modify: `FatGuysSpeak.Server/wwwroot/dashboard.js`

- [ ] **Step 1: Add the audit action color.** In `dashboard.js`, in the action→color map (the object containing entries like `MemberKicked: '#ed4245',` and `ChannelPermissionsChanged:'#f04010',`), add:
```javascript
    VoiceMoved:               '#f04010',
```

- [ ] **Step 2: Commit.**
```bash
git add FatGuysSpeak.Server/wwwroot/dashboard.js
git commit -m "Dashboard: color for VoiceMoved audit action"
```

- [ ] **Step 3: Manual runtime acceptance (two clients).** Build + launch server + 2 clients (`.\launch.ps1`). Log in client A as a mod/admin, client B as a member. Put B in a voice channel. In A's sidebar, drag B (from the voice channel's occupant list) onto a different voice channel. Verify: B is moved (B's toast "Moved to #X by …", B's audio routes to the new channel), both sidebars' occupant lists update, and the dashboard audit log shows a `VoiceMoved` entry. Also verify a non-mod client shows no drag affordance.

---

## Self-Review

- **Spec coverage:** voice force-move only (Task 1 validates target-in-voice; Task 3 join path) ✓; drag from occupant list / drop on channel (Task 4) ✓; flat mod/admin permission (Task 1 role check + Task 3/4 `IsAdminOrModerator` gate) ✓; audit entry (Task 1, tested) ✓; relay-to-target mechanism reusing join path (Task 1 + Task 3) ✓; live occupant refresh (reuses `JoinVoiceChannel` broadcasts — no new code) ✓; silent no-ops (Task 1) ✓; moved-user toast (Task 3) ✓; hub-test matrix (Task 1) ✓.
- **Placeholder scan:** none — every step has complete code/commands.
- **Type consistency:** `MoveUserToVoiceChannel(int,int)` (Task 1) ↔ `MoveUserToVoiceChannelAsync(int,int)` (Task 2) ↔ `hub.MoveUserToVoiceChannelAsync(user.Id, target.Channel.Id)` (Task 3); `ForceMoveToVoice` event is `Action<int,string>` everywhere (Tasks 2-3); `BeginUserDragCommand`/`DropUserOnChannelCommand` are the CommunityToolkit-generated names for `BeginUserDrag`/`DropUserOnChannel` (Task 3) and are bound under those names in XAML (Task 4); `IsAdminOrModerator` is the existing computed flag (MainViewModel:153); `ChannelViewItem.Channel.Id` and `UserDto.Id` match the bound template data types.
```
