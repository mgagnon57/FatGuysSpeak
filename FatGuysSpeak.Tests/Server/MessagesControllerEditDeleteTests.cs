using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;
using FatGuysSpeak.Server.Hubs;

namespace FatGuysSpeak.Tests.Server;

public class MessagesControllerEditDeleteTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly Mock<IHubContext<ChatHub>> _hubMock;
    private readonly MessagesController _controller;
    private GuildServer _server = null!;
    private Channel _textChannel = null!;
    private User _owner = null!;

    public MessagesControllerEditDeleteTests()
    {
        _testDb = new TestDb();

        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        _hubMock = new Mock<IHubContext<ChatHub>>();
        _hubMock.Setup(h => h.Clients).Returns(clients.Object);

        _controller = new MessagesController(_testDb.Db, _hubMock.Object, new FatGuysSpeak.Server.Services.ServerMetricsService(), TestHelpers.NullBot());
    }

    public void Dispose() => _testDb.Dispose();

    private async Task SeedAsync()
    {
        (_server, _owner) = await TestHelpers.SeedServerAsync(_testDb.Db);
        _textChannel = _testDb.Db.Channels.First(c => c.ServerId == _server.Id && c.Type == ChannelType.Text);
        TestHelpers.SetUser(_controller, _owner.Id, _owner.Username);
    }

    private async Task<Message> AddMessageAsync(string content = "original", int? authorId = null)
    {
        var msg = new Message
        {
            Content = content,
            AuthorId = authorId ?? _owner.Id,
            ChannelId = _textChannel.Id
        };
        _testDb.Db.Messages.Add(msg);
        await _testDb.Db.SaveChangesAsync();
        return msg;
    }

    // ── EditMessage ───────────────────────────────────────────────────────────

    [Fact]
    public async Task EditMessage_OwnMessage_UpdatesContentAndSetsEditedAt()
    {
        await SeedAsync();
        var msg = await AddMessageAsync("before");

        var result = await _controller.EditMessage(_textChannel.Id, msg.Id, new EditMessageRequest("after"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<MessageDto>(ok.Value);
        Assert.Equal("after", dto.Content);
        Assert.NotNull(dto.EditedAt);
    }

    [Fact]
    public async Task EditMessage_OwnMessage_BroadcastsMessageEdited()
    {
        await SeedAsync();
        var msg = await AddMessageAsync();

        await _controller.EditMessage(_textChannel.Id, msg.Id, new EditMessageRequest("updated"));

        _hubMock.Verify(h => h.Clients.Group($"channel-{_textChannel.Id}"), Times.Once);
    }

    [Fact]
    public async Task EditMessage_OtherUsersMessage_ReturnsForbid()
    {
        await SeedAsync();
        var other = new User { Username = "other", Email = "other@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(other);
        await _testDb.Db.SaveChangesAsync();

        var msg = await AddMessageAsync(authorId: other.Id);

        var result = await _controller.EditMessage(_textChannel.Id, msg.Id, new EditMessageRequest("hacked"));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task EditMessage_UnknownMessage_ReturnsNotFound()
    {
        await SeedAsync();

        var result = await _controller.EditMessage(_textChannel.Id, 9999, new EditMessageRequest("x"));

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task EditMessage_EmptyContent_ReturnsBadRequest()
    {
        await SeedAsync();
        var msg = await AddMessageAsync();

        var result = await _controller.EditMessage(_textChannel.Id, msg.Id, new EditMessageRequest("   "));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task EditMessage_ContentExceeds2000Chars_ReturnsBadRequest()
    {
        await SeedAsync();
        var msg = await AddMessageAsync();
        var tooLong = new string('x', 2001);

        var result = await _controller.EditMessage(_textChannel.Id, msg.Id, new EditMessageRequest(tooLong));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task EditMessage_DeletedMessage_ReturnsBadRequest()
    {
        await SeedAsync();
        var msg = await AddMessageAsync();
        msg.IsDeleted = true;
        await _testDb.Db.SaveChangesAsync();

        var result = await _controller.EditMessage(_textChannel.Id, msg.Id, new EditMessageRequest("new content"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task EditMessage_Success_TrimsWhitespace()
    {
        await SeedAsync();
        var msg = await AddMessageAsync("hello");

        var result = await _controller.EditMessage(_textChannel.Id, msg.Id, new EditMessageRequest("  padded  "));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<MessageDto>(ok.Value);
        Assert.Equal("padded", dto.Content);
    }

    [Fact]
    public async Task EditMessage_WrongChannelId_ReturnsNotFound()
    {
        await SeedAsync();
        var msg = await AddMessageAsync();

        // Pass a different channelId than the message belongs to
        var result = await _controller.EditMessage(9999, msg.Id, new EditMessageRequest("content"));

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ── DeleteMessage ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteMessage_OwnMessage_SetsIsDeleted()
    {
        await SeedAsync();
        var msg = await AddMessageAsync("bye");

        await _controller.DeleteMessage(_textChannel.Id, msg.Id);

        var stored = _testDb.Db.Messages.Find(msg.Id);
        Assert.True(stored!.IsDeleted);
    }

    [Fact]
    public async Task DeleteMessage_OwnMessage_ReturnsNoContent()
    {
        await SeedAsync();
        var msg = await AddMessageAsync();

        var result = await _controller.DeleteMessage(_textChannel.Id, msg.Id);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteMessage_OwnMessage_BroadcastsMessageDeleted()
    {
        await SeedAsync();
        var msg = await AddMessageAsync();

        await _controller.DeleteMessage(_textChannel.Id, msg.Id);

        _hubMock.Verify(h => h.Clients.Group($"channel-{_textChannel.Id}"), Times.Once);
    }

    [Fact]
    public async Task DeleteMessage_RegularMember_CannotDeleteOthersMessage()
    {
        await SeedAsync();
        // Add a second member (Member role) and a message authored by the owner
        var regularMember = new User { Username = "regular2", Email = "regular2@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(regularMember);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new FatGuysSpeak.Server.Models.ServerMember
        {
            ServerId = _server.Id, UserId = regularMember.Id,
            Role = FatGuysSpeak.Shared.ServerRole.Member
        });
        await _testDb.Db.SaveChangesAsync();

        var msg = await AddMessageAsync(authorId: _owner.Id, content: "owner message");
        TestHelpers.SetUser(_controller, regularMember.Id, regularMember.Username);

        var result = await _controller.DeleteMessage(_textChannel.Id, msg.Id);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task DeleteMessage_UnknownMessage_ReturnsNotFound()
    {
        await SeedAsync();

        var result = await _controller.DeleteMessage(_textChannel.Id, 9999);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DeleteMessage_WrongChannelId_ReturnsNotFound()
    {
        await SeedAsync();
        var msg = await AddMessageAsync();

        var result = await _controller.DeleteMessage(9999, msg.Id);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DeleteMessage_DoesNotHardDeleteRecord()
    {
        await SeedAsync();
        var msg = await AddMessageAsync("keep me");

        await _controller.DeleteMessage(_textChannel.Id, msg.Id);

        Assert.True(_testDb.Db.Messages.Any(m => m.Id == msg.Id),
            "Soft delete must keep the row — IsDeleted=true, not a hard DELETE");
    }

    // ── DTO fields ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMessages_ReturnsIsDeletedAndEditedAtInDto()
    {
        await SeedAsync();
        _testDb.Db.Messages.AddRange(
            new Message { Content = "normal", AuthorId = _owner.Id, ChannelId = _textChannel.Id },
            new Message { Content = "gone", AuthorId = _owner.Id, ChannelId = _textChannel.Id, IsDeleted = true },
            new Message { Content = "edited", AuthorId = _owner.Id, ChannelId = _textChannel.Id, EditedAt = DateTime.UtcNow }
        );
        await _testDb.Db.SaveChangesAsync();

        var result = await _controller.GetMessages(_textChannel.Id);

        var msgs = result.Value!;
        Assert.Equal(3, msgs.Count);
        Assert.False(msgs[0].IsDeleted);
        Assert.Null(msgs[0].EditedAt);
        Assert.True(msgs[1].IsDeleted);
        Assert.NotNull(msgs[2].EditedAt);
    }

    [Fact]
    public async Task SendMessage_WithAttachmentUrl_IncludesItInDto()
    {
        await SeedAsync();
        var attachmentUrl = "https://localhost:5238/uploads/abc.png";

        var result = await _controller.SendMessage(_textChannel.Id,
            new SendMessageRequest("look at this", AttachmentUrl: attachmentUrl));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<MessageDto>(ok.Value);
        Assert.Equal(attachmentUrl, dto.AttachmentUrl);
    }

    [Fact]
    public async Task SendMessage_AttachmentOnly_NoContentRequired()
    {
        await SeedAsync();

        var result = await _controller.SendMessage(_textChannel.Id,
            new SendMessageRequest("", AttachmentUrl: "https://localhost:5238/uploads/img.jpg"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<MessageDto>(ok.Value);
        Assert.Equal("", dto.Content);
        Assert.NotNull(dto.AttachmentUrl);
    }

    [Fact]
    public async Task SendMessage_AttachmentWithInvalidPath_ReturnsBadRequest()
    {
        await SeedAsync();

        var result = await _controller.SendMessage(_textChannel.Id,
            new SendMessageRequest("bad url", AttachmentUrl: "https://evil.com/hack.jpg"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }
}
