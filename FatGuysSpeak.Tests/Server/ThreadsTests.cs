using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;
using FatGuysSpeak.Server.Hubs;

namespace FatGuysSpeak.Tests.Server;

[Collection("HubTests")]
public class ThreadsTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly Mock<IHubContext<ChatHub>> _hubMock;
    private readonly MessagesController _ctrl;
    private GuildServer _server = null!;
    private Channel _channel = null!;
    private User _user = null!;

    public ThreadsTests()
    {
        _testDb = new TestDb();

        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        _hubMock = new Mock<IHubContext<ChatHub>>();
        _hubMock.Setup(h => h.Clients).Returns(clients.Object);

        _ctrl = new MessagesController(_testDb.Db, _hubMock.Object,
            new FatGuysSpeak.Server.Services.ServerMetricsService(), TestHelpers.NullBot(), TestHelpers.NullAutomod(), TestHelpers.NullWebhooks());
    }

    public void Dispose() => _testDb.Dispose();

    private async Task SeedAsync()
    {
        (_server, _user) = await TestHelpers.SeedServerAsync(_testDb.Db);
        _channel = _testDb.Db.Channels.First(c => c.ServerId == _server.Id && c.Type == ChannelType.Text);
        TestHelpers.SetUser(_ctrl, _user.Id, _user.Username);
    }

    private async Task<MessageDto> SendRootAsync(string content = "Root message")
    {
        var result = await _ctrl.SendMessage(_channel.Id, new SendMessageRequest(content));
        return ((OkObjectResult)result.Result!).Value as MessageDto ?? throw new Exception("no dto");
    }

    [Fact]
    public async Task SendMessage_WithThreadId_DoesNotAppearInMainFeed()
    {
        await SeedAsync();
        var root = await SendRootAsync();

        await _ctrl.SendMessage(_channel.Id, new SendMessageRequest("Thread reply", ThreadId: root.Id));

        var feedResult = await _ctrl.GetMessages(_channel.Id);
        var feed = feedResult.Value ?? [];
        Assert.DoesNotContain(feed, m => m.Content == "Thread reply");
    }

    [Fact]
    public async Task SendMessage_WithThreadId_BroadcastsThreadReplyReceived()
    {
        await SeedAsync();
        var root = await SendRootAsync();

        await _ctrl.SendMessage(_channel.Id, new SendMessageRequest("Thread reply", ThreadId: root.Id));

        _hubMock.Verify(h => h.Clients.Group($"channel-{_channel.Id}"), Times.AtLeastOnce);
    }

    [Fact]
    public async Task SendMessage_WithThreadId_InvalidRoot_ReturnsBadRequest()
    {
        await SeedAsync();

        var result = await _ctrl.SendMessage(_channel.Id, new SendMessageRequest("bad", ThreadId: 9999));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetMessages_ExcludesThreadReplies()
    {
        await SeedAsync();
        var root = await SendRootAsync();
        await _ctrl.SendMessage(_channel.Id, new SendMessageRequest("thread msg", ThreadId: root.Id));

        var feedResult = await _ctrl.GetMessages(_channel.Id);
        var feed = feedResult.Value ?? [];

        Assert.DoesNotContain(feed, m => m.ThreadId.HasValue);
    }

    [Fact]
    public async Task GetMessages_RootShowsReplyCount()
    {
        await SeedAsync();
        var root = await SendRootAsync();
        await _ctrl.SendMessage(_channel.Id, new SendMessageRequest("reply 1", ThreadId: root.Id));
        await _ctrl.SendMessage(_channel.Id, new SendMessageRequest("reply 2", ThreadId: root.Id));

        var feedResult = await _ctrl.GetMessages(_channel.Id);
        var feed = feedResult.Value ?? [];
        var rootInFeed = feed.First(m => m.Id == root.Id);

        Assert.Equal(2, rootInFeed.ReplyCount);
    }

    [Fact]
    public async Task GetThreadMessages_ReturnsRootAndReplies()
    {
        await SeedAsync();
        var root = await SendRootAsync();
        await _ctrl.SendMessage(_channel.Id, new SendMessageRequest("reply A", ThreadId: root.Id));
        await _ctrl.SendMessage(_channel.Id, new SendMessageRequest("reply B", ThreadId: root.Id));

        var result = await _ctrl.GetThreadMessages(_channel.Id, root.Id);
        var msgs = result.Value ?? [];

        Assert.Equal(3, msgs.Count);
        Assert.Equal(root.Id, msgs[0].Id);
        Assert.Contains(msgs, m => m.Content == "reply A");
        Assert.Contains(msgs, m => m.Content == "reply B");
    }

    [Fact]
    public async Task GetThreadMessages_UnknownRoot_ReturnsNotFound()
    {
        await SeedAsync();

        var result = await _ctrl.GetThreadMessages(_channel.Id, 9999);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task SendMessage_RootWithNoReplies_HasZeroReplyCount()
    {
        await SeedAsync();
        var root = await SendRootAsync();

        var feedResult = await _ctrl.GetMessages(_channel.Id);
        var feed = feedResult.Value ?? [];
        var rootDto = feed.First(m => m.Id == root.Id);

        Assert.Equal(0, rootDto.ReplyCount);
    }
}
