using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace FatGuysSpeak.Tests.Server;

public class DmReadReceiptTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly List<(string Group, string Method, object[] Args)> _hubCalls = [];
    private readonly IHubContext<FatGuysSpeak.Server.Hubs.ChatHub> _hub;

    public DmReadReceiptTests()
    {
        _testDb = new TestDb();

        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
             .Callback<string, object[], CancellationToken>((m, a, _) => _hubCalls.Add(("?", m, a)))
             .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>()))
               .Callback<string>(g =>
               {
                   proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                        .Callback<string, object[], CancellationToken>((m, a, _) => _hubCalls.Add((g, m, a)))
                        .Returns(Task.CompletedTask);
               })
               .Returns(proxy.Object);

        var hub = new Mock<IHubContext<FatGuysSpeak.Server.Hubs.ChatHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        _hub = hub.Object;
    }

    public void Dispose() => _testDb.Dispose();

    private DirectMessagesController MakeController(int userId, string username)
    {
        var ctrl = new DirectMessagesController(_testDb.Db, _hub);
        TestHelpers.SetUser(ctrl, userId, username);
        return ctrl;
    }

    private async Task<(User alice, User bob, DirectConversationDto convo)> SeedConvoAsync()
    {
        var alice = new User { Username = "alice", Email = "alice-rr@test.com", PasswordHash = "*" };
        var bob   = new User { Username = "bob",   Email = "bob-rr@test.com",   PasswordHash = "*" };
        _testDb.Db.Users.AddRange(alice, bob);
        await _testDb.Db.SaveChangesAsync();

        var result = await MakeController(alice.Id, "alice").OpenConversation(bob.Id);
        var convo = (DirectConversationDto)((OkObjectResult)result.Result!).Value!;
        return (alice, bob, convo);
    }

    // ── MarkAsRead ────────────────────────────────────────────────────────────

    [Fact]
    public async Task MarkAsRead_Participant_ReturnsOkWithReadState()
    {
        var (alice, _, convo) = await SeedConvoAsync();

        var result = await MakeController(alice.Id, "alice").MarkAsRead(convo.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<DmReadStateDto>(ok.Value);
        Assert.Equal(convo.Id, dto.ConversationId);
        Assert.True(dto.MyLastReadAt > DateTime.UtcNow.AddSeconds(-5));
    }

    [Fact]
    public async Task MarkAsRead_NonParticipant_ReturnsForbid()
    {
        var (alice, bob, convo) = await SeedConvoAsync();
        var carol = new User { Username = "carol", Email = "carol-rr@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(carol);
        await _testDb.Db.SaveChangesAsync();

        var result = await MakeController(carol.Id, "carol").MarkAsRead(convo.Id);
        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task MarkAsRead_UnknownConversation_ReturnsNotFound()
    {
        var alice = new User { Username = "alice2", Email = "alice2-rr@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(alice);
        await _testDb.Db.SaveChangesAsync();

        var result = await MakeController(alice.Id, "alice2").MarkAsRead(99999);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task MarkAsRead_Persists_ReadRecord()
    {
        var (alice, _, convo) = await SeedConvoAsync();

        await MakeController(alice.Id, "alice").MarkAsRead(convo.Id);

        var record = _testDb.Db.DirectConversationReads
            .FirstOrDefault(r => r.ConversationId == convo.Id && r.UserId == alice.Id);
        Assert.NotNull(record);
        Assert.True(record.LastReadAt > DateTime.UtcNow.AddSeconds(-5));
    }

    [Fact]
    public async Task MarkAsRead_CalledTwice_UpdatesExistingRecord()
    {
        var (alice, _, convo) = await SeedConvoAsync();

        await MakeController(alice.Id, "alice").MarkAsRead(convo.Id);
        await MakeController(alice.Id, "alice").MarkAsRead(convo.Id);

        var count = _testDb.Db.DirectConversationReads
            .Count(r => r.ConversationId == convo.Id && r.UserId == alice.Id);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task MarkAsRead_OtherHasNotRead_OtherUserLastReadAtIsNull()
    {
        var (alice, _, convo) = await SeedConvoAsync();

        var result = await MakeController(alice.Id, "alice").MarkAsRead(convo.Id);

        var dto = (DmReadStateDto)((OkObjectResult)result.Result!).Value!;
        Assert.Null(dto.OtherUserLastReadAt);
    }

    [Fact]
    public async Task MarkAsRead_BothHaveRead_OtherUserLastReadAtIsPopulated()
    {
        var (alice, bob, convo) = await SeedConvoAsync();

        await MakeController(bob.Id, "bob").MarkAsRead(convo.Id);
        var result = await MakeController(alice.Id, "alice").MarkAsRead(convo.Id);

        var dto = (DmReadStateDto)((OkObjectResult)result.Result!).Value!;
        Assert.NotNull(dto.OtherUserLastReadAt);
    }

    [Fact]
    public async Task MarkAsRead_PushesSignalREventToRecipient()
    {
        var (alice, bob, convo) = await SeedConvoAsync();
        _hubCalls.Clear();

        await MakeController(alice.Id, "alice").MarkAsRead(convo.Id);

        Assert.Contains(_hubCalls, c => c.Method == "DmConversationRead"
                                     && c.Group == $"user-{bob.Id}");
    }

    [Fact]
    public async Task MarkAsRead_SignalRPayload_ContainsConversationIdAndReaderId()
    {
        var (alice, bob, convo) = await SeedConvoAsync();
        _hubCalls.Clear();

        await MakeController(alice.Id, "alice").MarkAsRead(convo.Id);

        var call = _hubCalls.First(c => c.Method == "DmConversationRead");
        Assert.Equal(convo.Id, call.Args[0]);
        Assert.Equal(alice.Id, call.Args[1]);
    }
}
