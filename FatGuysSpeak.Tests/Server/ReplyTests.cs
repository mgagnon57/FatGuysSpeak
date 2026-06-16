using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Tests.Server;

public class ReplyTests : IDisposable
{
    private readonly TestDb _db;
    private readonly MessagesController _ctrl;
    private GuildServer _server = null!;
    private Channel _channel = null!;
    private User _user = null!;
    private User _other = null!;

    public ReplyTests()
    {
        _db = new TestDb();
        _ctrl = new MessagesController(_db.Db, TestHelpers.MockHub(), new ServerMetricsService(), TestHelpers.NullBot(), TestHelpers.NullAutomod(), TestHelpers.NullWebhooks());
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedAsync()
    {
        (_server, _user) = await TestHelpers.SeedServerAsync(_db.Db, "owner");
        _channel = _db.Db.Channels.First(c => c.ServerId == _server.Id && c.Type == ChannelType.Text);
        _other = new User { Username = "other", Email = "other@test.com", PasswordHash = "*" };
        _db.Db.Users.Add(_other);
        await _db.Db.SaveChangesAsync();
        _db.Db.ServerMembers.Add(new ServerMember { ServerId = _server.Id, UserId = _other.Id });
        await _db.Db.SaveChangesAsync();
        TestHelpers.SetUser(_ctrl, _user.Id, _user.Username);
    }

    [Fact]
    public async Task SendMessage_ReplyToMessageInAnotherChannel_ReturnsBadRequest()
    {
        await SeedAsync();
        // A message in a different channel — replying to it would leak its content via the preview.
        var otherChannel = new Channel { Name = "secret", Type = ChannelType.Text, ServerId = _server.Id, Position = 5 };
        _db.Db.Channels.Add(otherChannel);
        await _db.Db.SaveChangesAsync();
        var foreign = new Message { Content = "top secret", AuthorId = _other.Id, ChannelId = otherChannel.Id };
        _db.Db.Messages.Add(foreign);
        await _db.Db.SaveChangesAsync();

        var req = new SendMessageRequest("leak attempt", ReplyToMessageId: foreign.Id);
        var result = await _ctrl.SendMessage(_channel.Id, req);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SendMessage_WithReply_SetsReplyToId()
    {
        await SeedAsync();
        var original = new Message { Content = "original message", AuthorId = _other.Id, ChannelId = _channel.Id };
        _db.Db.Messages.Add(original);
        await _db.Db.SaveChangesAsync();

        var req = new SendMessageRequest("this is my reply", ReplyToMessageId: original.Id);
        var result = await _ctrl.SendMessage(_channel.Id, req);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<MessageDto>(ok.Value);
        Assert.Equal(original.Id, dto.ReplyToId);
        Assert.Equal(_other.Username, dto.ReplyToUsername);
        Assert.Equal("original message", dto.ReplyPreview);
    }

    [Fact]
    public async Task GetMessages_IncludesReplyData()
    {
        await SeedAsync();
        var original = new Message { Content = "the original", AuthorId = _other.Id, ChannelId = _channel.Id };
        _db.Db.Messages.Add(original);
        await _db.Db.SaveChangesAsync();

        var reply = new Message { Content = "my reply", AuthorId = _user.Id, ChannelId = _channel.Id, ReplyToId = original.Id };
        _db.Db.Messages.Add(reply);
        await _db.Db.SaveChangesAsync();

        var result = await _ctrl.GetMessages(_channel.Id);
        var msgs = result.Value!;

        var replyMsg = msgs.First(m => m.Content == "my reply");
        Assert.Equal(original.Id, replyMsg.ReplyToId);
        Assert.Equal(_other.Username, replyMsg.ReplyToUsername);
        Assert.Equal("the original", replyMsg.ReplyPreview);
    }

    [Fact]
    public async Task GetMessages_NullReplyData_WhenNoReply()
    {
        await SeedAsync();
        _db.Db.Messages.Add(new Message { Content = "plain message", AuthorId = _user.Id, ChannelId = _channel.Id });
        await _db.Db.SaveChangesAsync();

        var result = await _ctrl.GetMessages(_channel.Id);
        var msgs = result.Value!;

        Assert.Null(msgs[0].ReplyToId);
        Assert.Null(msgs[0].ReplyToUsername);
        Assert.Null(msgs[0].ReplyPreview);
    }

    [Fact]
    public async Task GetMessages_ReplyPreview_TruncatedAt100Chars()
    {
        await SeedAsync();
        var longContent = new string('x', 150);
        var original = new Message { Content = longContent, AuthorId = _other.Id, ChannelId = _channel.Id };
        _db.Db.Messages.Add(original);
        await _db.Db.SaveChangesAsync();

        var reply = new Message { Content = "reply", AuthorId = _user.Id, ChannelId = _channel.Id, ReplyToId = original.Id };
        _db.Db.Messages.Add(reply);
        await _db.Db.SaveChangesAsync();

        var result = await _ctrl.GetMessages(_channel.Id);
        var msgs = result.Value!;

        var replyMsg = msgs.First(m => m.Content == "reply");
        Assert.EndsWith("…", replyMsg.ReplyPreview);
        Assert.True(replyMsg.ReplyPreview!.Length <= 101);
    }

    [Fact]
    public async Task GetMessages_DeletedReply_PreviewIsNull()
    {
        await SeedAsync();
        var original = new Message { Content = "will be deleted", AuthorId = _other.Id, ChannelId = _channel.Id, IsDeleted = true };
        _db.Db.Messages.Add(original);
        await _db.Db.SaveChangesAsync();

        var reply = new Message { Content = "reply to deleted", AuthorId = _user.Id, ChannelId = _channel.Id, ReplyToId = original.Id };
        _db.Db.Messages.Add(reply);
        await _db.Db.SaveChangesAsync();

        var result = await _ctrl.GetMessages(_channel.Id);
        var msgs = result.Value!;

        var replyMsg = msgs.First(m => m.Content == "reply to deleted");
        Assert.Equal(original.Id, replyMsg.ReplyToId);
        Assert.Null(replyMsg.ReplyPreview);
    }

    [Fact]
    public async Task SendMessage_WithNullReplyId_SetsNoReply()
    {
        await SeedAsync();

        var result = await _ctrl.SendMessage(_channel.Id, new SendMessageRequest("plain message"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<MessageDto>(ok.Value);
        Assert.Null(dto.ReplyToId);
        Assert.Null(dto.ReplyToUsername);
        Assert.Null(dto.ReplyPreview);
    }
}
