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
    public async Task GetServers_ReturnsSeededServer()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_db.Db, "owner");
        var ok = Assert.IsType<OkObjectResult>(await _c.GetServers());
        var list = Assert.IsType<List<AdminServerDto>>(ok.Value);
        Assert.Contains(list, s => s.Id == server.Id);
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

    [Fact]
    public async Task GetMessages_ShowDeletedFalse_ExcludesDeleted()
    {
        var (_, admin) = await TestHelpers.SeedServerAsync(_db.Db, "owner");
        var keep = await AddMsg(admin.Id, "keep");
        var del = await AddMsg(admin.Id, "deleted");
        del.IsDeleted = true;
        await _db.Db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await _c.GetMessages(showDeleted: false));
        var list = Assert.IsType<List<AdminMessageDto>>(ok.Value);
        Assert.Single(list);
        Assert.Equal(keep.Id, list[0].Id);
    }

    [Fact]
    public async Task GetMessages_ShowDeletedTrue_IncludesDeleted()
    {
        var (_, admin) = await TestHelpers.SeedServerAsync(_db.Db, "owner");
        await AddMsg(admin.Id, "keep");
        var del = await AddMsg(admin.Id, "deleted");
        del.IsDeleted = true;
        await _db.Db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await _c.GetMessages(showDeleted: true));
        var list = Assert.IsType<List<AdminMessageDto>>(ok.Value);
        Assert.Equal(2, list.Count);
    }

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
        await AddMsg(admin.Id, "first");
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
    }

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

    [Fact]
    public async Task BulkDelete_Hard_OrphansThreadReplies()
    {
        var (_, admin) = await TestHelpers.SeedServerAsync(_db.Db, "owner");
        var root = await AddMsg(admin.Id, "thread root");
        var reply = new Message { AuthorId = admin.Id, ChannelId = root.ChannelId, Content = "reply", ThreadId = root.Id };
        _db.Db.Messages.Add(reply);
        await _db.Db.SaveChangesAsync();

        await _c.BulkDelete(new BulkDeleteRequest(Ids: new[] { root.Id }, Mode: "hard"));

        Assert.False(await _db.Db.Messages.AsNoTracking().AnyAsync(m => m.Id == root.Id)); // root gone
        var reloaded = await _db.Db.Messages.AsNoTracking().FirstAsync(m => m.Id == reply.Id);
        Assert.Null(reloaded.ThreadId); // reply orphaned, not deleted
    }

    [Fact]
    public async Task BulkDelete_Hard_RemovesDependentPinsAndReactions()
    {
        var (_, admin) = await TestHelpers.SeedServerAsync(_db.Db, "owner");
        var m = await AddMsg(admin.Id, "pinned and reacted");
        _db.Db.PinnedMessages.Add(new PinnedMessage
        {
            MessageId = m.Id,
            ChannelId = m.ChannelId,
            PinnedById = admin.Id
        });
        _db.Db.MessageReactions.Add(new MessageReaction
        {
            MessageId = m.Id,
            UserId = admin.Id,
            Username = admin.Username,
            Emoji = "👍"
        });
        await _db.Db.SaveChangesAsync();

        await _c.BulkDelete(new BulkDeleteRequest(Ids: new[] { m.Id }, Mode: "hard"));

        Assert.False(await _db.Db.Messages.AsNoTracking().AnyAsync(x => x.Id == m.Id));
        Assert.False(await _db.Db.PinnedMessages.AsNoTracking().AnyAsync(p => p.MessageId == m.Id));
        Assert.False(await _db.Db.MessageReactions.AsNoTracking().AnyAsync(r => r.MessageId == m.Id));
    }
}
