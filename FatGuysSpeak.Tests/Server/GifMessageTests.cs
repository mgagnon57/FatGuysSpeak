using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;
using FatGuysSpeak.Server.Hubs;

namespace FatGuysSpeak.Tests.Server;

public class GifMessageTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly MessagesController _controller;
    private GuildServer _server = null!;
    private Channel _textChannel = null!;
    private User _user = null!;

    public GifMessageTests()
    {
        _testDb = new TestDb();

        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<ChatHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        _controller = new MessagesController(_testDb.Db, hub.Object, new ServerMetricsService(), TestHelpers.NullBot());
    }

    public void Dispose() => _testDb.Dispose();

    private async Task SeedAsync()
    {
        (_server, _user) = await TestHelpers.SeedServerAsync(_testDb.Db);
        _textChannel = _testDb.Db.Channels.First(c => c.ServerId == _server.Id && c.Type == ChannelType.Text);
        TestHelpers.SetUser(_controller, _user.Id, _user.Username);
    }

    // ── GIF URL allowed ───────────────────────────────────────────────────────

    [Fact]
    public async Task SendMessage_WithGiphyUrl_Succeeds()
    {
        await SeedAsync();
        var req = new SendMessageRequest("", MessageSource.Text, "https://media.giphy.com/media/abc/giphy.gif");

        var result = await _controller.SendMessage(_textChannel.Id, req);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task SendMessage_WithGiphySubdomainUrl_Succeeds()
    {
        await SeedAsync();
        var req = new SendMessageRequest("", MessageSource.Text, "https://media2.giphy.com/media/abc/giphy.gif");

        var result = await _controller.SendMessage(_textChannel.Id, req);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task SendMessage_GiphyUrl_StoresAttachmentUrlOnMessage()
    {
        await SeedAsync();
        const string gifUrl = "https://media.giphy.com/media/xyz/giphy.gif";
        var req = new SendMessageRequest("", MessageSource.Text, gifUrl);

        await _controller.SendMessage(_textChannel.Id, req);

        var stored = _testDb.Db.Messages.First(m => m.ChannelId == _textChannel.Id);
        Assert.Equal(gifUrl, stored.AttachmentUrl);
    }

    [Fact]
    public async Task SendMessage_GiphyUrl_EmptyContent_IsAllowed()
    {
        await SeedAsync();
        var req = new SendMessageRequest("", MessageSource.Text, "https://media.giphy.com/media/abc/giphy.gif");

        var result = await _controller.SendMessage(_textChannel.Id, req);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task SendMessage_GiphyUrl_WithCaption_IsAllowed()
    {
        await SeedAsync();
        var req = new SendMessageRequest("lol", MessageSource.Text, "https://media.giphy.com/media/abc/giphy.gif");

        var result = await _controller.SendMessage(_textChannel.Id, req);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // ── Non-Giphy external URLs still blocked ─────────────────────────────────

    [Fact]
    public async Task SendMessage_ArbitraryExternalUrl_ReturnsError()
    {
        await SeedAsync();
        var req = new SendMessageRequest("", MessageSource.Text, "https://evil.com/malicious.gif");

        var result = await _controller.SendMessage(_textChannel.Id, req);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SendMessage_HttpGiphyUrl_ReturnsError()
    {
        await SeedAsync();
        // Giphy URL over plain http (not https) must be rejected
        var req = new SendMessageRequest("", MessageSource.Text, "http://media.giphy.com/media/abc/giphy.gif");

        var result = await _controller.SendMessage(_textChannel.Id, req);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SendMessage_FakeGiphyDomain_ReturnsError()
    {
        await SeedAsync();
        // Domain that contains "giphy.com" but is not actually giphy.com
        var req = new SendMessageRequest("", MessageSource.Text, "https://evil-giphy.com/media/abc/giphy.gif");

        var result = await _controller.SendMessage(_textChannel.Id, req);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SendMessage_NoContentNoAttachment_ReturnsError()
    {
        await SeedAsync();
        var req = new SendMessageRequest("", MessageSource.Text, null);

        var result = await _controller.SendMessage(_textChannel.Id, req);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ── Upload URL still accepted ─────────────────────────────────────────────

    [Fact]
    public async Task SendMessage_UploadUrl_StillAccepted()
    {
        await SeedAsync();
        var req = new SendMessageRequest("", MessageSource.Text, "/uploads/image.png");

        var result = await _controller.SendMessage(_textChannel.Id, req);

        Assert.IsType<OkObjectResult>(result.Result);
    }
}
