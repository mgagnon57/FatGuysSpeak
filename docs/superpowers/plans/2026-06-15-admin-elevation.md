# Admin Elevation & Channel Creation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow any in-app Admin to elevate a non-Admin to Admin via right-click, and to create channels via a "+" button or right-click.

**Architecture:** Relax two server-side permission guards, add a client-side `Dictionary<int, ServerRole>` role map loaded at server-select time and kept live by a new `MemberRoleChanged` SignalR event handler, then wire the two new commands into the MAUI UI.

**Tech Stack:** .NET 9 ASP.NET Core, MAUI XAML, CommunityToolkit.Mvvm, xUnit + Moq, SignalR.

---

## File Map

| File | Change |
|------|--------|
| `FatGuysSpeak.Server/Controllers/ServersController.cs` | Relax promotion guard; relax `GetMembersWithRoles` to any member |
| `FatGuysSpeak.Tests/Server/ServersControllerTests.cs` | New tests for promotion + role-read |
| `FatGuysSpeak.Tests/Server/RoleEnforcementTests.cs` | New test: already-Admin returns 400 |
| `FatGuysSpeak.Client/Services/ApiService.cs` | Add `GetMemberRolesAsync` + `SetMemberRoleAsync` |
| `FatGuysSpeak.Client/Services/ChatHubService.cs` | Add `MemberRoleChanged` event + `_connection.On` handler |
| `FatGuysSpeak.Client/ViewModels/MainViewModel.cs` | Add `_memberRoles` dict, load on server select, handle event, add two commands |
| `FatGuysSpeak.Client/Pages/MainPage.xaml` | Add "Make Admin" to user flyouts; add "+" button + channel flyout |

---

### Task 1: Server — fix permission guards

**Files:**
- Modify: `FatGuysSpeak.Server/Controllers/ServersController.cs`
- Test: `FatGuysSpeak.Tests/Server/ServersControllerTests.cs`
- Test: `FatGuysSpeak.Tests/Server/RoleEnforcementTests.cs`

- [ ] **Step 1: Write failing tests**

Add to `FatGuysSpeak.Tests/Server/ServersControllerTests.cs` (inside the existing class, after the last test):

```csharp
[Fact]
public async Task SetMemberRole_AdminPromotesNonAdminToAdmin_Succeeds()
{
    await SeedAsync();
    var member = new User { Username = "member2", Email = "m2@test.com", PasswordHash = "*" };
    _testDb.Db.Users.Add(member);
    await _testDb.Db.SaveChangesAsync();
    _testDb.Db.ServerMembers.Add(new ServerMember
        { ServerId = _server.Id, UserId = member.Id, Role = ServerRole.Member });
    await _testDb.Db.SaveChangesAsync();
    // _user is already Admin (seeded by SeedServerAsync)

    var result = await _controller.SetMemberRole(_server.Id, member.Id,
        new SetRoleRequest(ServerRole.Admin));

    Assert.IsType<NoContentResult>(result);
    var updated = await _testDb.Db.ServerMembers.FindAsync(_server.Id, member.Id);
    Assert.Equal(ServerRole.Admin, updated!.Role);
}

[Fact]
public async Task SetMemberRole_PromoteAlreadyAdmin_ReturnsBadRequest()
{
    await SeedAsync();
    var admin2 = new User { Username = "admin2", Email = "a2@test.com", PasswordHash = "*" };
    _testDb.Db.Users.Add(admin2);
    await _testDb.Db.SaveChangesAsync();
    _testDb.Db.ServerMembers.Add(new ServerMember
        { ServerId = _server.Id, UserId = admin2.Id, Role = ServerRole.Admin });
    await _testDb.Db.SaveChangesAsync();

    var result = await _controller.SetMemberRole(_server.Id, admin2.Id,
        new SetRoleRequest(ServerRole.Admin));

    Assert.IsType<BadRequestObjectResult>(result);
}

[Fact]
public async Task SetMemberRole_NonAdminCannotPromote_ReturnsForbid()
{
    await SeedAsync();
    var member = new User { Username = "member2", Email = "m2@test.com", PasswordHash = "*" };
    var target = new User { Username = "target", Email = "target@test.com", PasswordHash = "*" };
    _testDb.Db.Users.AddRange(member, target);
    await _testDb.Db.SaveChangesAsync();
    _testDb.Db.ServerMembers.AddRange(
        new ServerMember { ServerId = _server.Id, UserId = member.Id, Role = ServerRole.Member },
        new ServerMember { ServerId = _server.Id, UserId = target.Id, Role = ServerRole.Member }
    );
    await _testDb.Db.SaveChangesAsync();
    TestHelpers.SetUser(_controller, member.Id, member.Username);

    var result = await _controller.SetMemberRole(_server.Id, target.Id,
        new SetRoleRequest(ServerRole.Admin));

    Assert.IsType<ForbidResult>(result);
}

[Fact]
public async Task GetMembersWithRoles_AsRegularMember_Succeeds()
{
    await SeedAsync();
    var member = new User { Username = "member2", Email = "m2@test.com", PasswordHash = "*" };
    _testDb.Db.Users.Add(member);
    await _testDb.Db.SaveChangesAsync();
    _testDb.Db.ServerMembers.Add(new ServerMember
        { ServerId = _server.Id, UserId = member.Id, Role = ServerRole.Member });
    await _testDb.Db.SaveChangesAsync();
    TestHelpers.SetUser(_controller, member.Id, member.Username);

    var result = await _controller.GetMembersWithRoles(_server.Id);

    Assert.IsType<OkObjectResult>(result.Result);
}
```

- [ ] **Step 2: Run tests to verify they fail**

```
dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~SetMemberRole_AdminPromotesNonAdmin|SetMemberRole_PromoteAlreadyAdmin|SetMemberRole_NonAdminCannotPromote|GetMembersWithRoles_AsRegularMember" -v
```

Expected: all four FAIL.

- [ ] **Step 3: Fix `ServersController.SetMemberRole`**

In `FatGuysSpeak.Server/Controllers/ServersController.cs`, locate `SetMemberRole` (~line 167). Remove the owner-only promotion guard and add the already-admin BadRequest. The method body becomes:

```csharp
[HttpPut("{serverId}/members/{targetUserId}/role")]
public async Task<IActionResult> SetMemberRole(int serverId, int targetUserId, SetRoleRequest req)
{
    var actor = await db.ServerMembers.FindAsync(serverId, UserId);
    if (actor is null || actor.Role < ServerRole.Admin) return Forbid();

    if (targetUserId == UserId) return BadRequest("Cannot change your own role.");

    var server = await db.Servers.FindAsync(serverId);
    if (server is null) return NotFound();

    var target = await db.ServerMembers.Include(sm => sm.User)
        .FirstOrDefaultAsync(sm => sm.ServerId == serverId && sm.UserId == targetUserId);
    if (target is null) return NotFound();

    // Defence-in-depth: reject promoting someone who is already Admin
    if (req.Role == ServerRole.Admin && target.Role == ServerRole.Admin)
        return BadRequest("User is already an Admin.");

    // Only the server owner can demote an existing Admin
    if (target.Role == ServerRole.Admin && UserId != server.OwnerId)
        return Forbid();

    var oldRole = target.Role;
    target.Role = req.Role;
    db.AuditLogs.Add(new AuditLog
    {
        ServerId = serverId, ActorId = UserId, ActorUsername = Username,
        Action = "RoleChanged", TargetId = targetUserId, TargetUsername = target.User.Username,
        Detail = $"{oldRole} → {req.Role}"
    });
    await db.SaveChangesAsync();

    await hub.Clients.Group($"server-{serverId}")
        .SendAsync("MemberRoleChanged", targetUserId, req.Role.ToString());
    return NoContent();
}
```

- [ ] **Step 4: Fix `GetMembersWithRoles` to allow any member**

In the same file, locate `GetMembersWithRoles` (~line 153). Change the guard from requiring Admin to requiring only membership:

```csharp
[HttpGet("{serverId}/members/details")]
public async Task<ActionResult<List<ServerMemberDto>>> GetMembersWithRoles(int serverId)
{
    var caller = await db.ServerMembers.FindAsync(serverId, UserId);
    if (caller is null) return Forbid();

    return await db.ServerMembers
        .Where(sm => sm.ServerId == serverId)
        .Include(sm => sm.User)
        .Select(sm => new ServerMemberDto(sm.User.Id, sm.User.Username, sm.User.Status, sm.Role, sm.JoinedAt))
        .ToListAsync();
}
```

- [ ] **Step 5: Run tests to verify they pass**

```
dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~SetMemberRole_AdminPromotesNonAdmin|SetMemberRole_PromoteAlreadyAdmin|SetMemberRole_NonAdminCannotPromote|GetMembersWithRoles_AsRegularMember" -v
```

Expected: all four PASS.

- [ ] **Step 6: Run full test suite to catch regressions**

```
dotnet test FatGuysSpeak.Tests -v
```

Expected: all tests PASS.

- [ ] **Step 7: Commit**

```
git add FatGuysSpeak.Server/Controllers/ServersController.cs FatGuysSpeak.Tests/Server/ServersControllerTests.cs
git commit -m "Allow any Admin to promote users to Admin; members can read role list"
```

---

### Task 2: ApiService — add two new methods

**Files:**
- Modify: `FatGuysSpeak.Client/Services/ApiService.cs`

- [ ] **Step 1: Add `GetMemberRolesAsync` and `SetMemberRoleAsync`**

In `FatGuysSpeak.Client/Services/ApiService.cs`, after the `GetMembersAsync` method (~line 95), add:

```csharp
public Task<List<ServerMemberDto>?> GetMemberRolesAsync(int serverId) =>
    _http.GetFromJsonAsync<List<ServerMemberDto>>($"api/servers/{serverId}/members/details");

public async Task<bool> SetMemberRoleAsync(int serverId, int userId, ServerRole role)
{
    var resp = await _http.PutAsJsonAsync(
        $"api/servers/{serverId}/members/{userId}/role", new SetRoleRequest(role));
    return resp.IsSuccessStatusCode;
}
```

- [ ] **Step 2: Verify the project builds**

```
dotnet build FatGuysSpeak.Server --framework net9.0 -v q
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```
git add FatGuysSpeak.Client/Services/ApiService.cs
git commit -m "Add GetMemberRolesAsync and SetMemberRoleAsync to ApiService"
```

---

### Task 3: ChatHubService — add MemberRoleChanged event

**Files:**
- Modify: `FatGuysSpeak.Client/Services/ChatHubService.cs`

- [ ] **Step 1: Add event declaration**

In `FatGuysSpeak.Client/Services/ChatHubService.cs`, locate the block of `public event` declarations (~line 30-55). Add after the last event declaration:

```csharp
public event Action<int, string>? MemberRoleChanged;  // (userId, roleName e.g. "Admin")
```

- [ ] **Step 2: Register the SignalR handler in `ConnectAsync`**

In the same file, inside `ConnectAsync`, locate the block of `_connection.On<...>` calls. Add after the last one (before the Reconnecting/Reconnected/Closed handlers):

```csharp
_connection.On<int, string>("MemberRoleChanged",
    (uid, role) => MemberRoleChanged?.Invoke(uid, role));
```

- [ ] **Step 3: Commit**

```
git add FatGuysSpeak.Client/Services/ChatHubService.cs
git commit -m "Wire MemberRoleChanged SignalR event in ChatHubService"
```

---

### Task 4: MainViewModel — role map, event handler, and two commands

**Files:**
- Modify: `FatGuysSpeak.Client/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Add the role map field**

At the top of `MainViewModel`, in the private fields section near `_typingUsersInChannel` and `_blockedUserIds` (~line 111-112), add:

```csharp
private readonly Dictionary<int, ServerRole> _memberRoles = new();
```

- [ ] **Step 2: Load roles when a server is selected**

In `SelectServerAsync` (~line 527), after the line `Members = new ObservableCollection<UserDto>(onlineList);`, add:

```csharp
// Load role map for admin elevation UI; failure is non-fatal
_memberRoles.Clear();
try
{
    var roleList = await api.GetMemberRolesAsync(item.Server.Id);
    if (roleList is not null)
        foreach (var m in roleList)
            _memberRoles[m.UserId] = m.Role;
}
catch { /* role map unavailable; Make Admin will not appear */ }
```

- [ ] **Step 3: Subscribe to the hub event in `Initialize()`**

In `Initialize()` (~line 448), after the last `hub.xxx +=` line and before `hub.Reconnecting`, add:

```csharp
hub.MemberRoleChanged += OnMemberRoleChanged;
```

- [ ] **Step 4: Add the event handler method**

Add this private method anywhere in the class (near the other `On...` handlers):

```csharp
private void OnMemberRoleChanged(int userId, string roleName)
{
    if (!Enum.TryParse<ServerRole>(roleName, out var role)) return;
    _memberRoles[userId] = role;
    if (userId == api.CurrentUserId && SelectedServer is not null)
    {
        SelectedServer = SelectedServer with { MyRole = role };
        OnPropertyChanged(nameof(CurrentServerRole));
        OnPropertyChanged(nameof(IsServerAdmin));
        OnPropertyChanged(nameof(IsAdminOrModerator));
    }
}
```

- [ ] **Step 5: Add `ElevateToAdminCommand`**

Add after the handler method:

```csharp
[RelayCommand]
public async Task ElevateToAdminAsync(int userId)
{
    if (!IsServerAdmin || SelectedServer is null) return;
    if (_memberRoles.GetValueOrDefault(userId) == ServerRole.Admin) return;
    var ok = await api.SetMemberRoleAsync(SelectedServer.Id, userId, ServerRole.Admin);
    if (!ok)
        await toast.ShowAsync("Failed to update role.");
}
```

- [ ] **Step 6: Add `CreateChannelCommand`**

Add after `ElevateToAdminAsync`:

```csharp
[RelayCommand]
public async Task CreateChannelAsync()
{
    if (!IsServerAdmin || SelectedServer is null) return;
    var name = await Shell.Current.DisplayPromptAsync(
        "New Channel", "Enter channel name:", "Create", "Cancel");
    if (string.IsNullOrWhiteSpace(name)) return;
    var result = await api.CreateChannelAsync(
        SelectedServer.Id, new CreateChannelRequest(name.Trim(), ChannelType.Text));
    if (result is null)
        await toast.ShowAsync("Failed to create channel.");
    // ChannelCreated SignalR event already adds the channel to the list for all clients
}
```

- [ ] **Step 7: Build the client to verify**

```
dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0 -v q
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 8: Commit**

```
git add FatGuysSpeak.Client/ViewModels/MainViewModel.cs
git commit -m "Add role map, ElevateToAdminCommand, and CreateChannelCommand to MainViewModel"
```

---

### Task 5: MainPage.xaml — UI wiring

**Files:**
- Modify: `FatGuysSpeak.Client/Pages/MainPage.xaml`

- [ ] **Step 1: Add "Make Admin" to the connected users flyout**

Locate the existing `MenuFlyout` on the connected users list item (~line 219-228):

```xml
<FlyoutBase.ContextFlyout>
    <MenuFlyout>
        <MenuFlyoutItem Text="💬 Send Message"
            Command="..." CommandParameter="{Binding Id}" />
        <MenuFlyoutItem Text="🎤 Test My Mic (Loopback)"
            Command="..." />
    </MenuFlyout>
</FlyoutBase.ContextFlyout>
```

Add a third item inside the `MenuFlyout`, after the existing two:

```xml
<MenuFlyoutItem Text="👑 Make Admin"
    Command="{Binding Source={RelativeSource AncestorType={x:Type ContentPage}}, Path=BindingContext.ElevateToAdminCommand}"
    CommandParameter="{Binding Id}"
    IsEnabled="{Binding Source={RelativeSource AncestorType={x:Type ContentPage}}, Path=BindingContext.IsServerAdmin}" />
```

- [ ] **Step 2: Add flyout to voice strip participant items**

Locate the voice strip DataTemplate (~line 491-501):

```xml
<DataTemplate x:DataType="vm:VoiceParticipantViewModel">
    <HorizontalStackLayout Spacing="5" Padding="2,0">
        <Ellipse ... />
        <Label Text="{Binding Username}" ... />
    </HorizontalStackLayout>
</DataTemplate>
```

Add `FlyoutBase.ContextFlyout` as the first child of `HorizontalStackLayout`:

```xml
<DataTemplate x:DataType="vm:VoiceParticipantViewModel">
    <HorizontalStackLayout Spacing="5" Padding="2,0">
        <FlyoutBase.ContextFlyout>
            <MenuFlyout>
                <MenuFlyoutItem Text="👑 Make Admin"
                    Command="{Binding Source={RelativeSource AncestorType={x:Type ContentPage}}, Path=BindingContext.ElevateToAdminCommand}"
                    CommandParameter="{Binding UserId}"
                    IsEnabled="{Binding Source={RelativeSource AncestorType={x:Type ContentPage}}, Path=BindingContext.IsServerAdmin}" />
            </MenuFlyout>
        </FlyoutBase.ContextFlyout>
        <Ellipse WidthRequest="7" HeightRequest="7"
                 Fill="{Binding DotColor}"
                 VerticalOptions="Center" />
        <Label Text="{Binding Username}"
               TextColor="{Binding NameColor}"
               FontAttributes="{Binding NameFont}"
               FontSize="11" VerticalOptions="Center" />
    </HorizontalStackLayout>
</DataTemplate>
```

- [ ] **Step 3: Add "Create Channel" right-click to the channel list area**

Locate the `StackLayout` with `BindableLayout.ItemsSource="{Binding CategorizedChannels}"` (~line 248). Add `FlyoutBase.ContextFlyout` as the first child:

```xml
<StackLayout BindableLayout.ItemsSource="{Binding CategorizedChannels}" Spacing="0">
    <FlyoutBase.ContextFlyout>
        <MenuFlyout>
            <MenuFlyoutItem Text="+ Create Channel"
                Command="{Binding CreateChannelCommand}"
                IsEnabled="{Binding IsServerAdmin}" />
        </MenuFlyout>
    </FlyoutBase.ContextFlyout>
    <BindableLayout.ItemTemplate>
        ...
    </BindableLayout.ItemTemplate>
</StackLayout>
```

- [ ] **Step 4: Add "+" button for channel creation**

Locate the `Button Text="+ New Category"` (~line 341). Add a new button immediately before it:

```xml
<Button Text="+ New Channel"
        Command="{Binding CreateChannelCommand}"
        BackgroundColor="Transparent" TextColor="#555555"
        FontSize="11" HeightRequest="30" Padding="8,0"
        HorizontalOptions="Start"
        IsVisible="{Binding IsServerAdmin}"
        ToolTipProperties.Text="Create a new channel" />
<Button Text="+ New Category"
        Command="{Binding CreateCategoryPromptCommand}"
        ...existing attributes... />
```

- [ ] **Step 5: Build the client**

```
dotnet build FatGuysSpeak.Client --framework net9.0-windows10.0.19041.0 -v q
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Run the full test suite**

```
dotnet test FatGuysSpeak.Tests -v
```

Expected: all tests PASS.

- [ ] **Step 7: Commit**

```
git add FatGuysSpeak.Client/Pages/MainPage.xaml
git commit -m "Add Make Admin right-click and Create Channel button/right-click to UI"
```
