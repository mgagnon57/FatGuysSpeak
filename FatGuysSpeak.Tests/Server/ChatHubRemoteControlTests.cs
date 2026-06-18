using System.Reflection;
using System.Security.Claims;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace FatGuysSpeak.Tests.Server;

[Collection("HubTests")]
public class ChatHubRemoteControlTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly Mock<IHubCallerClients> _mockClients;
    private readonly Mock<IGroupManager> _mockGroups;
    private readonly Dictionary<string, List<(string Method, object[] Args)>> _sent = new();

    public ChatHubRemoteControlTests()
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
        p.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((m, a, _) => Track(target, m, a)).Returns(Task.CompletedTask);
        return p.Object;
    }
    private ISingleClientProxy Single(string target)
    {
        var p = new Mock<ISingleClientProxy>();
        p.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((m, a, _) => Track(target, m, a)).Returns(Task.CompletedTask);
        return p.Object;
    }
    private void Track(string t, string m, object[] a) { if (!_sent.TryGetValue(t, out var l)) _sent[t] = l = []; l.Add((m, a)); }
    private bool Sent(string target, string method) => _sent.TryGetValue(target, out var l) && l.Any(x => x.Method == method);

    private ChatHub Hub(int userId, string username, string conn = "conn")
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Name, username) };
        var ctx = new Mock<HubCallerContext>();
        ctx.Setup(c => c.ConnectionId).Returns(conn);
        ctx.Setup(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
        return new ChatHub(_testDb.Db, new FatGuysSpeak.Server.Services.OnlineTimeTracker())
        { Context = ctx.Object, Clients = _mockClients.Object, Groups = _mockGroups.Object };
    }

    private static void ClearHubStaticState()
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        foreach (var name in new[] { "ActiveStreamers", "RemoteControlSessions", "OnlineUsers", "VoiceChannelMap", "UserTextChannelMap", "ChannelOccupants" })
            (typeof(ChatHub).GetField(name, flags)?.GetValue(null) as System.Collections.IDictionary)?.Clear();
    }

    private async Task<(GuildServer server, User streamer, User viewer)> SeedStreamAsync(string prefix)
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
        await Hub(streamer.Id, streamer.Username, "conn-s").StartStream(channel.Id);
        return (server, streamer, viewer);
    }

    [Fact]
    public async Task GrantControl_OpensSession_NotifiesBothParties()
    {
        var (_, streamer, viewer) = await SeedStreamAsync("grant");
        _sent.Clear();
        await Hub(streamer.Id, streamer.Username, "conn-s").GrantControl(viewer.Id);
        Assert.True(Sent($"user:{viewer.Id}", "ControlGranted"));
        Assert.True(Sent($"user:{streamer.Id}", "ControlActive"));
    }

    [Fact]
    public async Task SendRemoteInput_FromActiveController_RelaysToStreamerOnly()
    {
        var (_, streamer, viewer) = await SeedStreamAsync("relay");
        await Hub(streamer.Id, streamer.Username, "conn-s").GrantControl(viewer.Id);
        _sent.Clear();
        await Hub(viewer.Id, viewer.Username, "conn-v").SendRemoteInput(new RemoteInputDto(RemoteInputKind.Move, 0.5, 0.5));
        Assert.True(Sent($"user:{streamer.Id}", "ReceiveRemoteInput"));
    }

    [Fact]
    public async Task SendRemoteInput_FromNonController_IsDropped()
    {
        var (_, streamer, viewer) = await SeedStreamAsync("drop");
        _sent.Clear();
        await Hub(viewer.Id, viewer.Username, "conn-v").SendRemoteInput(new RemoteInputDto(RemoteInputKind.Move, 0.5, 0.5));
        Assert.False(_sent.Any(kv => kv.Value.Any(m => m.Method == "ReceiveRemoteInput")));
    }

    [Fact]
    public async Task GrantControl_WhenAlreadyControlled_ReturnsBusy_AndKeepsController()
    {
        var (_, streamer, viewer) = await SeedStreamAsync("busy");
        var viewer2 = new User { Username = "busy-v2", Email = "busy-v2@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(viewer2);
        await _testDb.Db.SaveChangesAsync();
        await Hub(streamer.Id, streamer.Username, "conn-s").GrantControl(viewer.Id);
        _sent.Clear();
        await Hub(streamer.Id, streamer.Username, "conn-s").GrantControl(viewer2.Id);
        Assert.True(Sent("caller", "ControlBusy"));
        Assert.False(Sent($"user:{viewer2.Id}", "ControlGranted"));
    }

    [Fact]
    public async Task OfferThenAccept_OpensSession()
    {
        var (_, streamer, viewer) = await SeedStreamAsync("offer");
        await Hub(streamer.Id, streamer.Username, "conn-s").OfferControl(viewer.Id);
        _sent.Clear();
        await Hub(viewer.Id, viewer.Username, "conn-v").AcceptControl(streamer.Id);
        Assert.True(Sent($"user:{viewer.Id}", "ControlGranted"));
        Assert.True(Sent($"user:{streamer.Id}", "ControlActive"));
    }

    [Fact]
    public async Task StopControl_EndsSession_NotifiesController()
    {
        var (_, streamer, viewer) = await SeedStreamAsync("stop");
        await Hub(streamer.Id, streamer.Username, "conn-s").GrantControl(viewer.Id);
        _sent.Clear();
        await Hub(streamer.Id, streamer.Username, "conn-s").StopControl();
        Assert.True(Sent($"user:{viewer.Id}", "ControlEnded"));
        _sent.Clear();
        await Hub(viewer.Id, viewer.Username, "conn-v").SendRemoteInput(new RemoteInputDto(RemoteInputKind.Move));
        Assert.False(_sent.Any(kv => kv.Value.Any(m => m.Method == "ReceiveRemoteInput")));
    }

    [Fact]
    public async Task ReleaseControl_ByController_EndsSession()
    {
        var (_, streamer, viewer) = await SeedStreamAsync("release");
        await Hub(streamer.Id, streamer.Username, "conn-s").GrantControl(viewer.Id);
        _sent.Clear();
        await Hub(viewer.Id, viewer.Username, "conn-v").ReleaseControl();
        Assert.True(Sent($"user:{streamer.Id}", "ControlEnded"));
    }
}
