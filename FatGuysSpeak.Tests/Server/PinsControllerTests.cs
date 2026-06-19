using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace FatGuysSpeak.Tests.Server;

public class PinsControllerTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly List<(string Group, string Method, object[] Args)> _hubCalls = [];
    private readonly IHubContext<FatGuysSpeak.Server.Hubs.ChatHub> _hub;

    public PinsControllerTests()
    {
        _testDb = new TestDb();

        string lastGroup = "";
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
             .Callback<string, object[], CancellationToken>((m, a, _) => _hubCalls.Add((lastGroup, m, a)))
             .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>()))
               .Callback<string>(g => lastGroup = g)
               .Returns(proxy.Object);
        var hub = new Mock<IHubContext<FatGuysSpeak.Server.Hubs.ChatHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        _hub = hub.Object;
    }

    public void Dispose() => _testDb.Dispose();

    private PinsController MakeController(int userId, string username)
    {
        var ctrl = new PinsController(_testDb.Db, _hub);
        TestHelpers.SetUser(ctrl, userId, username);
        return ctrl;
    }

    private async Task<(GuildServer server, Channel channel, User mod, Message msg)> SeedChannelAsync()
    {
        var (server, mod) = await TestHelpers.SeedServerAsync(_testDb.Db, "pins");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id
                                                   && c.Type == ChannelType.Text);
        var msg = new Message
        {
            ChannelId = channel.Id,
            AuthorId = mod.Id,
            Content = "pin me",
            Source = MessageSource.Text
        };
        _testDb.Db.Messages.Add(msg);
        await _testDb.Db.SaveChangesAsync();
        // EF needs Author for message navigation
        _testDb.Db.Entry(msg).Reference(m => m.Author).Load();
        return (server, channel, mod, msg);
    }

    // ── Channel pins ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PinChannelMessage_Admin_ReturnsOk()
    {
        var (_, channel, mod, msg) = await SeedChannelAsync();
        var result = await MakeController(mod.Id, mod.Username).PinChannelMessage(channel.Id, msg.Id);
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task PinChannelMessage_Admin_PersistsPin()
    {
        var (_, channel, mod, msg) = await SeedChannelAsync();
        await MakeController(mod.Id, mod.Username).PinChannelMessage(channel.Id, msg.Id);
        Assert.True(_testDb.Db.PinnedMessages.Any(p => p.MessageId == msg.Id));
    }

    [Fact]
    public async Task PinChannelMessage_Admin_BroadcastsMessagePinned()
    {
        var (_, channel, mod, msg) = await SeedChannelAsync();
        _hubCalls.Clear();
        await MakeController(mod.Id, mod.Username).PinChannelMessage(channel.Id, msg.Id);
        Assert.Contains(_hubCalls, c => c.Method == "MessagePinned"
                                     && c.Group == $"channel-{channel.Id}");
    }

    [Fact]
    public async Task PinChannelMessage_MessagePinned_ArgsContainMessageIdAndChannelId()
    {
        var (_, channel, mod, msg) = await SeedChannelAsync();
        _hubCalls.Clear();
        await MakeController(mod.Id, mod.Username).PinChannelMessage(channel.Id, msg.Id);
        var call = _hubCalls.First(c => c.Method == "MessagePinned");
        Assert.Equal(msg.Id,     call.Args[0]);
        Assert.Equal(channel.Id, call.Args[1]);
    }

    [Fact]
    public async Task PinChannelMessage_NonMember_ReturnsForbid()
    {
        var (_, channel, _, msg) = await SeedChannelAsync();
        var outsider = new User { Username = "outsider-p", Email = "out-p@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(outsider);
        await _testDb.Db.SaveChangesAsync();
        var result = await MakeController(outsider.Id, "outsider-p").PinChannelMessage(channel.Id, msg.Id);
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task PinChannelMessage_Idempotent_DoesNotDuplicate()
    {
        var (_, channel, mod, msg) = await SeedChannelAsync();
        await MakeController(mod.Id, mod.Username).PinChannelMessage(channel.Id, msg.Id);
        await MakeController(mod.Id, mod.Username).PinChannelMessage(channel.Id, msg.Id);
        Assert.Equal(1, _testDb.Db.PinnedMessages.Count(p => p.MessageId == msg.Id));
    }

    [Fact]
    public async Task UnpinChannelMessage_ExistingPin_ReturnsNoContent()
    {
        var (_, channel, mod, msg) = await SeedChannelAsync();
        await MakeController(mod.Id, mod.Username).PinChannelMessage(channel.Id, msg.Id);
        var result = await MakeController(mod.Id, mod.Username).UnpinChannelMessage(channel.Id, msg.Id);
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task UnpinChannelMessage_RemovesPin()
    {
        var (_, channel, mod, msg) = await SeedChannelAsync();
        await MakeController(mod.Id, mod.Username).PinChannelMessage(channel.Id, msg.Id);
        await MakeController(mod.Id, mod.Username).UnpinChannelMessage(channel.Id, msg.Id);
        Assert.False(_testDb.Db.PinnedMessages.Any(p => p.MessageId == msg.Id));
    }

    [Fact]
    public async Task UnpinChannelMessage_BroadcastsMessageUnpinned()
    {
        var (_, channel, mod, msg) = await SeedChannelAsync();
        await MakeController(mod.Id, mod.Username).PinChannelMessage(channel.Id, msg.Id);
        _hubCalls.Clear();
        await MakeController(mod.Id, mod.Username).UnpinChannelMessage(channel.Id, msg.Id);
        Assert.Contains(_hubCalls, c => c.Method == "MessageUnpinned"
                                     && c.Group == $"channel-{channel.Id}");
    }

    [Fact]
    public async Task UnpinChannelMessage_NonExistent_ReturnsNotFound()
    {
        var (_, channel, mod, msg) = await SeedChannelAsync();
        var result = await MakeController(mod.Id, mod.Username).UnpinChannelMessage(channel.Id, msg.Id);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetChannelPins_ReturnsPinnedMessages()
    {
        var (_, channel, mod, msg) = await SeedChannelAsync();
        await MakeController(mod.Id, mod.Username).PinChannelMessage(channel.Id, msg.Id);
        var result = await MakeController(mod.Id, mod.Username).GetChannelPins(channel.Id);
        var ok   = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<MessageDto>>(ok.Value);
        Assert.Single(list);
        Assert.Equal(msg.Id, list[0].Id);
        Assert.True(list[0].IsPinned);
    }

    [Fact]
    public async Task GetChannelPins_EmptyWhenNonePinned()
    {
        var (_, channel, mod, _) = await SeedChannelAsync();
        var result = await MakeController(mod.Id, mod.Username).GetChannelPins(channel.Id);
        var ok   = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<MessageDto>>(ok.Value);
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetChannelPins_ExcludesSoftDeletedMessages()
    {
        var (_, channel, mod, _) = await SeedChannelAsync();

        // Add two messages and pin both.
        var live = new Message { ChannelId = channel.Id, AuthorId = mod.Id, Content = "live message", Source = MessageSource.Text };
        var dead = new Message { ChannelId = channel.Id, AuthorId = mod.Id, Content = "deleted message", Source = MessageSource.Text };
        _testDb.Db.Messages.AddRange(live, dead);
        await _testDb.Db.SaveChangesAsync();

        _testDb.Db.PinnedMessages.Add(new PinnedMessage { MessageId = live.Id, ChannelId = channel.Id, PinnedById = mod.Id });
        _testDb.Db.PinnedMessages.Add(new PinnedMessage { MessageId = dead.Id, ChannelId = channel.Id, PinnedById = mod.Id });
        await _testDb.Db.SaveChangesAsync();

        // Soft-delete one message (content is preserved per the soft-delete design).
        dead.IsDeleted = true;
        await _testDb.Db.SaveChangesAsync();

        var result = await MakeController(mod.Id, mod.Username).GetChannelPins(channel.Id);
        var ok   = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<MessageDto>>(ok.Value);

        Assert.Single(list);
        Assert.Equal(live.Id, list[0].Id);
        Assert.DoesNotContain(list, m => m.Id == dead.Id);
    }

    // ── DM pins ───────────────────────────────────────────────────────────────

    private async Task<(User alice, User bob, DirectConversationDto convo, DirectMessageDto msg)> SeedDmAsync()
    {
        var alice = new User { Username = "alice-pin", Email = "alice-pin@test.com", PasswordHash = "*" };
        var bob   = new User { Username = "bob-pin",   Email = "bob-pin@test.com",   PasswordHash = "*" };
        _testDb.Db.Users.AddRange(alice, bob);
        await _testDb.Db.SaveChangesAsync();

        var dmCtrl = new DirectMessagesController(_testDb.Db, _hub);
        TestHelpers.SetUser(dmCtrl, alice.Id, "alice-pin");
        var convoResult = await dmCtrl.OpenConversation(bob.Id);
        var convo = (DirectConversationDto)((OkObjectResult)convoResult.Result!).Value!;

        var msgResult = await dmCtrl.SendMessage(convo.Id, new SendDirectMessageRequest("dm pin me"));
        var msg = (DirectMessageDto)((OkObjectResult)msgResult.Result!).Value!;

        return (alice, bob, convo, msg);
    }

    [Fact]
    public async Task PinDmMessage_Participant_ReturnsOk()
    {
        var (alice, _, convo, msg) = await SeedDmAsync();
        var result = await MakeController(alice.Id, "alice-pin").PinDmMessage(convo.Id, msg.Id);
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task PinDmMessage_NonParticipant_ReturnsForbid()
    {
        var (_, _, convo, msg) = await SeedDmAsync();
        var carol = new User { Username = "carol-pin", Email = "carol-pin@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(carol);
        await _testDb.Db.SaveChangesAsync();
        var result = await MakeController(carol.Id, "carol-pin").PinDmMessage(convo.Id, msg.Id);
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task UnpinDmMessage_ExistingPin_ReturnsNoContent()
    {
        var (alice, _, convo, msg) = await SeedDmAsync();
        await MakeController(alice.Id, "alice-pin").PinDmMessage(convo.Id, msg.Id);
        var result = await MakeController(alice.Id, "alice-pin").UnpinDmMessage(convo.Id, msg.Id);
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task GetDmPins_ReturnsPinnedDmMessages()
    {
        var (alice, _, convo, msg) = await SeedDmAsync();
        await MakeController(alice.Id, "alice-pin").PinDmMessage(convo.Id, msg.Id);
        var result = await MakeController(alice.Id, "alice-pin").GetDmPins(convo.Id);
        var ok   = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<DirectMessageDto>>(ok.Value);
        Assert.Single(list);
        Assert.Equal(msg.Id, list[0].Id);
        Assert.True(list[0].IsPinned);
    }
}
