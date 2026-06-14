using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;
using FatGuysSpeak.Server.Hubs;

namespace FatGuysSpeak.Tests.Server;

public class MessagesControllerTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly Mock<IHubContext<ChatHub>> _hubMock;
    private readonly MessagesController _controller;
    private GuildServer _server = null!;
    private Channel _textChannel = null!;
    private User _user = null!;

    public MessagesControllerTests()
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
        (_server, _user) = await TestHelpers.SeedServerAsync(_testDb.Db);
        _textChannel = _testDb.Db.Channels.First(c => c.ServerId == _server.Id && c.Type == ChannelType.Text);
        TestHelpers.SetUser(_controller, _user.Id, _user.Username);
    }

    [Fact]
    public async Task GetMessages_ReturnsMemberMessages()
    {
        await SeedAsync();
        _testDb.Db.Messages.AddRange(
            new Message { Content = "hello", AuthorId = _user.Id, ChannelId = _textChannel.Id },
            new Message { Content = "world", AuthorId = _user.Id, ChannelId = _textChannel.Id }
        );
        await _testDb.Db.SaveChangesAsync();

        var result = await _controller.GetMessages(_textChannel.Id);

        var msgs = result.Value ?? (result.Result as OkObjectResult)?.Value as List<MessageDto>;
        Assert.NotNull(msgs);
        Assert.Equal(2, msgs.Count);
    }

    [Fact]
    public async Task GetMessages_NonMember_ReturnsForbid()
    {
        await SeedAsync();
        var stranger = new User { Username = "stranger", Email = "s@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(stranger);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_controller, stranger.Id);

        var result = await _controller.GetMessages(_textChannel.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetMessages_UnknownChannel_ReturnsNotFound()
    {
        await SeedAsync();

        var result = await _controller.GetMessages(999);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task SendMessage_Success_SavesAndBroadcasts()
    {
        await SeedAsync();

        var result = await _controller.SendMessage(_textChannel.Id, new SendMessageRequest("hi there"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<MessageDto>(ok.Value);
        Assert.Equal("hi there", dto.Content);
        Assert.Equal(_user.Username, dto.AuthorUsername);

        Assert.True(_testDb.Db.Messages.Any(m => m.Content == "hi there"));
        _hubMock.Verify(h => h.Clients.Group($"channel-{_textChannel.Id}"), Times.Once);
    }

    [Fact]
    public async Task SendMessage_NonMember_ReturnsForbid()
    {
        await SeedAsync();
        var stranger = new User { Username = "stranger", Email = "s@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(stranger);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_controller, stranger.Id);

        var result = await _controller.SendMessage(_textChannel.Id, new SendMessageRequest("hack"));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task SendMessage_UnknownChannel_ReturnsNotFound()
    {
        await SeedAsync();

        var result = await _controller.SendMessage(999, new SendMessageRequest("msg"));

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
