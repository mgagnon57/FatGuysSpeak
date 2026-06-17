# Dashboard User Profile Popout — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Clicking a username in the dashboard Users tab opens a centered modal with the user's identity, role, activity, and stats — including real cumulative online time.

**Architecture:** New `User.TotalOnlineSeconds` column accumulated by a unit-tested `OnlineTimeTracker` wired into `ChatHub` connect/disconnect (wall-clock, dedup overlapping connections). One aggregate `GET /api/admin/users/{id}/profile` endpoint feeds a CSP-clean modal injected by `dashboard.js`.

**Tech Stack:** ASP.NET Core 9, SignalR, EF Core (SQLite/PostgreSQL, no migrations — `EnsureCreated` + raw `ALTER TABLE`), xUnit, vanilla JS + the dashboard's `data-*` event delegation.

**Spec:** `docs/superpowers/specs/2026-06-16-dashboard-user-profile-design.md`

**Branch:** continue on `fix-dashboard-csp` (all dashboard work lives there).

---

## File Structure

- **Modify** `FatGuysSpeak.Server/Models/Entities.cs` — add `User.TotalOnlineSeconds`.
- **Modify** `FatGuysSpeak.Server/Program.cs` — raw `ALTER TABLE` for the column (both DB branches); register `OnlineTimeTracker` singleton.
- **Create** `FatGuysSpeak.Server/Services/OnlineTimeTracker.cs` — pure connection-time accounting.
- **Modify** `FatGuysSpeak.Server/Hubs/ChatHub.cs` — inject tracker; `Connect` on connect, `Disconnect`→persist on disconnect.
- **Modify** `FatGuysSpeak.Shared/DTOs.cs` — `UserProfileAdminDto`.
- **Modify** `FatGuysSpeak.Server/Controllers/AdminController.cs` — `GET users/{id}/profile`.
- **Modify** `FatGuysSpeak.Server/wwwroot/dashboard.js` — clickable username, `openProfile`/`closeProfile`, modal, delegation cases, Esc.
- **Modify** `FatGuysSpeak.Server/Controllers/MetricsController.cs` — modal CSS in the dashboard `<style>`.
- **Create** `FatGuysSpeak.Tests/Server/OnlineTimeTrackerTests.cs`, `FatGuysSpeak.Tests/Server/UserProfileEndpointTests.cs`.

Build/test commands:
- `dotnet build FatGuysSpeak.Server --framework net9.0`
- `dotnet test FatGuysSpeak.Tests`
- Windows build (dashboard runs here): `dotnet build FatGuysSpeak.Server --framework net9.0-windows10.0.19041.0`

---

## Task 1: Add User.TotalOnlineSeconds column

**Files:**
- Modify: `FatGuysSpeak.Server/Models/Entities.cs` (User class)
- Modify: `FatGuysSpeak.Server/Program.cs` (raw ALTER, both branches — next to the `LastSeenAt` ALTERs)

- [ ] **Step 1: Add the property**

In `FatGuysSpeak.Server/Models/Entities.cs`, in the `User` class, after `public DateTime? LastSeenAt { get; set; }`, add:

```csharp
    public long TotalOnlineSeconds { get; set; }
```

- [ ] **Step 2: Add the Postgres raw ALTER**

In `FatGuysSpeak.Server/Program.cs`, find the Postgres branch line that adds `LastSeenAt`:
```csharp
        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Users' AND column_name='LastSeenAt'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Users\" ADD COLUMN \"LastSeenAt\" TIMESTAMP");
```
Immediately after it, add:
```csharp
        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Users' AND column_name='TotalOnlineSeconds'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Users\" ADD COLUMN \"TotalOnlineSeconds\" BIGINT NOT NULL DEFAULT 0");
```

- [ ] **Step 3: Add the SQLite raw ALTER**

In the SQLite branch, find:
```csharp
        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Users') WHERE name='LastSeenAt'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Users ADD COLUMN LastSeenAt TEXT");
```
Immediately after it, add:
```csharp
        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Users') WHERE name='TotalOnlineSeconds'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Users ADD COLUMN TotalOnlineSeconds INTEGER NOT NULL DEFAULT 0");
```

- [ ] **Step 4: Build**

Run: `dotnet build FatGuysSpeak.Server --framework net9.0`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add FatGuysSpeak.Server/Models/Entities.cs FatGuysSpeak.Server/Program.cs
git commit -m "Add User.TotalOnlineSeconds column

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: OnlineTimeTracker (pure logic, TDD)

**Files:**
- Create: `FatGuysSpeak.Server/Services/OnlineTimeTracker.cs`
- Test: `FatGuysSpeak.Tests/Server/OnlineTimeTrackerTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `FatGuysSpeak.Tests/Server/OnlineTimeTrackerTests.cs`:

```csharp
using FatGuysSpeak.Server.Services;

namespace FatGuysSpeak.Tests.Server;

public class OnlineTimeTrackerTests
{
    [Fact]
    public void SingleSession_AddsElapsedSeconds()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = new OnlineTimeTracker(() => now);
        t.Connect(1);
        now = now.AddSeconds(90);
        Assert.Equal(90, t.Disconnect(1));
    }

    [Fact]
    public void OverlappingConnections_CountWallClockOnce()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = new OnlineTimeTracker(() => now);
        t.Connect(1);                 // online since t0
        now = now.AddSeconds(10);
        t.Connect(1);                 // second client; still since t0
        now = now.AddSeconds(10);
        Assert.Equal(0, t.Disconnect(1)); // one client left, still online
        now = now.AddSeconds(10);
        Assert.Equal(30, t.Disconnect(1)); // last client left: 30s wall clock
    }

    [Fact]
    public void UnmatchedDisconnect_ReturnsZero()
    {
        var t = new OnlineTimeTracker(() => DateTime.UtcNow);
        Assert.Equal(0, t.Disconnect(99));
    }

    [Fact]
    public void LiveSeconds_ReturnsInProgressWhenOnline_ZeroWhenOffline()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = new OnlineTimeTracker(() => now);
        Assert.Equal(0, t.LiveSeconds(1));
        t.Connect(1);
        now = now.AddSeconds(45);
        Assert.Equal(45, t.LiveSeconds(1));
        t.Disconnect(1);
        Assert.Equal(0, t.LiveSeconds(1));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~OnlineTimeTrackerTests"`
Expected: FAIL — `OnlineTimeTracker` does not exist.

- [ ] **Step 3: Implement**

Create `FatGuysSpeak.Server/Services/OnlineTimeTracker.cs`:

```csharp
namespace FatGuysSpeak.Server.Services;

/// <summary>
/// Tracks cumulative wall-clock online time per user. A user is "online" from their
/// first connection to their last disconnect; overlapping connections count once.
/// Thread-safe. Inject a clock for testing.
/// </summary>
public class OnlineTimeTracker(Func<DateTime>? clock = null)
{
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly Dictionary<int, (int Count, DateTime Since)> _sessions = new();
    private readonly object _lock = new();

    public void Connect(int userId)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(userId, out var s) && s.Count > 0)
                _sessions[userId] = (s.Count + 1, s.Since);
            else
                _sessions[userId] = (1, _now());
        }
    }

    /// <summary>Returns seconds to add to the user's total when their last connection drops; else 0.</summary>
    public long Disconnect(int userId)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(userId, out var s) || s.Count <= 0)
                return 0;
            if (s.Count > 1)
            {
                _sessions[userId] = (s.Count - 1, s.Since);
                return 0;
            }
            _sessions.Remove(userId);
            var secs = (long)(_now() - s.Since).TotalSeconds;
            return secs < 0 ? 0 : secs;
        }
    }

    /// <summary>Seconds of the in-progress session if currently online, else 0.</summary>
    public long LiveSeconds(int userId)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(userId, out var s) && s.Count > 0)
            {
                var secs = (long)(_now() - s.Since).TotalSeconds;
                return secs < 0 ? 0 : secs;
            }
            return 0;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~OnlineTimeTrackerTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add FatGuysSpeak.Server/Services/OnlineTimeTracker.cs FatGuysSpeak.Tests/Server/OnlineTimeTrackerTests.cs
git commit -m "Add OnlineTimeTracker (wall-clock online-time accounting)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: Register tracker + wire into ChatHub

**Files:**
- Modify: `FatGuysSpeak.Server/Program.cs` (DI, near the other singletons ~line 40)
- Modify: `FatGuysSpeak.Server/Hubs/ChatHub.cs` (constructor + OnConnected/OnDisconnected)

- [ ] **Step 1: Register the singleton**

In `FatGuysSpeak.Server/Program.cs`, after `builder.Services.AddSingleton<FatGuysSpeak.Server.Services.ServerMetricsService>();`, add:

```csharp
builder.Services.AddSingleton<FatGuysSpeak.Server.Services.OnlineTimeTracker>();
```

- [ ] **Step 2: Inject the tracker into ChatHub**

In `FatGuysSpeak.Server/Hubs/ChatHub.cs`, change the class declaration:
```csharp
public class ChatHub(AppDbContext db) : Hub
```
to:
```csharp
public class ChatHub(AppDbContext db, FatGuysSpeak.Server.Services.OnlineTimeTracker onlineTime) : Hub
```

- [ ] **Step 3: Record connect**

In `OnConnectedAsync`, immediately after `OnlineUsers[UserId] = Username;`, add:
```csharp
        onlineTime.Connect(UserId);
```

- [ ] **Step 4: Accumulate on disconnect**

In `OnDisconnectedAsync`, replace the block that sets the user offline:
```csharp
        var user = await db.Users.FindAsync(UserId);
        if (user is not null)
        {
            user.Status = UserStatus.Offline;
            user.LastSeenAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
```
with:
```csharp
        var addSeconds = onlineTime.Disconnect(UserId);
        var user = await db.Users.FindAsync(UserId);
        if (user is not null)
        {
            user.Status = UserStatus.Offline;
            user.LastSeenAt = DateTime.UtcNow;
            user.TotalOnlineSeconds += addSeconds;
            await db.SaveChangesAsync();
        }
```

- [ ] **Step 5: Build (both frameworks compile the hub)**

Run: `dotnet build FatGuysSpeak.Server --framework net9.0`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 6: Confirm hub tests still pass**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~ChatHub"`
Expected: PASS (the existing hub tests; they construct `ChatHub` via `TestHelpers` — if they call `new ChatHub(...)` directly they need the new arg; if so, update those call sites to pass `new OnlineTimeTracker()`).

- [ ] **Step 7: Commit**

```bash
git add FatGuysSpeak.Server/Program.cs FatGuysSpeak.Server/Hubs/ChatHub.cs FatGuysSpeak.Tests
git commit -m "Wire OnlineTimeTracker into ChatHub connect/disconnect

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: Profile DTO + endpoint (TDD)

**Files:**
- Modify: `FatGuysSpeak.Shared/DTOs.cs`
- Modify: `FatGuysSpeak.Server/Controllers/AdminController.cs`
- Test: `FatGuysSpeak.Tests/Server/UserProfileEndpointTests.cs`

- [ ] **Step 1: Add the DTO**

In `FatGuysSpeak.Shared/DTOs.cs`, add (near the other admin DTOs):

```csharp
public record UserProfileAdminDto(
    int Id, string Username, string Email, string? AvatarUrl, string? Bio,
    string Status, bool InVoice,
    DateTime CreatedAt, string Role, DateTime? MutedUntil, DateTime? TempBanExpiresAt,
    DateTime? LastLoginAt, string? LastLoginIp, string? LastLoginUserAgent,
    DateTime? LastSeenAt, int ActiveSessionCount,
    int MessageCount, string? TopChannel, long TotalOnlineSeconds);
```

- [ ] **Step 2: Write the failing tests**

Create `FatGuysSpeak.Tests/Server/UserProfileEndpointTests.cs`:

```csharp
using System.Net;
using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Tests.Server;

public class UserProfileEndpointTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly AdminController _controller;
    private readonly OnlineTimeTracker _tracker = new();

    public UserProfileEndpointTests()
    {
        _testDb = new TestDb();
        _controller = new AdminController(_testDb.Db, TestHelpers.MockHub(), new ServerMetricsService());
        // AdminController gates on a loopback RemoteIpAddress
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                Connection = { RemoteIpAddress = IPAddress.Loopback }
            }
        };
    }

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public async Task UnknownUser_Returns404()
    {
        var result = await _controller.GetUserProfile(999, _tracker, null);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ReturnsAggregateForSeededUser()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_testDb.Db, "owner");
        var user = new User { Username = "jane", Email = "jane@test.com", PasswordHash = "x",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), TotalOnlineSeconds = 3600 };
        _testDb.Db.Users.Add(user);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = user.Id, Role = ServerRole.Moderator });
        _testDb.Db.UserSessions.Add(new UserSession { UserId = user.Id, TokenHash = "h1", IpAddress = "1.2.3.4", UserAgent = "UA", CreatedAt = DateTime.UtcNow });
        var channel = _testDb.Db.Channels.First();
        _testDb.Db.Messages.Add(new Message { AuthorId = user.Id, ChannelId = channel.Id, Content = "hi" });
        await _testDb.Db.SaveChangesAsync();

        var result = await _controller.GetUserProfile(user.Id, _tracker, server.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<UserProfileAdminDto>(ok.Value);
        Assert.Equal("jane", dto.Username);
        Assert.Equal("Moderator", dto.Role);
        Assert.Equal(1, dto.MessageCount);
        Assert.Equal("1.2.3.4", dto.LastLoginIp);
        Assert.Equal(1, dto.ActiveSessionCount);
        Assert.Equal(3600, dto.TotalOnlineSeconds);
    }

    [Fact]
    public async Task NonLoopback_Forbidden()
    {
        _controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("8.8.8.8");
        var result = await _controller.GetUserProfile(1, _tracker, null);
        Assert.IsType<ForbidResult>(result);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~UserProfileEndpointTests"`
Expected: FAIL — `AdminController.GetUserProfile` does not exist.

- [ ] **Step 4: Implement the endpoint**

In `FatGuysSpeak.Server/Controllers/AdminController.cs`, add this method (after `GetUsers`):

```csharp
    [HttpGet("users/{id}/profile")]
    public async Task<IActionResult> GetUserProfile(int id, [FromServices] OnlineTimeTracker onlineTime, [FromQuery] int? serverId = null)
    {
        if (!IsLocal) return Forbid();

        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound();

        var voice = ChatHub.VoiceChannelSnapshot;

        var sid = serverId ?? await db.Servers.OrderBy(s => s.Id).Select(s => (int?)s.Id).FirstOrDefaultAsync();
        var member = sid is null ? null
            : await db.ServerMembers.FirstOrDefaultAsync(m => m.ServerId == sid && m.UserId == id);
        var tempBan = sid is null ? null
            : await db.TempBans.Where(tb => tb.ServerId == sid && tb.UserId == id && tb.ExpiresAt > DateTime.UtcNow)
                .OrderByDescending(tb => tb.ExpiresAt).FirstOrDefaultAsync();

        var lastSession = await db.UserSessions.Where(s => s.UserId == id)
            .OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync();
        var activeSessions = await db.UserSessions.CountAsync(s => s.UserId == id && s.RevokedAt == null);

        var messageCount = await db.Messages.CountAsync(m => m.AuthorId == id);
        var topChannelId = await db.Messages.Where(m => m.AuthorId == id)
            .GroupBy(m => m.ChannelId)
            .OrderByDescending(g => g.Count())
            .Select(g => (int?)g.Key)
            .FirstOrDefaultAsync();
        string? topChannel = topChannelId is null ? null
            : await db.Channels.Where(c => c.Id == topChannelId).Select(c => c.Name).FirstOrDefaultAsync();

        var dto = new FatGuysSpeak.Shared.UserProfileAdminDto(
            user.Id, user.Username, user.Email, user.AvatarUrl, user.Bio,
            user.Status.ToString(), voice.ContainsKey(id),
            user.CreatedAt, member?.Role.ToString() ?? "—", member?.MutedUntil, tempBan?.ExpiresAt,
            lastSession?.CreatedAt, lastSession?.IpAddress, lastSession?.UserAgent,
            user.LastSeenAt, activeSessions,
            messageCount, topChannel, user.TotalOnlineSeconds + onlineTime.LiveSeconds(id));
        return Ok(dto);
    }
```

Note: `OnlineTimeTracker` is in `FatGuysSpeak.Server.Services` (already imported via `using FatGuysSpeak.Server.Services;` at the top of AdminController). `ChatHub.VoiceChannelSnapshot` already exists and is used by other admin code.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~UserProfileEndpointTests"`
Expected: PASS (3 tests).

- [ ] **Step 6: Build, confirm zero warnings**

Run: `dotnet build FatGuysSpeak.Server --framework net9.0`

- [ ] **Step 7: Commit**

```bash
git add FatGuysSpeak.Shared/DTOs.cs FatGuysSpeak.Server/Controllers/AdminController.cs FatGuysSpeak.Tests/Server/UserProfileEndpointTests.cs
git commit -m "Add GET /api/admin/users/{id}/profile aggregate endpoint

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: Dashboard modal UI

No JS unit tests (consistent with the rest of the dashboard); verified in a headless browser in Task 6.

**Files:**
- Modify: `FatGuysSpeak.Server/wwwroot/dashboard.js`
- Modify: `FatGuysSpeak.Server/Controllers/MetricsController.cs` (modal CSS in the `<style>` block)

- [ ] **Step 1: Make the username clickable**

In `wwwroot/dashboard.js`, in `renderUsers`, change the username cell:
```javascript
              <td><strong style="color:#d0d0d0">${escapeHtml(u.username)}</strong></td>
```
to:
```javascript
              <td><span class="user-link" data-click="profile" data-uid="${u.id}">${escapeHtml(u.username)}</span></td>
```

- [ ] **Step 2: Add the profile click + close cases to the click delegation**

In `wwwroot/dashboard.js`, in the `document.addEventListener('click', ...)` switch, add these cases (next to `case 'tab':`):
```javascript
    case 'profile':       openProfile(+d.uid); break;
    case 'closeProfile':  closeProfile(); break;
```

- [ ] **Step 3: Add Esc-to-close to the keydown delegation**

In `wwwroot/dashboard.js`, in the `document.addEventListener('keydown', ...)` listener, add at the very top of the handler (before the `if (e.key !== 'Enter') return;` line):
```javascript
  if (e.key === 'Escape') { closeProfile(); return; }
```

- [ ] **Step 4: Add the profile modal functions**

In `wwwroot/dashboard.js`, add this block right after the `escapeHtml` function near the top:

```javascript
// ── User profile modal ────────────────────────────────────
function fmtDuration(secs) {
  secs = Math.max(0, Math.floor(secs || 0));
  const d = Math.floor(secs / 86400), h = Math.floor((secs % 86400) / 3600), m = Math.floor((secs % 3600) / 60);
  if (d) return `${d}d ${h}h ${m}m`;
  if (h) return `${h}h ${m}m`;
  if (m) return `${m}m`;
  return `${secs}s`;
}
function fmtWhen(iso) {
  if (!iso) return 'never';
  const t = new Date(iso);
  return t.toLocaleDateString() + ' ' + t.toLocaleTimeString();
}
function fmtAgo(iso) {
  if (!iso) return 'never';
  const s = Math.floor((Date.now() - new Date(iso).getTime()) / 1000);
  if (s < 60) return 'just now';
  if (s < 3600) return Math.floor(s / 60) + ' min ago';
  if (s < 86400) return Math.floor(s / 3600) + ' hr ago';
  return Math.floor(s / 86400) + ' days ago';
}

function closeProfile() {
  document.getElementById('profileModal')?.remove();
}

async function openProfile(userId) {
  closeProfile();
  let p = null;
  try {
    const q = currentServerId ? ('?serverId=' + currentServerId) : '';
    const res = await fetch('/api/admin/users/' + userId + '/profile' + q);
    if (res.ok) p = await res.json();
  } catch {}
  renderProfileModal(p);
}

function row(label, value) {
  return `<div class="pf-row"><span class="pf-label">${label}</span><span class="pf-val">${value}</span></div>`;
}

function renderProfileModal(p) {
  const wrap = document.createElement('div');
  wrap.id = 'profileModal';
  wrap.className = 'modal-backdrop';
  wrap.setAttribute('data-click', 'closeProfile');

  let body;
  if (!p) {
    body = `<div class="pf-section">Couldn't load this profile.</div>`;
  } else {
    const muted = p.mutedUntil && new Date(p.mutedUntil) > new Date();
    const banned = p.tempBanExpiresAt && new Date(p.tempBanExpiresAt) > new Date();
    body = `
      <div class="pf-section">
        ${row('Email', escapeHtml(p.email || '—'))}
        ${row('Status', escapeHtml(p.status) + (p.inVoice ? ' · 🎙 in voice' : ''))}
        ${p.bio ? row('Bio', escapeHtml(p.bio)) : ''}
      </div>
      <div class="pf-section">
        ${row('Role', escapeHtml(p.role))}
        ${row('Member since', fmtWhen(p.createdAt))}
        ${row('Moderation', muted ? ('Muted until ' + fmtWhen(p.mutedUntil)) : (banned ? ('Temp-banned until ' + fmtWhen(p.tempBanExpiresAt)) : 'None'))}
      </div>
      <div class="pf-section">
        ${row('Last login', p.lastLoginAt ? (fmtWhen(p.lastLoginAt) + (p.lastLoginIp ? ' · ' + escapeHtml(p.lastLoginIp) : '')) : 'never')}
        ${row('Last device', p.lastLoginUserAgent ? escapeHtml(p.lastLoginUserAgent) : '—')}
        ${row('Last seen', fmtAgo(p.lastSeenAt))}
        ${row('Active sessions', p.activeSessionCount)}
      </div>
      <div class="pf-section">
        ${row('Messages sent', p.messageCount)}
        ${row('Most-used channel', p.topChannel ? '#' + escapeHtml(p.topChannel) : '—')}
        ${row('Total time on server', fmtDuration(p.totalOnlineSeconds))}
      </div>`;
  }

  const title = p ? escapeHtml(p.username) : 'Profile';
  wrap.innerHTML = `
    <div class="modal-card">
      <div class="pf-head">
        <h2>${title}</h2>
        <button class="pf-close" data-click="closeProfile" title="Close" aria-label="Close">✕</button>
      </div>
      ${body}
    </div>`;
  document.body.appendChild(wrap);
}
```

Note: the backdrop has `data-click="closeProfile"`; the card sits inside it. Clicking the card bubbles to the backdrop too, so guard the close to only fire when the backdrop itself (or the × ) is the matched `[data-click]`. The delegation already uses `e.target.closest('[data-click]')`; clicks inside `.modal-card` (on non-`data-click` elements) resolve to the nearest `[data-click]` ancestor = the backdrop, which would close. To prevent closing when clicking inside the card, add `data-click=""` is not enough — instead stop propagation on the card. Add to `renderProfileModal` after building `wrap`:

```javascript
  wrap.querySelector('.modal-card').addEventListener('click', (e) => {
    if (!e.target.closest('[data-click="closeProfile"]')) e.stopPropagation();
  });
```

This lets the × (which has `data-click="closeProfile"`) work while clicks elsewhere in the card don't reach the backdrop.

- [ ] **Step 5: Add modal CSS**

In `FatGuysSpeak.Server/Controllers/MetricsController.cs`, inside the dashboard `<style>` block (just before `</style>`), add:

```css
          .user-link { color: #8ab4d4; cursor: pointer; }
          .user-link:hover { text-decoration: underline; }
          .modal-backdrop {
            position: fixed; inset: 0; background: rgba(0,0,0,.6);
            display: flex; align-items: center; justify-content: center; z-index: 9998;
          }
          .modal-card {
            background: #1f1f1f; border: 1px solid #333; border-radius: 10px;
            width: 460px; max-width: 92vw; max-height: 86vh; overflow-y: auto;
            box-shadow: 0 12px 40px rgba(0,0,0,.6); padding: 0 0 8px;
          }
          .pf-head {
            display: flex; justify-content: space-between; align-items: center;
            padding: 16px 18px; border-bottom: 1px solid #2a2a2a;
          }
          .pf-head h2 { font-size: 16px; color: #8ab4d4; }
          .pf-close {
            background: none; border: none; color: #777; font-size: 16px; cursor: pointer;
          }
          .pf-close:hover { color: #ed4245; }
          .pf-section { padding: 12px 18px; border-bottom: 1px solid #242424; }
          .pf-section:last-child { border-bottom: none; }
          .pf-row { display: flex; justify-content: space-between; gap: 16px; padding: 4px 0; font-size: 12px; }
          .pf-label { color: #666; text-transform: uppercase; letter-spacing: .5px; font-size: 10px; }
          .pf-val { color: #d0d0d0; text-align: right; word-break: break-word; }
```

- [ ] **Step 6: Syntax-check + build the dashboard**

Run: `node --check FatGuysSpeak.Server/wwwroot/dashboard.js` (expect: no output / exit 0)
Run: `dotnet build FatGuysSpeak.Server --framework net9.0-windows10.0.19041.0`
Expected: `dashboard.js` parses; Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add FatGuysSpeak.Server/wwwroot/dashboard.js FatGuysSpeak.Server/Controllers/MetricsController.cs
git commit -m "Add clickable user profile modal to the dashboard

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: Full verification

**Files:** none (verification only)

- [ ] **Step 1: Build both server frameworks**

Run: `dotnet build FatGuysSpeak.Server --framework net9.0`
Run: `dotnet build FatGuysSpeak.Server --framework net9.0-windows10.0.19041.0`
Expected: Build succeeded, 0 warnings, both.

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test FatGuysSpeak.Tests`
Expected: all pass — the existing suite plus 4 `OnlineTimeTracker` + 3 profile-endpoint tests.

- [ ] **Step 3: Headless browser check of the modal**

Stop running instances, rebuild net9.0-windows, relaunch via `launch.ps1`. Stage a same-origin test page (`wwwroot/_pftest.html` built from the dashboard markup + a `_pftest.js` that stubs `currentServerId`, calls `renderUsers` with a fake user, clicks the `data-click="profile"` username, and — since the real endpoint needs a real user — instead calls `renderProfileModal(<sample profile object>)` directly and asserts the modal appears with the fields, then clicks `data-click="closeProfile"` and asserts it's gone). Render with headless Chrome (`--virtual-time-budget`), screenshot, confirm the card shows the rows and closes. Remove `_pftest.*` after. (This mirrors the verification used for the temp-ban/mute dropdowns.)

- [ ] **Step 4: Final commit if anything was adjusted**

```bash
git add -A
git commit -m "Finalize dashboard user profile: builds + tests green

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review Notes

- **Spec coverage:** online-time column (Task 1); tracker + tests (Task 2); hub wiring (Task 3); aggregate endpoint + DTO + tests (Task 4); clickable username + modal + close (×/backdrop/Esc) + CSS, CSP-clean via delegation (Task 5); verification incl. headless modal check (Task 6). All spec fields present in `UserProfileAdminDto`. Caveats (forward-only, crash loses in-progress) are inherent to the design; surfaced via "Total time on server" being a plain accumulator.
- **Type consistency:** `OnlineTimeTracker.Connect/Disconnect/LiveSeconds` used identically in tests, hub, and endpoint. `UserProfileAdminDto` field names match the endpoint construction and the modal's `p.*` reads (`username`, `email`, `avatarUrl`, `bio`, `status`, `inVoice`, `createdAt`, `role`, `mutedUntil`, `tempBanExpiresAt`, `lastLoginAt`, `lastLoginIp`, `lastLoginUserAgent`, `lastSeenAt`, `activeSessionCount`, `messageCount`, `topChannel`, `totalOnlineSeconds` — JSON camelCases the record's PascalCase).
- **Out of scope:** periodic checkpointing; profile from Message Log/Audit; editing from the modal.
