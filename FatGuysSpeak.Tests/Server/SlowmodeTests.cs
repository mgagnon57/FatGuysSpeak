using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;
using FatGuysSpeak.Server.Hubs;

namespace FatGuysSpeak.Tests.Server;

public class SlowmodeTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly Mock<IHubContext<ChatHub>> _hubMock;
    private readonly ServersController _serversCtrl;
    private readonly MessagesController _messagesCtrl;
    private GuildServer _server = null!;
    private Channel _channel = null!;
    private User _admin = null!;
    private User _member = null!;

    public SlowmodeTests()
    {
        _testDb = new TestDb();

        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        _hubMock = new Mock<IHubContext<ChatHub>>();
        _hubMock.Setup(h => h.Clients).Returns(clients.Object);

        _serversCtrl = new ServersController(_testDb.Db, _hubMock.Object, TestHelpers.NullWebhooks());
        _messagesCtrl = new MessagesController(_testDb.Db, _hubMock.Object,
            new FatGuysSpeak.Server.Services.ServerMetricsService(), TestHelpers.NullBot(), TestHelpers.NullAutomod(), TestHelpers.NullWebhooks());
    }

    public void Dispose() => _testDb.Dispose();

    private async Task SeedAsync()
    {
        (_server, _admin) = await TestHelpers.SeedServerAsync(_testDb.Db);
        _channel = _testDb.Db.Channels.First(c => c.ServerId == _server.Id && c.Type == ChannelType.Text);
        _member = new User { Username = "regularuser", Email = "u@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(_member);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = _server.Id, UserId = _member.Id, Role = ServerRole.Member });
        await _testDb.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task SetSlowmode_Admin_Succeeds()
    {
        await SeedAsync();
        TestHelpers.SetUser(_serversCtrl, _admin.Id, _admin.Username);

        var result = await _serversCtrl.SetSlowmode(_server.Id, _channel.Id, new SetSlowmodeRequest(30));

        Assert.IsType<NoContentResult>(result);
        var updated = _testDb.Db.Channels.Find(_channel.Id);
        Assert.Equal(30, updated!.SlowmodeSeconds);
    }

    [Fact]
    public async Task SetSlowmode_NonAdmin_ReturnsForbid()
    {
        await SeedAsync();
        TestHelpers.SetUser(_serversCtrl, _member.Id, _member.Username);

        var result = await _serversCtrl.SetSlowmode(_server.Id, _channel.Id, new SetSlowmodeRequest(10));

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task SetSlowmode_NegativeSeconds_ReturnsBadRequest()
    {
        await SeedAsync();
        TestHelpers.SetUser(_serversCtrl, _admin.Id, _admin.Username);

        var result = await _serversCtrl.SetSlowmode(_server.Id, _channel.Id, new SetSlowmodeRequest(-1));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SetSlowmode_ExceedsMax_ReturnsBadRequest()
    {
        await SeedAsync();
        TestHelpers.SetUser(_serversCtrl, _admin.Id, _admin.Username);

        var result = await _serversCtrl.SetSlowmode(_server.Id, _channel.Id, new SetSlowmodeRequest(21601));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SetSlowmode_BroadcastsSignalR()
    {
        await SeedAsync();
        TestHelpers.SetUser(_serversCtrl, _admin.Id, _admin.Username);

        await _serversCtrl.SetSlowmode(_server.Id, _channel.Id, new SetSlowmodeRequest(60));

        _hubMock.Verify(h => h.Clients.Group($"server-{_server.Id}"), Times.AtLeastOnce);
    }

    [Fact]
    public async Task SendMessage_SlowmodeActive_MemberThrottled()
    {
        await SeedAsync();
        _channel.SlowmodeSeconds = 30;
        await _testDb.Db.SaveChangesAsync();

        TestHelpers.SetUser(_messagesCtrl, _member.Id, _member.Username);

        // First message succeeds
        var first = await _messagesCtrl.SendMessage(_channel.Id,
            new SendMessageRequest("First message"));
        Assert.IsType<OkObjectResult>(first.Result);

        // Second message immediately — should be throttled (429)
        var second = await _messagesCtrl.SendMessage(_channel.Id,
            new SendMessageRequest("Too soon"));
        var status = Assert.IsType<ObjectResult>(second.Result);
        Assert.Equal(429, status.StatusCode);
    }

    [Fact]
    public async Task SendMessage_SlowmodeActive_AdminExempt()
    {
        await SeedAsync();
        _channel.SlowmodeSeconds = 30;
        await _testDb.Db.SaveChangesAsync();

        TestHelpers.SetUser(_messagesCtrl, _admin.Id, _admin.Username);

        var first = await _messagesCtrl.SendMessage(_channel.Id, new SendMessageRequest("First"));
        Assert.IsType<OkObjectResult>(first.Result);

        var second = await _messagesCtrl.SendMessage(_channel.Id, new SendMessageRequest("Second"));
        Assert.IsType<OkObjectResult>(second.Result);
    }

    [Fact]
    public async Task SendMessage_NoSlowmode_MemberSendsTwice()
    {
        await SeedAsync();
        TestHelpers.SetUser(_messagesCtrl, _member.Id, _member.Username);

        var first = await _messagesCtrl.SendMessage(_channel.Id, new SendMessageRequest("First"));
        Assert.IsType<OkObjectResult>(first.Result);

        var second = await _messagesCtrl.SendMessage(_channel.Id, new SendMessageRequest("Second"));
        Assert.IsType<OkObjectResult>(second.Result);
    }
}
