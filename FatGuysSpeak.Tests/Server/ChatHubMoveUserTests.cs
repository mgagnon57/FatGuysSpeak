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

    [Fact]
    public async Task AdminMovesMemberInVoice_SendsForceMoveToTarget_AndWritesAudit()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_testDb.Db, "mv-admin");
        var voiceA = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var voiceB = await AddVoiceChannelAsync(server.Id, "Voice B");
        var target = await AddMemberAsync(server.Id, "mv-target", ServerRole.Member);

        await CreateHub(target.Id, target.Username, "conn-target").JoinVoiceChannel(voiceA.Id);
        _sent.Clear();
        await CreateHub(owner.Id, owner.Username, "conn-admin").MoveUserToVoiceChannel(target.Id, voiceB.Id);

        Assert.True(WasSentTo($"user:{target.Id}", "ForceMoveToVoice"));
        var (_, args) = LastSent($"user:{target.Id}", "ForceMoveToVoice");
        Assert.Equal(voiceB.Id, (int)args[0]);
        Assert.Equal(owner.Username, (string)args[1]);
        Assert.True(await _testDb.Db.AuditLogs.AnyAsync(a => a.Action == "VoiceMoved" && a.TargetId == target.Id && a.ActorId == owner.Id));
    }

    [Fact]
    public async Task ModeratorMovesMember_IsAllowed()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_testDb.Db, "mv-mod");
        var voiceA = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var voiceB = await AddVoiceChannelAsync(server.Id, "Voice B");
        var mod = await AddMemberAsync(server.Id, "mv-themod", ServerRole.Moderator);
        var target = await AddMemberAsync(server.Id, "mv-tgt2", ServerRole.Member);

        await CreateHub(target.Id, target.Username, "conn-t2").JoinVoiceChannel(voiceA.Id);
        _sent.Clear();
        await CreateHub(mod.Id, mod.Username, "conn-mod").MoveUserToVoiceChannel(target.Id, voiceB.Id);

        Assert.True(WasSentTo($"user:{target.Id}", "ForceMoveToVoice"));
    }

    [Fact]
    public async Task MemberCaller_CannotMove_NoSend_NoAudit()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_testDb.Db, "mv-member");
        var voiceA = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var voiceB = await AddVoiceChannelAsync(server.Id, "Voice B");
        var caller = await AddMemberAsync(server.Id, "mv-lowcaller", ServerRole.Member);
        var target = await AddMemberAsync(server.Id, "mv-tgt3", ServerRole.Member);

        await CreateHub(target.Id, target.Username, "conn-t3").JoinVoiceChannel(voiceA.Id);
        _sent.Clear();
        await CreateHub(caller.Id, caller.Username, "conn-low").MoveUserToVoiceChannel(target.Id, voiceB.Id);

        Assert.False(WasSentTo($"user:{target.Id}", "ForceMoveToVoice"));
        Assert.False(await _testDb.Db.AuditLogs.AnyAsync(a => a.Action == "VoiceMoved"));
    }

    [Fact]
    public async Task TargetNotInVoice_NoSend()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_testDb.Db, "mv-novoice");
        var voiceB = await AddVoiceChannelAsync(server.Id, "Voice B");
        var target = await AddMemberAsync(server.Id, "mv-idle", ServerRole.Member);

        await CreateHub(owner.Id, owner.Username, "conn-a").MoveUserToVoiceChannel(target.Id, voiceB.Id);

        Assert.False(WasSentTo($"user:{target.Id}", "ForceMoveToVoice"));
    }

    [Fact]
    public async Task TargetAlreadyInDestination_NoSend()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_testDb.Db, "mv-same");
        var voiceA = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Voice);
        var target = await AddMemberAsync(server.Id, "mv-tgt4", ServerRole.Member);

        await CreateHub(target.Id, target.Username, "conn-t4").JoinVoiceChannel(voiceA.Id);
        _sent.Clear();
        await CreateHub(owner.Id, owner.Username, "conn-a").MoveUserToVoiceChannel(target.Id, voiceA.Id);

        Assert.False(WasSentTo($"user:{target.Id}", "ForceMoveToVoice"));
    }
}
