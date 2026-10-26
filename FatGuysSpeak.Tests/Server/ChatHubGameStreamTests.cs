using System.Reflection;
using System.Security.Claims;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace FatGuysSpeak.Tests.Server;

// Stream Game vs Share Screen on the hub: kind + game name reach viewers, and remote control is
// only possible on a Share Screen stream.
[Collection("HubTests")]
public class ChatHubGameStreamTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly Mock<IHubCallerClients> _mockClients;
    private readonly Mock<IGroupManager> _mockGroups;
    private readonly Dictionary<string, List<(string Method, object?[] Args)>> _sent = new();

    public ChatHubGameStreamTests()
    {
        _testDb = new TestDb();
        _mockClients = new Mock<IHubCallerClients>();
        _mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns<string>(g => Proxy($"group:{g}"));
        _mockClients.Setup(c => c.OthersInGroup(It.IsAny<string>())).Returns<string>(g => Proxy($"others:{g}"));
        _mockClients.Setup(c => c.Caller).Returns(Single("caller"));
        _mockClients.Setup(c => c.User(It.IsAny<string>())).Returns<string>(u => Single($"user:{u}"));
        _mockGroups = new Mock<IGroupManager>();
        _mockGroups.Setup(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockGroups.Setup(g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    public void Dispose() { ClearHubStaticState(); _testDb.Dispose(); }

    private IClientProxy Proxy(string target)
    {
        var p = new Mock<IClientProxy>();
        p.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((m, a, _) => Track(target, m, a)).Returns(Task.CompletedTask);
        return p.Object;
    }
    private ISingleClientProxy Single(string target)
    {
        var p = new Mock<ISingleClientProxy>();
        p.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((m, a, _) => Track(target, m, a)).Returns(Task.CompletedTask);
        return p.Object;
    }
    private void Track(string t, string m, object?[] a) { if (!_sent.TryGetValue(t, out var l)) _sent[t] = l = []; l.Add((m, a)); }
    private bool Sent(string target, string method) => _sent.TryGetValue(target, out var l) && l.Any(x => x.Method == method);
    private object?[] Last(string target, string method) => _sent[target].Last(x => x.Method == method).Args;

    private ChatHub Hub(int userId, string username, string conn)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Name, username) };
        var ctx = new Mock<HubCallerContext>();
        ctx.Setup(c => c.ConnectionId).Returns(conn);
        ctx.Setup(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
        return new ChatHub(_testDb.Db, new FatGuysSpeak.Server.Services.OnlineTimeTracker(), TestHelpers.NullBot(), TestHelpers.NullTts())
        { Context = ctx.Object, Clients = _mockClients.Object, Groups = _mockGroups.Object };
    }

    private static void ClearHubStaticState()
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        foreach (var name in new[] { "ActiveStreamers", "RemoteControlSessions", "OnlineUsers", "VoiceChannelMap", "UserTextChannelMap", "ChannelOccupants" })
            (typeof(ChatHub).GetField(name, flags)?.GetValue(null) as System.Collections.IDictionary)?.Clear();
    }

    private async Task<(GuildServer server, Channel channel, User streamer, User viewer)> SeedAsync(string prefix)
    {
        var (server, streamer) = await TestHelpers.SeedServerAsync(_testDb.Db, prefix);
        var viewer = new User { Username = prefix + "-viewer", Email = prefix + "-v@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(viewer);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = viewer.Id, Role = ServerRole.Member });
        await _testDb.Db.SaveChangesAsync();
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Name == "general");
        await Hub(viewer.Id, viewer.Username, "conn-v").OnConnectedAsync();
        await Hub(streamer.Id, streamer.Username, "conn-s").OnConnectedAsync();
        _sent.Clear();
        return (server, channel, streamer, viewer);
    }

    [Fact]
    public async Task StartGameStream_SendsStreamStartedAndDetailsWithGame()
    {
        var (server, channel, streamer, _) = await SeedAsync("gs-start");

        await Hub(streamer.Id, streamer.Username, "conn-s").StartGameStream(channel.Id, "Wardogs");

        Assert.True(Sent($"group:server-{server.Id}", "StreamStarted"));
        Assert.Equal(new object?[] { streamer.Id, channel.Id, (int)StreamKind.Game, "Wardogs" },
            Last($"group:server-{server.Id}", "StreamDetails"));
    }

    [Fact]
    public async Task StartStream_Legacy_IsScreenKind()
    {
        var (server, channel, streamer, _) = await SeedAsync("gs-legacy");

        await Hub(streamer.Id, streamer.Username, "conn-s").StartStream(channel.Id);

        Assert.Equal(new object?[] { streamer.Id, channel.Id, (int)StreamKind.Screen, null },
            Last($"group:server-{server.Id}", "StreamDetails"));
    }

    [Fact]
    public async Task StartGameStream_SanitizesGameName()
    {
        var (server, channel, streamer, _) = await SeedAsync("gs-sanitize");

        await Hub(streamer.Id, streamer.Username, "conn-s").StartGameStream(channel.Id, "x\u0007" + new string('y', 80));

        var game = (string)Last($"group:server-{server.Id}", "StreamDetails")[3]!;
        Assert.DoesNotContain(game, char.IsControl);
        Assert.True(game.Length <= 40);
    }

    [Fact]
    public async Task StartGameStream_NotificationNamesGameAndChannel()
    {
        var (_, channel, streamer, _) = await SeedAsync("gs-notif");

        await Hub(streamer.Id, streamer.Username, "conn-s").StartGameStream(channel.Id, "Wardogs");

        var text = (string)Last($"group:channel-{channel.Id}", "StreamNotification")[1]!;
        Assert.Equal($"🎮 {streamer.Username} started streaming Wardogs in #general", text);
    }

    [Fact]
    public async Task StartStream_NotificationSaysSharingScreen()
    {
        var (_, channel, streamer, _) = await SeedAsync("gs-notif-screen");

        await Hub(streamer.Id, streamer.Username, "conn-s").StartStream(channel.Id);

        var text = (string)Last($"group:channel-{channel.Id}", "StreamNotification")[1]!;
        Assert.Equal($"🖥 {streamer.Username} started sharing their screen in #general", text);
    }

    [Fact]
    public async Task StopStream_OnGameStream_NamesGame()
    {
        var (_, channel, streamer, _) = await SeedAsync("gs-stop");
        var hub = Hub(streamer.Id, streamer.Username, "conn-s");
        await hub.StartGameStream(channel.Id, "Wardogs");

        await hub.StopStream();

        var text = (string)Last($"group:channel-{channel.Id}", "StreamNotification")[1]!;
        Assert.Equal($"⏹ {streamer.Username} stopped streaming Wardogs in #general", text);
    }

    [Fact]
    public async Task JoinChannel_ReplaysStreamDetailsToCaller()
    {
        var (_, channel, streamer, viewer) = await SeedAsync("gs-replay");
        await Hub(streamer.Id, streamer.Username, "conn-s").StartGameStream(channel.Id, "Wardogs");
        _sent.Clear();

        await Hub(viewer.Id, viewer.Username, "conn-v").JoinChannel(channel.Id);

        Assert.Equal(new object?[] { streamer.Id, channel.Id, (int)StreamKind.Game, "Wardogs" }, Last("caller", "StreamDetails"));
    }

    [Fact]
    public async Task UpdateStreamGame_UpdatesDetailsAndNotifies()
    {
        var (server, channel, streamer, _) = await SeedAsync("gs-update");
        var hub = Hub(streamer.Id, streamer.Username, "conn-s");
        await hub.StartGameStream(channel.Id, "Wardogs");
        _sent.Clear();

        await hub.UpdateStreamGame("Fortnite");

        Assert.Equal(new object?[] { streamer.Id, channel.Id, (int)StreamKind.Game, "Fortnite" },
            Last($"group:server-{server.Id}", "StreamDetails"));
        Assert.Equal($"🎮 {streamer.Username} switched to Fortnite",
            (string)Last($"group:channel-{channel.Id}", "StreamNotification")[1]!);
    }

    [Fact]
    public async Task UpdateStreamGame_WhenNotStreaming_DoesNothing()
    {
        var (_, _, streamer, _) = await SeedAsync("gs-update-none");

        await Hub(streamer.Id, streamer.Username, "conn-s").UpdateStreamGame("Fortnite");

        Assert.Empty(_sent);
    }

    [Fact]
    public async Task RequestControl_OnGameStream_IsIgnored()
    {
        var (_, channel, streamer, viewer) = await SeedAsync("gs-req");
        await Hub(streamer.Id, streamer.Username, "conn-s").StartGameStream(channel.Id, "Wardogs");
        _sent.Clear();

        await Hub(viewer.Id, viewer.Username, "conn-v").RequestControl(streamer.Id);

        Assert.False(Sent($"user:{streamer.Id}", "ControlRequested"));
    }

    [Fact]
    public async Task GrantControl_OnGameStream_DoesNotOpenSession()
    {
        var (_, channel, streamer, viewer) = await SeedAsync("gs-grant");
        var hub = Hub(streamer.Id, streamer.Username, "conn-s");
        await hub.StartGameStream(channel.Id, "Wardogs");
        _sent.Clear();

        await hub.GrantControl(viewer.Id);
        await Hub(viewer.Id, viewer.Username, "conn-v").AcceptControl(streamer.Id);

        Assert.False(Sent($"user:{viewer.Id}", "ControlGranted"));
    }

    [Fact]
    public async Task GrantControl_OnScreenStream_StillWorks()
    {
        var (_, channel, streamer, viewer) = await SeedAsync("gs-grant-screen");
        var hub = Hub(streamer.Id, streamer.Username, "conn-s");
        await hub.StartStream(channel.Id);
        _sent.Clear();

        await hub.GrantControl(viewer.Id);

        Assert.True(Sent($"user:{viewer.Id}", "ControlGranted"));
    }
}
