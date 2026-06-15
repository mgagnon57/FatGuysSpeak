using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace FatGuysSpeak.Tests.Server;

public class DirectMessagesTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly List<(string Method, object[] Args)> _hubCalls = [];
    private readonly IHubContext<FatGuysSpeak.Server.Hubs.ChatHub> _hub;

    public DirectMessagesTests()
    {
        _testDb = new TestDb();

        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
             .Callback<string, object[], CancellationToken>((m, a, _) => _hubCalls.Add((m, a)))
             .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
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

    private async Task<(User alice, User bob)> SeedUsersAsync()
    {
        var alice = new User { Username = "alice", Email = "alice@test.com", PasswordHash = "*" };
        var bob   = new User { Username = "bob",   Email = "bob@test.com",   PasswordHash = "*" };
        _testDb.Db.Users.AddRange(alice, bob);
        await _testDb.Db.SaveChangesAsync();
        return (alice, bob);
    }

    // ── OpenConversation ──────────────────────────────────────────────────────

    [Fact]
    public async Task OpenConversation_NewPair_CreatesConversation()
    {
        var (alice, bob) = await SeedUsersAsync();
        var result = await MakeController(alice.Id, "alice").OpenConversation(bob.Id);

        var ok  = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<DirectConversationDto>(ok.Value);
        Assert.Equal(bob.Id, dto.OtherUserId);
        Assert.Equal("bob", dto.OtherUsername);
    }

    [Fact]
    public async Task OpenConversation_Idempotent_ReturnsSameId()
    {
        var (alice, bob) = await SeedUsersAsync();
        var c1 = await MakeController(alice.Id, "alice").OpenConversation(bob.Id);
        var c2 = await MakeController(alice.Id, "alice").OpenConversation(bob.Id);

        var id1 = ((DirectConversationDto)((OkObjectResult)c1.Result!).Value!).Id;
        var id2 = ((DirectConversationDto)((OkObjectResult)c2.Result!).Value!).Id;
        Assert.Equal(id1, id2);
    }

    [Fact]
    public async Task OpenConversation_BothDirections_ReturnsSameId()
    {
        var (alice, bob) = await SeedUsersAsync();
        var fromAlice = await MakeController(alice.Id, "alice").OpenConversation(bob.Id);
        var fromBob   = await MakeController(bob.Id,   "bob").OpenConversation(alice.Id);

        var id1 = ((DirectConversationDto)((OkObjectResult)fromAlice.Result!).Value!).Id;
        var id2 = ((DirectConversationDto)((OkObjectResult)fromBob.Result!).Value!).Id;
        Assert.Equal(id1, id2);
    }

    [Fact]
    public async Task OpenConversation_Self_ReturnsBadRequest()
    {
        var (alice, _) = await SeedUsersAsync();
        var result = await MakeController(alice.Id, "alice").OpenConversation(alice.Id);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task OpenConversation_NonExistentUser_ReturnsNotFound()
    {
        var (alice, _) = await SeedUsersAsync();
        var result = await MakeController(alice.Id, "alice").OpenConversation(99999);
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // ── GetMessages ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMessages_Participant_ReturnsMessages()
    {
        var (alice, bob) = await SeedUsersAsync();
        var convoDto = ((OkObjectResult)(await MakeController(alice.Id, "alice").OpenConversation(bob.Id)).Result!).Value as DirectConversationDto;
        await MakeController(alice.Id, "alice").SendMessage(convoDto!.Id, new SendDirectMessageRequest("Hello Bob"));
        await MakeController(bob.Id, "bob").SendMessage(convoDto!.Id, new SendDirectMessageRequest("Hi Alice"));

        var result = await MakeController(alice.Id, "alice").GetMessages(convoDto.Id);

        var ok   = Assert.IsType<OkObjectResult>(result.Result);
        var msgs = Assert.IsType<List<DirectMessageDto>>(ok.Value);
        Assert.Equal(2, msgs.Count);
        Assert.Equal("Hello Bob", msgs[0].Content);
        Assert.Equal("Hi Alice",  msgs[1].Content);
    }

    [Fact]
    public async Task GetMessages_NonParticipant_ReturnsForbid()
    {
        var (alice, bob) = await SeedUsersAsync();
        var carol = new User { Username = "carol", Email = "carol@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(carol);
        await _testDb.Db.SaveChangesAsync();

        var convoDto = ((OkObjectResult)(await MakeController(alice.Id, "alice").OpenConversation(bob.Id)).Result!).Value as DirectConversationDto;
        var result = await MakeController(carol.Id, "carol").GetMessages(convoDto!.Id);
        Assert.IsType<ForbidResult>(result.Result);
    }

    // ── SendMessage ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SendMessage_ValidContent_Persisted()
    {
        var (alice, bob) = await SeedUsersAsync();
        var convoDto = ((OkObjectResult)(await MakeController(alice.Id, "alice").OpenConversation(bob.Id)).Result!).Value as DirectConversationDto;

        var result = await MakeController(alice.Id, "alice").SendMessage(convoDto!.Id, new SendDirectMessageRequest("Hey!"));

        var ok  = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<DirectMessageDto>(ok.Value);
        Assert.Equal("Hey!", dto.Content);
        Assert.Equal(alice.Id, dto.AuthorId);
        Assert.Equal("alice", dto.AuthorUsername);
        Assert.Equal(convoDto.Id, dto.ConversationId);
    }

    [Fact]
    public async Task SendMessage_ValidContent_BroadcastsToBothUsers()
    {
        var (alice, bob) = await SeedUsersAsync();
        var convoDto = ((OkObjectResult)(await MakeController(alice.Id, "alice").OpenConversation(bob.Id)).Result!).Value as DirectConversationDto;
        _hubCalls.Clear();

        await MakeController(alice.Id, "alice").SendMessage(convoDto!.Id, new SendDirectMessageRequest("Ping!"));

        Assert.Equal(2, _hubCalls.Count(c => c.Method == "ReceiveDirectMessage"));
    }

    [Fact]
    public async Task SendMessage_EmptyContent_ReturnsBadRequest()
    {
        var (alice, bob) = await SeedUsersAsync();
        var convoDto = ((OkObjectResult)(await MakeController(alice.Id, "alice").OpenConversation(bob.Id)).Result!).Value as DirectConversationDto;

        var result = await MakeController(alice.Id, "alice").SendMessage(convoDto!.Id, new SendDirectMessageRequest(""));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SendMessage_ContentTooLong_ReturnsBadRequest()
    {
        var (alice, bob) = await SeedUsersAsync();
        var convoDto = ((OkObjectResult)(await MakeController(alice.Id, "alice").OpenConversation(bob.Id)).Result!).Value as DirectConversationDto;

        var result = await MakeController(alice.Id, "alice").SendMessage(
            convoDto!.Id, new SendDirectMessageRequest(new string('x', 2001)));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SendMessage_NonParticipant_ReturnsForbid()
    {
        var (alice, bob) = await SeedUsersAsync();
        var carol = new User { Username = "carol2", Email = "carol2@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(carol);
        await _testDb.Db.SaveChangesAsync();

        var convoDto = ((OkObjectResult)(await MakeController(alice.Id, "alice").OpenConversation(bob.Id)).Result!).Value as DirectConversationDto;
        var result = await MakeController(carol.Id, "carol2").SendMessage(convoDto!.Id, new SendDirectMessageRequest("Intruder!"));
        Assert.IsType<ForbidResult>(result.Result);
    }

    // ── DeleteMessage ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteMessage_OwnMessage_SoftDeletes()
    {
        var (alice, bob) = await SeedUsersAsync();
        var convoDto = ((OkObjectResult)(await MakeController(alice.Id, "alice").OpenConversation(bob.Id)).Result!).Value as DirectConversationDto;
        var msgDto = ((OkObjectResult)(await MakeController(alice.Id, "alice").SendMessage(convoDto!.Id, new SendDirectMessageRequest("To delete"))).Result!).Value as DirectMessageDto;

        var deleteResult = await MakeController(alice.Id, "alice").DeleteMessage(convoDto.Id, msgDto!.Id);
        Assert.IsType<NoContentResult>(deleteResult);

        var db = _testDb.Db;
        var msg = await db.DirectMessages.FindAsync(msgDto.Id);
        Assert.True(msg!.IsDeleted);
    }

    [Fact]
    public async Task DeleteMessage_OtherUsersMessage_ReturnsForbid()
    {
        var (alice, bob) = await SeedUsersAsync();
        var convoDto = ((OkObjectResult)(await MakeController(alice.Id, "alice").OpenConversation(bob.Id)).Result!).Value as DirectConversationDto;
        var msgDto = ((OkObjectResult)(await MakeController(alice.Id, "alice").SendMessage(convoDto!.Id, new SendDirectMessageRequest("Alice's msg"))).Result!).Value as DirectMessageDto;

        var result = await MakeController(bob.Id, "bob").DeleteMessage(convoDto.Id, msgDto!.Id);
        Assert.IsType<ForbidResult>(result);
    }

    // ── GetConversations ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetConversations_ReturnsAllForUser()
    {
        var (alice, bob) = await SeedUsersAsync();
        var carol = new User { Username = "carol3", Email = "carol3@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(carol);
        await _testDb.Db.SaveChangesAsync();

        await MakeController(alice.Id, "alice").OpenConversation(bob.Id);
        await MakeController(alice.Id, "alice").OpenConversation(carol.Id);

        var result = await MakeController(alice.Id, "alice").GetConversations();
        var ok     = Assert.IsType<OkObjectResult>(result.Result);
        var list   = Assert.IsType<List<DirectConversationDto>>(ok.Value);
        Assert.Equal(2, list.Count);
        Assert.Contains(list, dc => dc.OtherUsername == "bob");
        Assert.Contains(list, dc => dc.OtherUsername == "carol3");
    }

    [Fact]
    public async Task GetConversations_DoesNotReturnOtherUsersConversations()
    {
        var (alice, bob) = await SeedUsersAsync();
        var carol = new User { Username = "carol4", Email = "carol4@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(carol);
        await _testDb.Db.SaveChangesAsync();

        await MakeController(alice.Id, "alice").OpenConversation(bob.Id);

        var result = await MakeController(carol.Id, "carol4").GetConversations();
        var list   = Assert.IsType<List<DirectConversationDto>>(((OkObjectResult)result.Result!).Value!);
        Assert.Empty(list);
    }
}
