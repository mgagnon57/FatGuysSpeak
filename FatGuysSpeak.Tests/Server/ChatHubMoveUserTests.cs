using System.Reflection;
using System.Security.Claims;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace FatGuysSpeak.Tests.Server;

[Collection("HubTests")]
public class ChatHubMoveUserTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly Mock<IHubCallerClients> _mockClients;
    private readonly Mock<IGroupManager> _mockGroups;
    private readonly Dictionary<string, List<(string Method, object[] Args)>> _sent = new();

    public ChatHubMoveUserTests()
    {
        _testDb = new TestDb();
        _mockClients = new Mock<IHubCallerClients>();
        _mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns<string>(g => Proxy($"group:{g}"));
        _mockClients.Setup(c => c.OthersInGroup(It.IsAny<string>())).Returns<string>(g => Proxy($"others:{g}"));
        _mockClients.Setup(c => c.Caller).Returns(Single("caller"));
        _mockClients.Setup(c => c.User(It.IsAny<string>())).Returns<string>(id => Single($"user:{id}"));

        _mockGroups = new Mock<IGroupManager>();
        _mockGroups.Setup(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockGroups.Setup(g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    public void Dispose() { ClearHubStaticState(); _testDb.Dispose(); }

    private IClientProxy Proxy(string t)
    {
        var p = new Mock<IClientProxy>();
        p.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((m, a, _) => Track(t, m, a)).Returns(Task.CompletedTask);
        return p.Object;
    }
    private ISingleClientProxy Single(string t)
    {
        var p = new Mock<ISingleClientProxy>();
        p.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((m, a, _) => Track(t, m, a)).Returns(Task.CompletedTask);
        return p.Object;
    }
    private void Track(string t, string m, object[] a) { if (!_sent.TryGetValue(t, out var l)) _sent[t] = l = []; l.Add((m, a)); }
    private bool WasSentTo(string t, string m) => _sent.TryGetValue(t, out var l) && l.Any(x => x.Method == m);
    private (string Method, object[] Args) LastSent(string t, string m) => _sent[t].Last(x => x.Method == m);

    private ChatHub CreateHub(int userId, string username, string conn)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Name, username) };
        var ctx = new Mock<HubCallerContext>();
        ctx.Setup(c => c.ConnectionId).Returns(conn);
        ctx.Setup(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
        return new ChatHub(_testDb.Db, new FatGuysSpeak.Server.Services.OnlineTimeTracker())
        { Context = ctx.Object, Clients = _mockClients.Object, Groups = _mockGroups.Object };
    }

    private async Task<User> AddMemberAsync(int serverId, string name, ServerRole role)
    {
        var u = new User { Username = name, Email = $"{name}@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(u);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = serverId, UserId = u.Id, Role = role });
        await _testDb.Db.SaveChangesAsync();
        return u;
    }
    private async Task<Channel> AddVoiceChannelAsync(int serverId, string name)
    {
        var ch = new Channel { Name = name, Type = ChannelType.Voice, ServerId = serverId, Position = 9 };
        _testDb.Db.Channels.Add(ch);
        await _testDb.Db.SaveChangesAsync();
        return ch;
    }

    private static void ClearHubStaticState()
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        foreach (var name in new[] { "ActiveStreamers", "VoiceChannelMap", "UserTextChannelMap", "ChannelOccupants", "OnlineUsers", "ActiveCameras" })
            (typeof(ChatHub).GetField(name, flags)?.GetValue(null) as System.Collections.IDictionary)?.Clear();
    }

    // The move tells the TARGET's client to switch to the channel via the existing ForceJoinChannel
    // relay (the same one Kick-Voice uses). The target does NOT need to be in voice.

    [Fact]
    public async Task AdminMovesMember_SendsForceJoinToTarget_AndWritesAudit()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_testDb.Db, "mv-admin");
        var dest = await AddVoiceChannelAsync(server.Id, "Dest");
        var target = await AddMemberAsync(server.Id, "mv-target", ServerRole.Member);

        await CreateHub(owner.Id, owner.Username, "conn-admin").MoveUserToChannel(target.Id, dest.Id);

        Assert.True(WasSentTo($"user:{target.Id}", "ForceJoinChannel"));
        var (_, args) = LastSent($"user:{target.Id}", "ForceJoinChannel");
        Assert.Equal(dest.Id, (int)args[0]);
        Assert.True(await _testDb.Db.AuditLogs.AnyAsync(a => a.Action == "UserMoved" && a.TargetId == target.Id && a.ActorId == owner.Id));
    }

    [Fact]
    public async Task ModeratorMovesMember_IsAllowed()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_testDb.Db, "mv-mod");
        var dest = await AddVoiceChannelAsync(server.Id, "Dest");
        var mod = await AddMemberAsync(server.Id, "mv-themod", ServerRole.Moderator);
        var target = await AddMemberAsync(server.Id, "mv-tgt2", ServerRole.Member);

        await CreateHub(mod.Id, mod.Username, "conn-mod").MoveUserToChannel(target.Id, dest.Id);

        Assert.True(WasSentTo($"user:{target.Id}", "ForceJoinChannel"));
    }

    [Fact]
    public async Task MemberCaller_CannotMove_NoSend_NoAudit()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_testDb.Db, "mv-member");
        var dest = await AddVoiceChannelAsync(server.Id, "Dest");
        var caller = await AddMemberAsync(server.Id, "mv-lowcaller", ServerRole.Member);
        var target = await AddMemberAsync(server.Id, "mv-tgt3", ServerRole.Member);

        await CreateHub(caller.Id, caller.Username, "conn-low").MoveUserToChannel(target.Id, dest.Id);

        Assert.False(WasSentTo($"user:{target.Id}", "ForceJoinChannel"));
        Assert.False(await _testDb.Db.AuditLogs.AnyAsync(a => a.Action == "UserMoved"));
    }

    [Fact]
    public async Task MovingSelf_IsNoOp()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_testDb.Db, "mv-self");
        var dest = await AddVoiceChannelAsync(server.Id, "Dest");

        await CreateHub(owner.Id, owner.Username, "conn-self").MoveUserToChannel(owner.Id, dest.Id);

        Assert.False(WasSentTo($"user:{owner.Id}", "ForceJoinChannel"));
    }

    [Fact]
    public async Task TargetNotAMemberOfServer_NoSend()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_testDb.Db, "mv-nonmember");
        var dest = await AddVoiceChannelAsync(server.Id, "Dest");
        // A user that exists but is NOT a member of this server.
        var stranger = new User { Username = "mv-stranger", Email = "mv-stranger@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(stranger);
        await _testDb.Db.SaveChangesAsync();

        await CreateHub(owner.Id, owner.Username, "conn-a").MoveUserToChannel(stranger.Id, dest.Id);

        Assert.False(WasSentTo($"user:{stranger.Id}", "ForceJoinChannel"));
    }
}
