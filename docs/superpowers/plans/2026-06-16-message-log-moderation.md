# Message Log Moderation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the dashboard's Message Log tab into a bulk-moderation console — content/date/server search, full-history paging, multi-select and criteria-based delete (soft default / guarded hard), restore, CSV export, and author→profile jump.

**Architecture:** All server logic lives in `AdminController` (loopback + `DashboardAdmin` gated), projecting to new named DTOs in `FatGuysSpeak.Shared`. A shared `ApplyMessageFilters` helper backs both the GET list and the criteria-based delete/restore so filter semantics never diverge. Soft-delete stops overwriting message content (the normal message API already hides `IsDeleted` rows), which makes Restore possible. UI changes are CSP-clean: external `dashboard.js` + `data-*` delegation, no inline handlers.

**Tech Stack:** ASP.NET Core 9 Web API, EF Core 9 (SQLite local / Postgres prod), xUnit + in-memory SQLite (`TestDb`), vanilla JS dashboard served under strict CSP, WebView2 WPF host.

**Spec:** `docs/superpowers/specs/2026-06-16-message-log-moderation-design.md`

**Conventions for every task:**
- Server build (headless, for tests): `dotnet build FatGuysSpeak.Server --framework net9.0`
- Run a test class: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~MessageLogModerationTests"`
- UI verification requires the Windows build + relaunch:
  - Stop running instances (PowerShell): `Get-Process FatGuysSpeak.Server,FatGuysSpeak.Client -ErrorAction SilentlyContinue | Stop-Process -Force`
  - `dotnet build FatGuysSpeak.Server --framework net9.0-windows10.0.19041.0`
  - `& C:\FatGuysSpeak\launch.ps1` (launches server + 2 clients)
  - Dashboard: `http://localhost:5238/dashboard`, login `admin` / `ChangeMe2026!`
- After editing `dashboard.js`, syntax-check: `node --check FatGuysSpeak.Server/wwwroot/dashboard.js`

---

## PHASE 1 — Foundation & low-risk wins

### Task 1: Typed message DTO + AuthorId + soft-delete preserves content

**Files:**
- Modify: `FatGuysSpeak.Shared/DTOs.cs`
- Modify: `FatGuysSpeak.Server/Controllers/AdminController.cs` (`GetMessages` ~125-163, `DeleteMessage` ~172-198)
- Create: `FatGuysSpeak.Tests/Server/MessageLogModerationTests.cs`

- [ ] **Step 1: Add the DTO**

In `FatGuysSpeak.Shared/DTOs.cs`, add near the other admin DTOs:

```csharp
public record AdminMessageDto(
    int Id, string Content, int AuthorId, string Author, string Channel,
    string Server, string Source, DateTime CreatedAt, bool IsDeleted);
```

- [ ] **Step 2: Write the failing test**

Create `FatGuysSpeak.Tests/Server/MessageLogModerationTests.cs`:

```csharp
using System.Net;
using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Tests.Server;

public class MessageLogModerationTests : IDisposable
{
    private readonly TestDb _db;
    private readonly AdminController _c;

    public MessageLogModerationTests()
    {
        _db = new TestDb();
        _c = new AdminController(_db.Db, TestHelpers.MockHub(), new ServerMetricsService());
        _c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Connection = { RemoteIpAddress = IPAddress.Loopback } }
        };
    }

    public void Dispose() => _db.Dispose();

    // Adds a message to the first seeded channel and returns it.
    private async Task<Message> AddMsg(int authorId, string content, int? channelId = null,
        MessageSource source = MessageSource.Text, DateTime? when = null)
    {
        var cid = channelId ?? _db.Db.Channels.First().Id;
        var m = new Message { AuthorId = authorId, ChannelId = cid, Content = content,
            Source = source, CreatedAt = when ?? DateTime.UtcNow };
        _db.Db.Messages.Add(m);
        await _db.Db.SaveChangesAsync();
        return m;
    }

    [Fact]
    public async Task GetMessages_IncludesAuthorId()
    {
        var (_, admin) = await TestHelpers.SeedServerAsync(_db.Db, "owner");
        await AddMsg(admin.Id, "hello");

        var ok = Assert.IsType<OkObjectResult>(await _c.GetMessages());
        var list = Assert.IsType<List<AdminMessageDto>>(ok.Value);
        Assert.Single(list);
        Assert.Equal(admin.Id, list[0].AuthorId);
        Assert.Equal("hello", list[0].Content);
    }

    [Fact]
    public async Task DeleteMessage_SoftDeletes_AndPreservesContent()
    {
        var (_, admin) = await TestHelpers.SeedServerAsync(_db.Db, "owner");
        var m = await AddMsg(admin.Id, "keep this text");

        var res = await _c.DeleteMessage(m.Id);
        Assert.IsType<NoContentResult>(res);

        var reloaded = await _db.Db.Messages.AsNoTracking().FirstAsync(x => x.Id == m.Id);
        Assert.True(reloaded.IsDeleted);
        Assert.Equal("keep this text", reloaded.Content); // content NOT overwritten
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~MessageLogModerationTests"`
Expected: FAIL — `GetMessages` returns an anonymous list (not `List<AdminMessageDto>`); `DeleteMessage` overwrites content with `[deleted by admin]`.

- [ ] **Step 4: Update `GetMessages` projection**

In `AdminController.GetMessages`, replace the `.Select(...)` projection block (the anonymous `new { ... }`) with the typed DTO and add `AuthorId`:

```csharp
        var messages = await query
            .OrderByDescending(m => m.Id)
            .Take(Math.Min(limit, 500))
            .Select(m => new AdminMessageDto(
                m.Id, m.Content, m.AuthorId, m.Author.Username,
                m.Channel.Name, m.Channel.Server.Name, m.Source.ToString(),
                m.CreatedAt, m.IsDeleted))
            .ToListAsync();

        return Ok(messages);
```

(Ordering changes from `CreatedAt` to `Id` descending — equivalent newest-first order, and a stable cursor for Task 4's pagination.)

- [ ] **Step 5: Stop overwriting content on soft-delete**

In `AdminController.DeleteMessage`, delete the content-overwrite line so it reads:

```csharp
        msg.IsDeleted = true;
        db.AuditLogs.Add(new FatGuysSpeak.Server.Models.AuditLog
```

(Remove only the line `msg.Content = "[deleted by admin]";`. Everything else in the method is unchanged.)

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~MessageLogModerationTests"`
Expected: PASS (2 tests).

- [ ] **Step 7: Commit**

```bash
git add FatGuysSpeak.Shared/DTOs.cs FatGuysSpeak.Server/Controllers/AdminController.cs FatGuysSpeak.Tests/Server/MessageLogModerationTests.cs
git commit -m "Message log: typed DTO + AuthorId; soft-delete preserves content"
```

---

### Task 2: Restore endpoint (ids or filter)

**Files:**
- Modify: `FatGuysSpeak.Shared/DTOs.cs`
- Modify: `FatGuysSpeak.Server/Controllers/AdminController.cs`
- Modify: `FatGuysSpeak.Tests/Server/MessageLogModerationTests.cs`

- [ ] **Step 1: Add request/result DTOs**

In `FatGuysSpeak.Shared/DTOs.cs`, add:

```csharp
public record MessageFilterDto(
    string? Author = null, string? Channel = null, int? ServerId = null,
    string? Source = null, string? Keyword = null,
    DateTime? From = null, DateTime? To = null);

public record BulkRestoreRequest(int[]? Ids = null, MessageFilterDto? Filter = null);
public record BulkActionResult(int Affected, int[] ChannelIds);
```

- [ ] **Step 2: Write the failing test**

Add to `MessageLogModerationTests`:

```csharp
    [Fact]
    public async Task Restore_ByIds_UnflagsAndKeepsContent()
    {
        var (_, admin) = await TestHelpers.SeedServerAsync(_db.Db, "owner");
        var m = await AddMsg(admin.Id, "original text");
        await _c.DeleteMessage(m.Id); // soft-delete (content preserved)

        var res = await _c.BulkRestore(new BulkRestoreRequest(Ids: new[] { m.Id }));
        var ok = Assert.IsType<OkObjectResult>(res);
        var result = Assert.IsType<BulkActionResult>(ok.Value);
        Assert.Equal(1, result.Affected);

        var reloaded = await _db.Db.Messages.AsNoTracking().FirstAsync(x => x.Id == m.Id);
        Assert.False(reloaded.IsDeleted);
        Assert.Equal("original text", reloaded.Content);
    }

    [Fact]
    public async Task Restore_RejectsBothIdsAndFilter()
    {
        var res = await _c.BulkRestore(new BulkRestoreRequest(
            Ids: new[] { 1 }, Filter: new MessageFilterDto(Author: "x")));
        Assert.IsType<BadRequestObjectResult>(res);
    }
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~MessageLogModerationTests"`
Expected: FAIL — `BulkRestore` does not exist.

- [ ] **Step 4: Add the shared filter helper**

In `AdminController`, add this private static helper (used here and reused in Tasks 4 and 7):

```csharp
    private static IQueryable<Message> ApplyMessageFilters(IQueryable<Message> q, FatGuysSpeak.Shared.MessageFilterDto f)
    {
        if (!string.IsNullOrWhiteSpace(f.Author))
            q = q.Where(m => m.Author.Username.ToLower().Contains(f.Author.ToLower()));
        if (!string.IsNullOrWhiteSpace(f.Channel))
            q = q.Where(m => m.Channel.Name.ToLower().Contains(f.Channel.ToLower()));
        if (f.ServerId is int sid)
            q = q.Where(m => m.Channel.ServerId == sid);
        if (!string.IsNullOrWhiteSpace(f.Source) &&
            Enum.TryParse<FatGuysSpeak.Shared.MessageSource>(f.Source, true, out var src))
            q = q.Where(m => m.Source == src);
        if (!string.IsNullOrWhiteSpace(f.Keyword))
            q = q.Where(m => m.Content.ToLower().Contains(f.Keyword.ToLower()));
        if (f.From is DateTime from)
            q = q.Where(m => m.CreatedAt >= from);
        if (f.To is DateTime to)
            q = q.Where(m => m.CreatedAt <= to);
        return q;
    }
```

- [ ] **Step 5: Add the restore endpoint**

In `AdminController`, add:

```csharp
    [HttpPost("messages/restore")]
    public async Task<IActionResult> BulkRestore(FatGuysSpeak.Shared.BulkRestoreRequest req)
    {
        if (!IsLocal) return Forbid();

        var hasIds = req.Ids is { Length: > 0 };
        var hasFilter = req.Filter is not null;
        if (hasIds == hasFilter) return BadRequest("Provide exactly one of ids or filter.");

        var q = db.Messages.Include(m => m.Channel).Where(m => m.IsDeleted);
        q = hasIds ? q.Where(m => req.Ids!.Contains(m.Id)) : ApplyMessageFilters(q, req.Filter!);

        var matched = await q.ToListAsync();
        foreach (var m in matched) m.IsDeleted = false;

        if (matched.Count > 0)
            db.AuditLogs.Add(new FatGuysSpeak.Server.Models.AuditLog
            {
                ServerId = matched[0].Channel.ServerId,
                ActorId = 0, ActorUsername = "admin",
                Action = "MessagesRestored", TargetId = 0, TargetUsername = "",
                Detail = $"Restored {matched.Count} message(s)."
            });
        await db.SaveChangesAsync();

        var channelIds = matched.Select(m => m.ChannelId).Distinct().ToArray();
        return Ok(new FatGuysSpeak.Shared.BulkActionResult(matched.Count, channelIds));
    }
```

(Restore is database-authoritative; live clients see restored messages on next channel load — no re-inject broadcast, per spec.)

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~MessageLogModerationTests"`
Expected: PASS (4 tests).

- [ ] **Step 7: Commit**

```bash
git add FatGuysSpeak.Shared/DTOs.cs FatGuysSpeak.Server/Controllers/AdminController.cs FatGuysSpeak.Tests/Server/MessageLogModerationTests.cs
git commit -m "Message log: add restore endpoint + shared message-filter helper"
```

---

### Task 3: Dashboard Phase-1 UI — restore, expand content, author link, CSV export

**Files:**
- Modify: `FatGuysSpeak.Server/wwwroot/dashboard.js` (`renderMessages` ~578-603, click switch ~899-926, header ~347-364)
- Modify: `FatGuysSpeak.Server/Controllers/MetricsController.cs` (messages tab ~345-381)

- [ ] **Step 1: Add an Export CSV button to the messages tab header**

In `MetricsController.cs`, inside the messages-tab `panel-header` `<div style="display:flex;...">`, after the "Show deleted" `<label>...</label>` block, add:

```html
              <button class="btn-sm" id="msgExportCsv" data-click="exportMsgCsv" title="Download the currently shown rows as a CSV file">Export CSV</button>
```

- [ ] **Step 2: Replace `renderMessages` to add restore + clickable author + expandable content**

In `dashboard.js`, replace the whole `renderMessages` function with:

```javascript
let lastRenderedMsgs = [];
function renderMessages(msgs) {
  lastRenderedMsgs = msgs;
  const tbody = document.getElementById('msgTableBody');
  if (!msgs.length) {
    tbody.innerHTML = '<tr><td colspan="7" style="color:#444;padding:20px 10px;">No messages found.</td></tr>';
    return;
  }
  tbody.innerHTML = msgs.map(m => {
    const ts = new Date(m.createdAt);
    const timeStr = ts.toLocaleDateString() + ' ' + ts.toLocaleTimeString();
    const deleted = m.isDeleted;
    const contentStyle = deleted ? 'color:#555;font-style:italic' : 'color:#c0c0c0';
    const full = m.content || '';
    const truncated = full.length > 120;
    const shown = truncated ? full.slice(0, 120) + '…' : full;
    const contentCell = truncated
      ? `<span class="msg-content" data-click="expandMsg" data-mid="${m.id}" style="cursor:pointer" title="Click to expand">${escapeHtml(shown)}</span>`
      : escapeHtml(shown);
    const action = deleted
      ? `<button class="btn-sm" title="Restore this message — un-hide it (content was preserved)" data-click="restoreMsg" data-mid="${m.id}">Restore</button>`
      : `<button class="btn-sm danger" title="Soft-delete this message — hidden from clients but kept in the database" data-click="delMsg" data-mid="${m.id}">Delete</button>`;
    return `<tr data-mid="${m.id}" style="${deleted ? 'opacity:.55' : ''}">
      <td style="color:#555;font-size:11px;white-space:nowrap">${timeStr}</td>
      <td><span class="user-link" data-click="profile" data-uid="${m.authorId}" style="color:#8ab4d4;font-weight:500;cursor:pointer">${escapeHtml(m.author)}</span></td>
      <td style="color:#666">#${escapeHtml(m.channel)}</td>
      <td style="color:#555;font-size:11px">${escapeHtml(m.server)}</td>
      <td>${sourceBadge(m.source)}</td>
      <td style="${contentStyle}">${contentCell}</td>
      <td>${action}</td>
    </tr>`;
  }).join('');
}
```

- [ ] **Step 3: Add the restore / expand / CSV handlers**

In `dashboard.js`, after `adminDeleteMsg` (~618), add:

```javascript
async function adminRestoreMsg(msgId, btn) {
  btn.disabled = true; btn.textContent = '…';
  try {
    const res = await fetch('/api/admin/messages/restore', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ids: [msgId] })
    });
    if (!res.ok) throw new Error(res.status);
    loadMessages();
  } catch (e) { btn.disabled = false; btn.textContent = 'Restore'; alert('Restore failed: ' + e.message); }
}

function expandMsg(msgId, el) {
  const m = lastRenderedMsgs.find(x => x.id === msgId);
  if (!m) return;
  el.textContent = m.content || '';
  el.removeAttribute('data-click');
  el.style.cursor = 'default';
  el.title = '';
}

function exportMsgCsv() {
  if (!lastRenderedMsgs.length) { alert('Nothing to export.'); return; }
  const esc = v => `"${String(v ?? '').replace(/"/g, '""')}"`;
  const header = ['Time', 'Author', 'Channel', 'Server', 'Source', 'Content', 'Deleted'];
  const rows = lastRenderedMsgs.map(m => [
    new Date(m.createdAt).toISOString(), m.author, m.channel, m.server,
    m.source, m.content, m.isDeleted ? 'yes' : 'no'
  ].map(esc).join(','));
  const blob = new Blob([header.join(',') + '\n' + rows.join('\n')], { type: 'text/csv' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = `messages-${new Date().toISOString().slice(0, 10)}.csv`;
  a.click();
  URL.revokeObjectURL(a.href);
}
```

- [ ] **Step 4: Wire the new `data-click` cases**

In `dashboard.js`, in the `click` delegation `switch (d.click)` block (~903), add these cases next to `case 'delMsg':`:

```javascript
    case 'restoreMsg':    adminRestoreMsg(+d.mid, el); break;
    case 'expandMsg':     expandMsg(+d.mid, el); break;
    case 'exportMsgCsv':  exportMsgCsv(); break;
```

- [ ] **Step 5: Syntax-check the JS**

Run: `node --check FatGuysSpeak.Server/wwwroot/dashboard.js`
Expected: no output (valid).

- [ ] **Step 6: Build, relaunch, and verify in the dashboard**

```
Get-Process FatGuysSpeak.Server,FatGuysSpeak.Client -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build FatGuysSpeak.Server --framework net9.0-windows10.0.19041.0
& C:\FatGuysSpeak\launch.ps1
```
Open `http://localhost:5238/dashboard`, login `admin`/`ChangeMe2026!`, go to Message Log. Verify:
- An author name is a blue link; clicking opens the profile modal.
- A long message shows "…"; clicking the content expands to full text.
- Delete a message, tick "Show deleted" → the row shows a "Restore" button; clicking it un-hides the message and its original text is intact (not "[deleted by admin]").
- "Export CSV" downloads a CSV of the current rows.

- [ ] **Step 7: Commit**

```bash
git add FatGuysSpeak.Server/wwwroot/dashboard.js FatGuysSpeak.Server/Controllers/MetricsController.cs
git commit -m "Message log UI: restore button, expandable content, author link, CSV export"
```

---

## PHASE 2 — Discovery (search / filter / paging)

### Task 4: GET messages — keyword, serverId, date range, beforeId cursor

**Files:**
- Modify: `FatGuysSpeak.Server/Controllers/AdminController.cs` (`GetMessages`)
- Modify: `FatGuysSpeak.Tests/Server/MessageLogModerationTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `MessageLogModerationTests`:

```csharp
    [Fact]
    public async Task GetMessages_KeywordFiltersContent()
    {
        var (_, admin) = await TestHelpers.SeedServerAsync(_db.Db, "owner");
        await AddMsg(admin.Id, "hello world");
        await AddMsg(admin.Id, "goodbye");

        var ok = Assert.IsType<OkObjectResult>(await _c.GetMessages(keyword: "WORLD"));
        var list = Assert.IsType<List<AdminMessageDto>>(ok.Value);
        Assert.Single(list);
        Assert.Equal("hello world", list[0].Content);
    }

    [Fact]
    public async Task GetMessages_DateRangeIsInclusive()
    {
        var (_, admin) = await TestHelpers.SeedServerAsync(_db.Db, "owner");
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await AddMsg(admin.Id, "january", when: t1);
        await AddMsg(admin.Id, "june", when: t2);

        var ok = Assert.IsType<OkObjectResult>(await _c.GetMessages(
            from: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), to: t2));
        var list = Assert.IsType<List<AdminMessageDto>>(ok.Value);
        Assert.Single(list);
        Assert.Equal("june", list[0].Content);
    }

    [Fact]
    public async Task GetMessages_BeforeIdReturnsOlderPage()
    {
        var (_, admin) = await TestHelpers.SeedServerAsync(_db.Db, "owner");
        var a = await AddMsg(admin.Id, "first");
        var b = await AddMsg(admin.Id, "second");
        var c = await AddMsg(admin.Id, "third");

        // Newest-first page of size 1 -> "third".
        var page1 = Assert.IsType<List<AdminMessageDto>>(
            ((OkObjectResult)await _c.GetMessages(limit: 1)).Value);
        Assert.Equal(c.Id, page1[0].Id);

        // Next page (older than "third") -> "second".
        var page2 = Assert.IsType<List<AdminMessageDto>>(
            ((OkObjectResult)await _c.GetMessages(limit: 1, beforeId: c.Id)).Value);
        Assert.Equal(b.Id, page2[0].Id);
        Assert.DoesNotContain(page2, x => x.Id == c.Id);
        _ = a;
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~MessageLogModerationTests"`
Expected: FAIL — `GetMessages` has no `keyword`/`from`/`to`/`beforeId` parameters (compile error / wrong results).

- [ ] **Step 3: Rewrite `GetMessages` to use the shared filter helper + new params**

Replace the entire `GetMessages` method body with:

```csharp
    [HttpGet("messages")]
    public async Task<IActionResult> GetMessages(
        [FromQuery] int limit = 100,
        [FromQuery] string? author = null,
        [FromQuery] string? channel = null,
        [FromQuery] string? source = null,
        [FromQuery] int? serverId = null,
        [FromQuery] string? keyword = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int? beforeId = null)
    {
        if (!IsLocal) return Forbid();

        var query = db.Messages
            .Include(m => m.Author)
            .Include(m => m.Channel).ThenInclude(c => c.Server)
            .AsQueryable();

        query = ApplyMessageFilters(query,
            new FatGuysSpeak.Shared.MessageFilterDto(author, channel, serverId, source, keyword, from, to));
        if (beforeId is int bid) query = query.Where(m => m.Id < bid);

        var messages = await query
            .OrderByDescending(m => m.Id)
            .Take(Math.Min(limit, 500))
            .Select(m => new FatGuysSpeak.Shared.AdminMessageDto(
                m.Id, m.Content, m.AuthorId, m.Author.Username,
                m.Channel.Name, m.Channel.Server.Name, m.Source.ToString(),
                m.CreatedAt, m.IsDeleted))
            .ToListAsync();

        return Ok(messages);
    }
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~MessageLogModerationTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add FatGuysSpeak.Server/Controllers/AdminController.cs FatGuysSpeak.Tests/Server/MessageLogModerationTests.cs
git commit -m "Message log: keyword/server/date-range filters + beforeId pagination"
```

---

### Task 5: GET /api/admin/servers

**Files:**
- Modify: `FatGuysSpeak.Shared/DTOs.cs`
- Modify: `FatGuysSpeak.Server/Controllers/AdminController.cs`
- Modify: `FatGuysSpeak.Tests/Server/MessageLogModerationTests.cs`

- [ ] **Step 1: Add the DTO**

In `FatGuysSpeak.Shared/DTOs.cs`, add:

```csharp
public record AdminServerDto(int Id, string Name);
```

- [ ] **Step 2: Write the failing test**

Add to `MessageLogModerationTests`:

```csharp
    [Fact]
    public async Task GetServers_ReturnsSeededServer()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_db.Db, "owner");
        var ok = Assert.IsType<OkObjectResult>(await _c.GetServers());
        var list = Assert.IsType<List<AdminServerDto>>(ok.Value);
        Assert.Contains(list, s => s.Id == server.Id);
    }
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~MessageLogModerationTests"`
Expected: FAIL — `GetServers` does not exist.

- [ ] **Step 4: Add the endpoint**

In `AdminController`, add:

```csharp
    [HttpGet("servers")]
    public async Task<IActionResult> GetServers()
    {
        if (!IsLocal) return Forbid();
        var servers = await db.Servers
            .OrderBy(s => s.Id)
            .Select(s => new FatGuysSpeak.Shared.AdminServerDto(s.Id, s.Name))
            .ToListAsync();
        return Ok(servers);
    }
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~MessageLogModerationTests"`
Expected: PASS (8 tests).

- [ ] **Step 6: Commit**

```bash
git add FatGuysSpeak.Shared/DTOs.cs FatGuysSpeak.Server/Controllers/AdminController.cs FatGuysSpeak.Tests/Server/MessageLogModerationTests.cs
git commit -m "Message log: add GET /api/admin/servers for filter dropdown"
```

---

### Task 6: Dashboard Phase-2 UI — keyword, date range, server dropdown, Load more

**Files:**
- Modify: `FatGuysSpeak.Server/Controllers/MetricsController.cs` (messages tab header + table)
- Modify: `FatGuysSpeak.Server/wwwroot/dashboard.js` (`loadMessages`, init)

- [ ] **Step 1: Add the new filter controls + Load-more row to the HTML**

In `MetricsController.cs`, inside the messages-tab filter `<div style="display:flex;gap:8px;align-items:center">`, after the `msgChannel` input, add a keyword box:

```html
              <input class="search-box" id="msgKeyword" placeholder="Search text…" data-input="loadMessages" style="width:140px" title="Search within message content" />
```

After the `msgSource` `<select>...</select>`, add the server dropdown and date-range preset:

```html
              <select id="msgServer" data-change="loadMessages" title="Filter by server"
                style="background:#252525;border:1px solid #333;border-radius:4px;color:#d0d0d0;font-size:11px;padding:5px 8px;font-family:inherit;">
                <option value="">All servers</option>
              </select>
              <select id="msgRange" data-change="loadMessages" title="Limit to a recent time window"
                style="background:#252525;border:1px solid #333;border-radius:4px;color:#d0d0d0;font-size:11px;padding:5px 8px;font-family:inherit;">
                <option value="">Any time</option>
                <option value="1">Last 24h</option>
                <option value="7">Last 7 days</option>
                <option value="30">Last 30 days</option>
              </select>
```

After the closing `</table>` of the messages tab (before `</div><!-- /tab-messages -->`), add a Load-more row:

```html
          <div style="text-align:center;padding:10px">
            <button class="btn-sm" id="msgLoadMore" data-click="loadMoreMsgs" style="display:none">Load more</button>
          </div>
```

- [ ] **Step 2: Rewrite `loadMessages` to send new params and support paging**

In `dashboard.js`, replace the whole `loadMessages` function (the `let msgDebounce` block stays) with:

```javascript
let msgDebounce = null;
let oldestMsgId = null;     // cursor for "load more"
let allLoadedMsgs = [];     // accumulated across pages

function buildMsgParams(beforeId) {
  const author  = document.getElementById('msgAuthor').value.trim();
  const channel = document.getElementById('msgChannel').value.trim();
  const keyword = document.getElementById('msgKeyword').value.trim();
  const source  = document.getElementById('msgSource').value;
  const server  = document.getElementById('msgServer').value;
  const range   = document.getElementById('msgRange').value;
  const params = new URLSearchParams({ limit: 100 });
  if (author)  params.set('author',  author);
  if (channel) params.set('channel', channel);
  if (keyword) params.set('keyword', keyword);
  if (source)  params.set('source',  source);
  if (server)  params.set('serverId', server);
  if (range) {
    const from = new Date(Date.now() - (+range) * 86400000).toISOString();
    params.set('from', from);
  }
  if (beforeId) params.set('beforeId', beforeId);
  return params;
}

async function loadMessages() {
  clearTimeout(msgDebounce);
  msgDebounce = setTimeout(async () => {
    const showDel = document.getElementById('msgShowDeleted').checked;
    try {
      const res = await fetch('/api/admin/messages?' + buildMsgParams(null));
      if (!res.ok) throw new Error(res.status);
      let msgs = await res.json();
      if (!showDel) msgs = msgs.filter(m => !m.isDeleted);
      allLoadedMsgs = msgs;
      oldestMsgId = msgs.length ? msgs[msgs.length - 1].id : null;
      document.getElementById('msgLoadMore').style.display = (msgs.length >= 100) ? '' : 'none';
      renderMessages(allLoadedMsgs);
    } catch (e) {
      document.getElementById('msgTableBody').innerHTML =
        `<tr><td colspan="7" style="color:#ed4245;padding:20px 10px;">Failed: ${e.message}</td></tr>`;
    }
  }, 250);
}

async function loadMoreMsgs() {
  if (!oldestMsgId) return;
  const showDel = document.getElementById('msgShowDeleted').checked;
  try {
    const res = await fetch('/api/admin/messages?' + buildMsgParams(oldestMsgId));
    if (!res.ok) throw new Error(res.status);
    let msgs = await res.json();
    if (!showDel) msgs = msgs.filter(m => !m.isDeleted);
    allLoadedMsgs = allLoadedMsgs.concat(msgs);
    oldestMsgId = msgs.length ? msgs[msgs.length - 1].id : oldestMsgId;
    document.getElementById('msgLoadMore').style.display = (msgs.length >= 100) ? '' : 'none';
    renderMessages(allLoadedMsgs);
  } catch (e) { alert('Load more failed: ' + e.message); }
}

async function loadServerOptions() {
  try {
    const res = await fetch('/api/admin/servers');
    if (!res.ok) return;
    const servers = await res.json();
    const sel = document.getElementById('msgServer');
    sel.innerHTML = '<option value="">All servers</option>' +
      servers.map(s => `<option value="${s.id}">${escapeHtml(s.name)}</option>`).join('');
  } catch { /* dropdown stays "All servers" */ }
}
```

(Note: `renderMessages` from Task 3 already assigns `lastRenderedMsgs = msgs`, so CSV/expand operate on the full accumulated set.)

- [ ] **Step 3: Load server options once at startup**

In `dashboard.js`, in the bottom IIFE `(async () => { await initServer(); ... })();` (~884), add `loadServerOptions();` after `await initServer();`.

- [ ] **Step 4: Wire `loadMoreMsgs`**

In the `click` delegation `switch (d.click)`, add:

```javascript
    case 'loadMoreMsgs':  loadMoreMsgs(); break;
```

- [ ] **Step 5: Syntax-check**

Run: `node --check FatGuysSpeak.Server/wwwroot/dashboard.js`
Expected: no output.

- [ ] **Step 6: Build, relaunch, verify**

Rebuild Windows + relaunch (see conventions). In Message Log verify:
- Typing in "Search text…" narrows to messages whose content contains the term.
- The server dropdown lists the seeded server and filtering by it works.
- Selecting "Last 24h"/"Last 7 days" narrows by time.
- When ≥100 rows return, "Load more" appears and appends the next older page (no duplicates).

- [ ] **Step 7: Commit**

```bash
git add FatGuysSpeak.Server/Controllers/MetricsController.cs FatGuysSpeak.Server/wwwroot/dashboard.js
git commit -m "Message log UI: keyword search, server + date-range filters, Load more"
```

---

## PHASE 3 — Bulk moderation

### Task 7: Bulk delete endpoint (ids or filter, soft/hard, 5000 cap)

**Files:**
- Modify: `FatGuysSpeak.Shared/DTOs.cs`
- Modify: `FatGuysSpeak.Server/Controllers/AdminController.cs`
- Modify: `FatGuysSpeak.Tests/Server/MessageLogModerationTests.cs`

- [ ] **Step 1: Add the request DTO**

In `FatGuysSpeak.Shared/DTOs.cs`, add:

```csharp
public record BulkDeleteRequest(int[]? Ids = null, MessageFilterDto? Filter = null, string Mode = "soft");
```

- [ ] **Step 2: Write the failing tests**

Add to `MessageLogModerationTests`:

```csharp
    [Fact]
    public async Task BulkDelete_ByIds_SoftDeletes()
    {
        var (_, admin) = await TestHelpers.SeedServerAsync(_db.Db, "owner");
        var a = await AddMsg(admin.Id, "one");
        var b = await AddMsg(admin.Id, "two");

        var ok = Assert.IsType<OkObjectResult>(
            await _c.BulkDelete(new BulkDeleteRequest(Ids: new[] { a.Id, b.Id })));
        var result = Assert.IsType<BulkActionResult>(ok.Value);
        Assert.Equal(2, result.Affected);

        Assert.True(await _db.Db.Messages.AsNoTracking().AllAsync(m => m.IsDeleted));
        // content preserved on soft delete
        Assert.Equal("one", (await _db.Db.Messages.AsNoTracking().FirstAsync(m => m.Id == a.Id)).Content);
    }

    [Fact]
    public async Task BulkDelete_ByFilter_OnlyMatchingAuthor()
    {
        var (server, admin) = await TestHelpers.SeedServerAsync(_db.Db, "owner");
        var other = new User { Username = "spammer", Email = "s@t.com", PasswordHash = "x" };
        _db.Db.Users.Add(other);
        await _db.Db.SaveChangesAsync();
        await AddMsg(admin.Id, "legit");
        await AddMsg(other.Id, "spam1");
        await AddMsg(other.Id, "spam2");

        var ok = Assert.IsType<OkObjectResult>(
            await _c.BulkDelete(new BulkDeleteRequest(Filter: new MessageFilterDto(Author: "spammer"))));
        Assert.Equal(2, Assert.IsType<BulkActionResult>(ok.Value).Affected);

        Assert.False((await _db.Db.Messages.AsNoTracking().FirstAsync(m => m.Content == "legit")).IsDeleted);
    }

    [Fact]
    public async Task BulkDelete_Hard_RemovesRows()
    {
        var (_, admin) = await TestHelpers.SeedServerAsync(_db.Db, "owner");
        var a = await AddMsg(admin.Id, "gone");

        var ok = Assert.IsType<OkObjectResult>(
            await _c.BulkDelete(new BulkDeleteRequest(Ids: new[] { a.Id }, Mode: "hard")));
        Assert.Equal(1, Assert.IsType<BulkActionResult>(ok.Value).Affected);
        Assert.False(await _db.Db.Messages.AsNoTracking().AnyAsync(m => m.Id == a.Id));
    }

    [Fact]
    public async Task BulkDelete_RejectsBothIdsAndFilter()
    {
        var res = await _c.BulkDelete(new BulkDeleteRequest(
            Ids: new[] { 1 }, Filter: new MessageFilterDto(Author: "x")));
        Assert.IsType<BadRequestObjectResult>(res);
    }

    [Fact]
    public async Task BulkDelete_WritesSingleAuditEntry()
    {
        var (_, admin) = await TestHelpers.SeedServerAsync(_db.Db, "owner");
        var a = await AddMsg(admin.Id, "one");
        var b = await AddMsg(admin.Id, "two");

        await _c.BulkDelete(new BulkDeleteRequest(Ids: new[] { a.Id, b.Id }));
        Assert.Equal(1, await _db.Db.AuditLogs.CountAsync(x => x.Action == "MessagesBulkDeleted"));
    }
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~MessageLogModerationTests"`
Expected: FAIL — `BulkDelete` does not exist.

- [ ] **Step 4: Add the bulk-delete endpoint**

In `AdminController`, add:

```csharp
    private const int BulkDeleteMax = 5000;

    [HttpPost("messages/delete")]
    public async Task<IActionResult> BulkDelete(FatGuysSpeak.Shared.BulkDeleteRequest req)
    {
        if (!IsLocal) return Forbid();

        var hasIds = req.Ids is { Length: > 0 };
        var hasFilter = req.Filter is not null;
        if (hasIds == hasFilter) return BadRequest("Provide exactly one of ids or filter.");

        var q = db.Messages.Include(m => m.Channel).AsQueryable();
        q = hasIds ? q.Where(m => req.Ids!.Contains(m.Id)) : ApplyMessageFilters(q, req.Filter!);

        var count = await q.CountAsync();
        if (count == 0) return Ok(new FatGuysSpeak.Shared.BulkActionResult(0, Array.Empty<int>()));
        if (count > BulkDeleteMax)
            return BadRequest($"Too many messages match ({count}). Narrow the filter; max {BulkDeleteMax} per operation.");

        var matched = await q.ToListAsync();
        var hard = string.Equals(req.Mode, "hard", StringComparison.OrdinalIgnoreCase);
        var channelIds = matched.Select(m => m.ChannelId).Distinct().ToArray();
        var msgRefs = matched.Select(m => (m.Id, m.ChannelId)).ToList();

        if (hard) db.Messages.RemoveRange(matched);
        else foreach (var m in matched) m.IsDeleted = true;

        db.AuditLogs.Add(new FatGuysSpeak.Server.Models.AuditLog
        {
            ServerId = matched[0].Channel.ServerId,
            ActorId = 0, ActorUsername = "admin",
            Action = hard ? "MessagesBulkPurged" : "MessagesBulkDeleted",
            TargetId = 0, TargetUsername = "",
            Detail = $"{(hard ? "Hard" : "Soft")}-deleted {matched.Count} message(s) via {(hasIds ? "selection" : "filter")}."
        });
        await db.SaveChangesAsync();

        foreach (var (id, channelId) in msgRefs)
            await hub.Clients.Group($"channel-{channelId}").SendAsync("MessageDeleted", id, channelId);

        return Ok(new FatGuysSpeak.Shared.BulkActionResult(matched.Count, channelIds));
    }
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test FatGuysSpeak.Tests --filter "FullyQualifiedName~MessageLogModerationTests"`
Expected: PASS (13 tests).

- [ ] **Step 6: Commit**

```bash
git add FatGuysSpeak.Shared/DTOs.cs FatGuysSpeak.Server/Controllers/AdminController.cs FatGuysSpeak.Tests/Server/MessageLogModerationTests.cs
git commit -m "Message log: bulk delete endpoint (ids/filter, soft/hard, 5000 cap)"
```

---

### Task 8: Dashboard Phase-3 UI — selection, bulk bar, purge-by-filter, hard-delete confirm

**Files:**
- Modify: `FatGuysSpeak.Server/Controllers/MetricsController.cs` (messages tab header + table head)
- Modify: `FatGuysSpeak.Server/wwwroot/dashboard.js` (`renderMessages`, handlers, click switch)

- [ ] **Step 1: Add the bulk action bar + Permanent toggle to the HTML header**

In `MetricsController.cs`, immediately after the messages-tab `<div class="panel-header">...</div>` (before `<table class="user-table">`), insert a bulk bar:

```html
          <div id="msgBulkBar" style="display:none;gap:10px;align-items:center;padding:8px 0;border-bottom:1px solid #2a2a2a;margin-bottom:6px">
            <span id="msgSelCount" style="color:#8ab4d4;font-size:12px">0 selected</span>
            <button class="btn-sm danger" data-click="bulkDelSelected">Delete selected</button>
            <button class="btn-sm" data-click="bulkClearSel">Clear</button>
            <label style="font-size:11px;color:#888;display:flex;align-items:center;gap:5px;cursor:pointer" title="Permanently remove from the database (irreversible) instead of hiding">
              <input type="checkbox" id="msgHardDelete" style="accent-color:#ed4245" /> Permanent
            </label>
          </div>
          <div style="padding:0 0 8px 0;display:flex;gap:8px">
            <button class="btn-sm danger" data-click="bulkDelFilter" title="Delete ALL messages matching the current filters (not just the loaded page)">Delete all matching filter</button>
            <button class="btn-sm" data-click="bulkRestoreFilter" title="Restore ALL soft-deleted messages matching the current filters">Restore all matching filter</button>
          </div>
```

- [ ] **Step 2: Add a select-all checkbox column to the table head**

In `MetricsController.cs`, in the messages-tab `<thead><tr>`, add as the first `<th>`:

```html
                <th style="width:28px"><input type="checkbox" id="msgSelectAll" data-change="toggleSelectAll" title="Select all loaded rows" style="accent-color:#8ab4d4" /></th>
```

Also bump the "Loading…"/empty `colspan` from `7` to `8` in both `MetricsController.cs` (the initial `<tr><td colspan="7">Loading…`) and in `dashboard.js` (`renderMessages` empty state and the `catch` error rows in `loadMessages`/`loadMoreMsgs`). Use `colspan="8"`.

- [ ] **Step 3: Update `renderMessages` to add per-row checkboxes**

In `dashboard.js`, in `renderMessages`, add a checkbox cell as the first `<td>` of the row template (keep everything else from Task 3):

```javascript
      <td><input type="checkbox" class="msg-sel" data-change="toggleMsgSel" data-mid="${m.id}" ${selectedMsgIds.has(m.id) ? 'checked' : ''} style="accent-color:#8ab4d4" /></td>
```

Add the empty-state `colspan="8"` (was 7). At the top of `dashboard.js`'s message section, add the selection set:

```javascript
const selectedMsgIds = new Set();
```

- [ ] **Step 4: Add selection + bulk handlers**

In `dashboard.js`, add:

```javascript
function updateBulkBar() {
  const n = selectedMsgIds.size;
  document.getElementById('msgBulkBar').style.display = n ? 'flex' : 'none';
  document.getElementById('msgSelCount').textContent = `${n} selected`;
}

function toggleMsgSel(el) {
  const id = +el.dataset.mid;
  if (el.checked) selectedMsgIds.add(id); else selectedMsgIds.delete(id);
  updateBulkBar();
}

function toggleSelectAll(el) {
  selectedMsgIds.clear();
  if (el.checked) allLoadedMsgs.forEach(m => selectedMsgIds.add(m.id));
  document.querySelectorAll('.msg-sel').forEach(cb => { cb.checked = el.checked; });
  updateBulkBar();
}

function bulkClearSel() {
  selectedMsgIds.clear();
  document.getElementById('msgSelectAll').checked = false;
  document.querySelectorAll('.msg-sel').forEach(cb => { cb.checked = false; });
  updateBulkBar();
}

async function postBulkDelete(body, label) {
  const hard = document.getElementById('msgHardDelete').checked;
  body.mode = hard ? 'hard' : 'soft';
  let warn = `${label}\n\nThis will ${hard ? 'PERMANENTLY delete' : 'soft-delete'} the matching messages.`;
  if (!confirm(warn)) return;
  if (hard && !confirm('Permanent delete is IRREVERSIBLE. Type-check: click OK only if you are sure.')) return;
  try {
    const res = await fetch('/api/admin/messages/delete', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
    });
    if (!res.ok) { alert('Delete failed: ' + (await res.text())); return; }
    const r = await res.json();
    alert(`Deleted ${r.affected} message(s).`);
    bulkClearSel();
    loadMessages();
  } catch (e) { alert('Delete failed: ' + e.message); }
}

function bulkDelSelected() {
  if (!selectedMsgIds.size) return;
  postBulkDelete({ ids: [...selectedMsgIds] }, `Delete ${selectedMsgIds.size} selected message(s)?`);
}

function bulkDelFilter() {
  const f = {
    author:   document.getElementById('msgAuthor').value.trim() || null,
    channel:  document.getElementById('msgChannel').value.trim() || null,
    keyword:  document.getElementById('msgKeyword').value.trim() || null,
    source:   document.getElementById('msgSource').value || null,
    serverId: document.getElementById('msgServer').value ? +document.getElementById('msgServer').value : null,
  };
  const range = document.getElementById('msgRange').value;
  if (range) f.from = new Date(Date.now() - (+range) * 86400000).toISOString();
  const hasAny = Object.values(f).some(v => v !== null && v !== undefined);
  if (!hasAny && !confirm('No filters set — this will delete EVERY message. Continue?')) return;
  postBulkDelete({ filter: f }, 'Delete ALL messages matching the current filter?');
}

function currentFilterObj() {
  const f = {
    author:   document.getElementById('msgAuthor').value.trim() || null,
    channel:  document.getElementById('msgChannel').value.trim() || null,
    keyword:  document.getElementById('msgKeyword').value.trim() || null,
    source:   document.getElementById('msgSource').value || null,
    serverId: document.getElementById('msgServer').value ? +document.getElementById('msgServer').value : null,
  };
  const range = document.getElementById('msgRange').value;
  if (range) f.from = new Date(Date.now() - (+range) * 86400000).toISOString();
  return f;
}

async function bulkRestoreFilter() {
  const f = currentFilterObj();
  if (!confirm('Restore ALL soft-deleted messages matching the current filter?')) return;
  try {
    const res = await fetch('/api/admin/messages/restore', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ filter: f })
    });
    if (!res.ok) { alert('Restore failed: ' + (await res.text())); return; }
    const r = await res.json();
    alert(`Restored ${r.affected} message(s).`);
    loadMessages();
  } catch (e) { alert('Restore failed: ' + e.message); }
}
```

(Note: `bulkDelFilter` can be simplified to reuse `currentFilterObj()`, but is shown standalone above for clarity; either is fine.)

- [ ] **Step 5: Wire the new delegation cases**

In `dashboard.js` `click` switch, add:

```javascript
    case 'bulkDelSelected':   bulkDelSelected(); break;
    case 'bulkClearSel':      bulkClearSel(); break;
    case 'bulkDelFilter':     bulkDelFilter(); break;
    case 'bulkRestoreFilter': bulkRestoreFilter(); break;
```

In `dashboard.js` `change` delegation switch (`switch (d.change)`), add:

```javascript
    case 'toggleMsgSel':    toggleMsgSel(el); break;
    case 'toggleSelectAll': toggleSelectAll(el); break;
```

- [ ] **Step 6: Syntax-check**

Run: `node --check FatGuysSpeak.Server/wwwroot/dashboard.js`
Expected: no output.

- [ ] **Step 7: Build, relaunch, verify**

Rebuild Windows + relaunch. In Message Log verify:
- Ticking row checkboxes shows the bulk bar with a live count; "Delete selected" soft-deletes them and they vanish (or show as deleted with "show deleted").
- The header checkbox selects all loaded rows; "Clear" deselects.
- "Delete all matching filter" with an author/channel/keyword set deletes the whole matching set (confirm the count in the alert), and unrelated messages remain.
- Ticking "Permanent" then deleting requires the second confirmation and the rows are gone even with "show deleted" on.
- With no filters, "Delete all matching filter" warns before proceeding.
- With "show deleted" on and an author filter set, "Restore all matching filter" un-hides that author's deleted messages and reports the count.

- [ ] **Step 8: Run the full suite + commit**

Run: `dotnet test FatGuysSpeak.Tests`
Expected: all green.

```bash
git add FatGuysSpeak.Server/Controllers/MetricsController.cs FatGuysSpeak.Server/wwwroot/dashboard.js
git commit -m "Message log UI: multi-select, bulk delete, purge-by-filter, hard-delete confirm"
```

---

## Final verification (after all phases)

- [ ] Run full suite: `dotnet test FatGuysSpeak.Tests` — all green.
- [ ] Two-client runtime smoke (reuse the established pattern): with two SignalR clients in a channel, soft-delete one client's message via the dashboard endpoint and confirm both clients receive `MessageDeleted` for it; restore and confirm the row's content is intact server-side.
- [ ] Dispatch a final code review over the whole branch.
