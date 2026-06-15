using System.Reflection;
using System.Security.Claims;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace FatGuysSpeak.Tests.Server;

[Collection("HubTests")]
public class DmTypingTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly Mock<IHubCallerClients> _mockClients;
    private readonly Mock<IGroupManager> _mockGroups;
    private readonly Dictionary<string, List<(string Method, object[] Args)>> _sent = new();

    public DmTypingTests()
    {
        _testDb = new TestDb();

        _mockClients = new Mock<IHubCallerClients>();
        _mockClients.Setup(c => c.Group(It.IsAny<string>()))
            .Returns<string>(g => TrackingProxy($"group:{g}"));
        _mockClients.Setup(c => c.Caller)
            .Returns(SingleTrackingProxy("caller"));

        _mockGroups = new Mock<IGroupManager>();
        _mockGroups
            .Setup(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockGroups
            .Setup(g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        ClearHubStaticState();
        _testDb.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private IClientProxy TrackingProxy(string target)
    {
        var proxy = new Mock<IClientProxy>();
        proxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) => Track(target, method, args))
            .Returns(Task.CompletedTask);
        return proxy.Object;
    }

    private ISingleClientProxy SingleTrackingProxy(string target)
    {
        var proxy = new Mock<ISingleClientProxy>();
        proxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, _) => Track(target, method, args))
            .Returns(Task.CompletedTask);
        return proxy.Object;
    }

    private void Track(string target, string method, object[] args)
    {
        if (!_sent.TryGetValue(target, out var list))
            _sent[target] = list = [];
        list.Add((method, args));
    }

    private ChatHub CreateHub(int userId, string username, string connectionId = "conn1")
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, username)
        };
        var mockCtx = new Mock<HubCallerContext>();
        mockCtx.Setup(c => c.ConnectionId).Returns(connectionId);
        mockCtx.Setup(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));

        var hub = new ChatHub(_testDb.Db);
        hub.Context = mockCtx.Object;
        hub.Clients = _mockClients.Object;
        hub.Groups = _mockGroups.Object;
        return hub;
    }

    private bool WasSentTo(string target, string method) =>
        _sent.TryGetValue(target, out var list) && list.Any(m => m.Method == method);

    private (string Method, object[] Args) GetLastSent(string target, string method) =>
        _sent[target].Last(m => m.Method == method);

    private static void ClearHubStaticState()
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        foreach (var name in new[] { "ActiveStreamers", "VoiceChannelMap", "UserTextChannelMap",
                                     "ChannelOccupants", "OnlineUsers", "ActiveCameras" })
        {
            var field = typeof(ChatHub).GetField(name, flags);
            (field?.GetValue(null) as System.Collections.IDictionary)?.Clear();
        }
    }

    private async Task<(User alice, User bob, DirectConversation convo)> SeedConversationAsync()
    {
        var alice = new User { Username = "alice", Email = "alice-dmt@test.com", PasswordHash = "*" };
        var bob   = new User { Username = "bob",   Email = "bob-dmt@test.com",   PasswordHash = "*" };
        _testDb.Db.Users.AddRange(alice, bob);
        await _testDb.Db.SaveChangesAsync();

        int u1 = Math.Min(alice.Id, bob.Id);
        int u2 = Math.Max(alice.Id, bob.Id);
        var convo = new DirectConversation { User1Id = u1, User2Id = u2 };
        _testDb.Db.DirectConversations.Add(convo);
        await _testDb.Db.SaveChangesAsync();

        return (alice, bob, convo);
    }

    // ── StartDmTyping ─────────────────────────────────────────────────────────

    [Fact]
    public async Task StartDmTyping_ValidConversation_SendsDmUserTypingToRecipient()
    {
        var (alice, bob, convo) = await SeedConversationAsync();
        var hub = CreateHub(alice.Id, "alice");

        await hub.StartDmTyping(convo.Id);

        Assert.True(WasSentTo($"group:user-{bob.Id}", "DmUserTyping"),
            "StartDmTyping must push DmUserTyping to the recipient's personal user group");
    }

    [Fact]
    public async Task StartDmTyping_ValidConversation_IncludesCorrectArgs()
    {
        var (alice, bob, convo) = await SeedConversationAsync();
        var hub = CreateHub(alice.Id, "alice");

        await hub.StartDmTyping(convo.Id);

        var (_, args) = GetLastSent($"group:user-{bob.Id}", "DmUserTyping");
        Assert.Equal(alice.Id, args[0]);
        Assert.Equal("alice",  args[1]);
        Assert.Equal(convo.Id, args[2]);
    }

    [Fact]
    public async Task StartDmTyping_ValidConversation_DoesNotNotifySender()
    {
        var (alice, bob, convo) = await SeedConversationAsync();
        var hub = CreateHub(alice.Id, "alice");

        await hub.StartDmTyping(convo.Id);

        Assert.False(WasSentTo($"group:user-{alice.Id}", "DmUserTyping"),
            "StartDmTyping must not push DmUserTyping to the caller's own group");
    }

    [Fact]
    public async Task StartDmTyping_UnknownConversation_SendsNothing()
    {
        var (alice, _, _) = await SeedConversationAsync();
        var hub = CreateHub(alice.Id, "alice");

        await hub.StartDmTyping(99999);

        Assert.Empty(_sent);
    }

    [Fact]
    public async Task StartDmTyping_NonParticipant_SendsNothing()
    {
        var (alice, bob, convo) = await SeedConversationAsync();
        var carol = new User { Username = "carol", Email = "carol-dmt@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(carol);
        await _testDb.Db.SaveChangesAsync();

        var hub = CreateHub(carol.Id, "carol");
        await hub.StartDmTyping(convo.Id);

        Assert.False(WasSentTo($"group:user-{alice.Id}", "DmUserTyping"));
        Assert.False(WasSentTo($"group:user-{bob.Id}",   "DmUserTyping"));
    }

    [Fact]
    public async Task StartDmTyping_User2Typing_SendsToUser1Group()
    {
        var (alice, bob, convo) = await SeedConversationAsync();
        // bob is User2 when bob.Id > alice.Id; verify the reverse direction works
        int user1Id = convo.User1Id;
        int user2Id = convo.User2Id;
        var (typerId, typerName, recipientId) = user2Id == bob.Id
            ? (bob.Id, "bob", alice.Id)
            : (alice.Id, "alice", bob.Id);

        var hub = CreateHub(typerId, typerName, "conn-u2");
        await hub.StartDmTyping(convo.Id);

        Assert.True(WasSentTo($"group:user-{recipientId}", "DmUserTyping"),
            "StartDmTyping must correctly route regardless of User1/User2 order");
    }

    // ── StopDmTyping ──────────────────────────────────────────────────────────

    [Fact]
    public async Task StopDmTyping_ValidConversation_SendsDmUserStoppedTypingToRecipient()
    {
        var (alice, bob, convo) = await SeedConversationAsync();
        var hub = CreateHub(alice.Id, "alice");

        await hub.StopDmTyping(convo.Id);

        Assert.True(WasSentTo($"group:user-{bob.Id}", "DmUserStoppedTyping"),
            "StopDmTyping must push DmUserStoppedTyping to the recipient's personal user group");
    }

    [Fact]
    public async Task StopDmTyping_ValidConversation_IncludesCorrectArgs()
    {
        var (alice, bob, convo) = await SeedConversationAsync();
        var hub = CreateHub(alice.Id, "alice");

        await hub.StopDmTyping(convo.Id);

        var (_, args) = GetLastSent($"group:user-{bob.Id}", "DmUserStoppedTyping");
        Assert.Equal(alice.Id, args[0]);
        Assert.Equal(convo.Id, args[1]);
    }

    [Fact]
    public async Task StopDmTyping_UnknownConversation_SendsNothing()
    {
        var (alice, _, _) = await SeedConversationAsync();
        var hub = CreateHub(alice.Id, "alice");

        await hub.StopDmTyping(99999);

        Assert.Empty(_sent);
    }

    [Fact]
    public async Task StopDmTyping_NonParticipant_SendsNothing()
    {
        var (alice, bob, convo) = await SeedConversationAsync();
        var carol = new User { Username = "carol2", Email = "carol2-dmt@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(carol);
        await _testDb.Db.SaveChangesAsync();

        var hub = CreateHub(carol.Id, "carol2");
        await hub.StopDmTyping(convo.Id);

        Assert.False(WasSentTo($"group:user-{alice.Id}", "DmUserStoppedTyping"));
        Assert.False(WasSentTo($"group:user-{bob.Id}",   "DmUserStoppedTyping"));
    }
}
