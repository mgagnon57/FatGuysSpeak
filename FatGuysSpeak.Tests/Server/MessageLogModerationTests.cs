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
