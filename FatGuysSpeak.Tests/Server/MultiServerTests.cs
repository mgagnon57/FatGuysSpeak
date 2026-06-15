using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace FatGuysSpeak.Tests.Server;

public class MultiServerTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly List<(string Target, string Method, object[] Args)> _hubCalls = [];
    private readonly IHubContext<FatGuysSpeak.Server.Hubs.ChatHub> _hub;

    public MultiServerTests()
    {
        _testDb = new TestDb();

        string lastTarget = "";
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
             .Callback<string, object[], CancellationToken>((m, a, _) => _hubCalls.Add((lastTarget, m, a)))
             .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>()))
               .Callback<string>(g => lastTarget = g)
               .Returns(proxy.Object);
        clients.Setup(c => c.User(It.IsAny<string>()))
               .Callback<string>(u => lastTarget = $"user-{u}")
               .Returns(proxy.Object);
        var hub = new Mock<IHubContext<FatGuysSpeak.Server.Hubs.ChatHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        _hub = hub.Object;
    }

    public void Dispose() => _testDb.Dispose();

    private ServersController MakeController(int userId, string username)
    {
        var ctrl = new ServersController(_testDb.Db, _hub);
        TestHelpers.SetUser(ctrl, userId, username);
        return ctrl;
    }

    // ── Multi-server membership ───────────────────────────────────────────────

    [Fact]
    public async Task GetMyServers_UserInMultipleServers_ReturnsAll()
    {
        var (_, owner) = await TestHelpers.SeedServerAsync(_testDb.Db, "multi-owner");
        var server2 = new GuildServer { Name = "Second Server", OwnerId = owner.Id };
        _testDb.Db.Servers.Add(server2);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = server2.Id, UserId = owner.Id, Role = ServerRole.Admin });
        await _testDb.Db.SaveChangesAsync();

        var result = await MakeController(owner.Id, owner.Username).GetMyServers();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.Name == "FatGuysSpeak");
        Assert.Contains(result, s => s.Name == "Second Server");
    }

    [Fact]
    public async Task CreateServer_Creator_IsAdmin()
    {
        var (_, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "create-admin-check");

        var result = await MakeController(user.Id, user.Username)
            .CreateServer(new CreateServerRequest("Brand New Guild", null));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ServerDto>(ok.Value);
        Assert.Equal(ServerRole.Admin, dto.MyRole);
    }

    [Fact]
    public async Task CreateServer_AppearsInGetMyServers()
    {
        var (_, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "create-appears-owner");

        await MakeController(user.Id, user.Username)
            .CreateServer(new CreateServerRequest("Brand New Guild", null));

        var servers = await MakeController(user.Id, user.Username).GetMyServers();
        Assert.Equal(2, servers.Count);
        Assert.Contains(servers, s => s.Name == "Brand New Guild");
    }

    // ── Leave server ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LeaveServer_Member_RemovesMembership()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_testDb.Db, "leave-owner");
        var member = new User { Username = "leave-member", Email = "lm@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(member);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = member.Id });
        await _testDb.Db.SaveChangesAsync();

        var result = await MakeController(member.Id, "leave-member").LeaveServer(server.Id);

        Assert.IsType<OkResult>(result);
        Assert.False(_testDb.Db.ServerMembers.Any(sm => sm.ServerId == server.Id && sm.UserId == member.Id));
    }

    [Fact]
    public async Task LeaveServer_NonMember_ReturnsNotFound()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_testDb.Db, "leave-nf-owner");
        var outsider = new User { Username = "outsider-lv", Email = "olv@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(outsider);
        await _testDb.Db.SaveChangesAsync();

        var result = await MakeController(outsider.Id, "outsider-lv").LeaveServer(server.Id);

        Assert.IsType<NotFoundResult>(result);
    }

    // ── Kick member ──────────────────────────────────────────────────────────

    [Fact]
    public async Task KickMember_AsAdmin_RemovesMembership()
    {
        var (server, admin) = await TestHelpers.SeedServerAsync(_testDb.Db, "kick-admin");
        var victim = new User { Username = "kick-victim", Email = "kv@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(victim);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = victim.Id });
        await _testDb.Db.SaveChangesAsync();

        var result = await MakeController(admin.Id, admin.Username).KickMember(server.Id, victim.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(_testDb.Db.ServerMembers.Any(sm => sm.ServerId == server.Id && sm.UserId == victim.Id));
    }

    [Fact]
    public async Task KickMember_AsAdmin_SendsKickedFromServerToTarget()
    {
        var (server, admin) = await TestHelpers.SeedServerAsync(_testDb.Db, "kick-hub-admin");
        var victim = new User { Username = "kick-hub-victim", Email = "khv@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(victim);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = victim.Id });
        await _testDb.Db.SaveChangesAsync();

        _hubCalls.Clear();
        await MakeController(admin.Id, admin.Username).KickMember(server.Id, victim.Id);

        Assert.Contains(_hubCalls, c =>
            c.Method == "KickedFromServer"
            && c.Target == $"user-{victim.Id}"
            && (int)c.Args[0] == server.Id);
    }

    [Fact]
    public async Task KickMember_AsAdmin_BroadcastsUserDisconnectedToGroup()
    {
        var (server, admin) = await TestHelpers.SeedServerAsync(_testDb.Db, "kick-dc-admin");
        var victim = new User { Username = "kick-dc-victim", Email = "kdv@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(victim);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = victim.Id });
        await _testDb.Db.SaveChangesAsync();

        _hubCalls.Clear();
        await MakeController(admin.Id, admin.Username).KickMember(server.Id, victim.Id);

        Assert.Contains(_hubCalls, c =>
            c.Method == "UserDisconnected"
            && c.Target == $"server-{server.Id}");
    }

    [Fact]
    public async Task KickMember_NonAdmin_ReturnsForbid()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_testDb.Db, "kick-forbid-owner");
        var regular = new User { Username = "kick-regular", Email = "kr@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(regular);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = regular.Id });
        await _testDb.Db.SaveChangesAsync();

        var victim = new User { Username = "kick-forbid-victim", Email = "kfv@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(victim);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = victim.Id });
        await _testDb.Db.SaveChangesAsync();

        var result = await MakeController(regular.Id, "kick-regular").KickMember(server.Id, victim.Id);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task KickMember_Owner_ReturnsBadRequest()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_testDb.Db, "kick-owner-check");

        var result = await MakeController(owner.Id, owner.Username).KickMember(server.Id, owner.Id);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
