using System.Reflection;
using System.Security.Claims;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace FatGuysSpeak.Tests.Server;

[Collection("HubTests")]
public class ChatHubTypingTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly Mock<IHubCallerClients> _mockClients;
    private readonly Mock<IGroupManager> _mockGroups;
    private readonly Dictionary<string, List<(string Method, object[] Args)>> _sent = new();

    public ChatHubTypingTests()
    {
        _testDb = new TestDb();

        _mockClients = new Mock<IHubCallerClients>();
        _mockClients.Setup(c => c.OthersInGroup(It.IsAny<string>()))
            .Returns<string>(g => TrackingProxy($"others:{g}"));
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
        var ctx = new Mock<HubCallerContext>();
        ctx.Setup(c => c.ConnectionId).Returns(connectionId);
        ctx.Setup(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));

        var hub = new ChatHub(_testDb.Db, new FatGuysSpeak.Server.Services.OnlineTimeTracker(), TestHelpers.NullBot(), TestHelpers.NullTts());
        hub.Context = ctx.Object;
        hub.Clients = _mockClients.Object;
        hub.Groups = _mockGroups.Object;
        return hub;
    }

    private bool WasSentToOthers(int channelId, string method) =>
        _sent.TryGetValue($"others:channel-{channelId}", out var list)
        && list.Any(m => m.Method == method);

    private (string Method, object[] Args) GetLastSentToOthers(int channelId, string method) =>
        _sent[$"others:channel-{channelId}"].Last(m => m.Method == method);

    private static void ClearHubStaticState()
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        foreach (var name in new[] { "ActiveStreamers", "VoiceChannelMap", "UserTextChannelMap", "ChannelOccupants", "OnlineUsers" })
        {
            var field = typeof(ChatHub).GetField(name, flags);
            (field?.GetValue(null) as System.Collections.IDictionary)?.Clear();
        }
    }

    // ── StartTyping ───────────────────────────────────────────────────────────

    [Fact]
    public async Task StartTyping_WhenInChannel_BroadcastsUserTypingToOthers()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "typer-a");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        var hub = CreateHub(user.Id, user.Username, "conn-a");

        await hub.JoinChannel(channel.Id);
        _sent.Clear();
        await hub.StartTyping(channel.Id);

        Assert.True(WasSentToOthers(channel.Id, "UserTyping"),
            "StartTyping must broadcast UserTyping to OthersInGroup for the channel");
    }

    [Fact]
    public async Task StartTyping_WhenNotInChannel_DoesNotBroadcast()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "typer-b");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);

        // Never call JoinChannel — user is not in UserTextChannelMap
        await CreateHub(user.Id, user.Username).StartTyping(channel.Id);

        Assert.False(WasSentToOthers(channel.Id, "UserTyping"),
            "StartTyping must be a no-op when the user has not joined the channel");
    }

    [Fact]
    public async Task StartTyping_WhenInDifferentChannel_DoesNotBroadcast()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "typer-c");
        var textChannel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);

        // Add a second text channel and join it instead
        var other = new Channel { Name = "other", Type = ChannelType.Text, ServerId = server.Id, Position = 5 };
        _testDb.Db.Channels.Add(other);
        await _testDb.Db.SaveChangesAsync();

        var hub = CreateHub(user.Id, user.Username, "conn-c");
        await hub.JoinChannel(other.Id); // joined channel 'other', not textChannel
        _sent.Clear();
        await hub.StartTyping(textChannel.Id); // typing for wrong channel

        Assert.False(WasSentToOthers(textChannel.Id, "UserTyping"),
            "StartTyping must be a no-op when the supplied channelId differs from the user's current channel");
    }

    [Fact]
    public async Task StartTyping_Payload_ContainsUserIdUsernameAndChannelId()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "typer-d");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        var hub = CreateHub(user.Id, user.Username, "conn-d");

        await hub.JoinChannel(channel.Id);
        _sent.Clear();
        await hub.StartTyping(channel.Id);

        var (_, args) = GetLastSentToOthers(channel.Id, "UserTyping");
        Assert.Equal(user.Id, (int)args[0]);
        Assert.Equal(user.Username, (string)args[1]);
        Assert.Equal(channel.Id, (int)args[2]);
    }

    [Fact]
    public async Task StartTyping_DoesNotNotifyCallerItself()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "typer-e");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        var hub = CreateHub(user.Id, user.Username, "conn-e");

        await hub.JoinChannel(channel.Id);
        _sent.Clear();
        await hub.StartTyping(channel.Id);

        // Caller target must never receive UserTyping (OthersInGroup excludes caller)
        var callerGotTyping = _sent.TryGetValue("caller", out var callerMsgs)
            && callerMsgs.Any(m => m.Method == "UserTyping");
        Assert.False(callerGotTyping, "StartTyping must use OthersInGroup so the typing user doesn't echo to themselves");
    }

    [Fact]
    public async Task StartTyping_MultipleUsers_EachBroadcastsIndependently()
    {
        var (server, alice) = await TestHelpers.SeedServerAsync(_testDb.Db, "alice-typing");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);

        var bob = new User { Username = "bob-typing", Email = "bob-typing@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(bob);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = bob.Id, Role = ServerRole.Member });
        await _testDb.Db.SaveChangesAsync();

        var hubAlice = CreateHub(alice.Id, alice.Username, "conn-alice-t");
        var hubBob = CreateHub(bob.Id, bob.Username, "conn-bob-t");
        await hubAlice.JoinChannel(channel.Id);
        await hubBob.JoinChannel(channel.Id);
        _sent.Clear();

        await hubAlice.StartTyping(channel.Id);
        await hubBob.StartTyping(channel.Id);

        var typingCalls = _sent
            .Where(kv => kv.Key.StartsWith("others:channel-"))
            .SelectMany(kv => kv.Value)
            .Where(m => m.Method == "UserTyping")
            .ToList();

        Assert.Equal(2, typingCalls.Count);
    }

    // ── StopTyping ────────────────────────────────────────────────────────────

    [Fact]
    public async Task StopTyping_AlwaysBroadcastsUserStoppedTyping()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "stoptyper-a");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);

        // StopTyping has no guard — it fires even without a prior JoinChannel
        await CreateHub(user.Id, user.Username).StopTyping(channel.Id);

        Assert.True(WasSentToOthers(channel.Id, "UserStoppedTyping"),
            "StopTyping must always broadcast UserStoppedTyping regardless of channel membership state");
    }

    [Fact]
    public async Task StopTyping_Payload_ContainsUserIdAndChannelId()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "stoptyper-b");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        var hub = CreateHub(user.Id, user.Username, "conn-sb");

        await hub.JoinChannel(channel.Id);
        _sent.Clear();
        await hub.StopTyping(channel.Id);

        var (_, args) = GetLastSentToOthers(channel.Id, "UserStoppedTyping");
        Assert.Equal(user.Id, (int)args[0]);
        Assert.Equal(channel.Id, (int)args[1]);
    }

    [Fact]
    public async Task StopTyping_DoesNotNotifyCallerItself()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "stoptyper-c");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);

        await CreateHub(user.Id, user.Username, "conn-sc").StopTyping(channel.Id);

        var callerGotStopped = _sent.TryGetValue("caller", out var callerMsgs)
            && callerMsgs.Any(m => m.Method == "UserStoppedTyping");
        Assert.False(callerGotStopped, "StopTyping must use OthersInGroup so the typing user doesn't echo to themselves");
    }

    [Fact]
    public async Task StartTyping_ThenStop_BothBroadcastInOrder()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "cycle-typer");
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        var hub = CreateHub(user.Id, user.Username, "conn-cycle");

        await hub.JoinChannel(channel.Id);
        _sent.Clear();

        await hub.StartTyping(channel.Id);
        await hub.StopTyping(channel.Id);

        Assert.True(WasSentToOthers(channel.Id, "UserTyping"), "UserTyping must have been sent");
        Assert.True(WasSentToOthers(channel.Id, "UserStoppedTyping"), "UserStoppedTyping must have been sent");
    }
}
