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
}
