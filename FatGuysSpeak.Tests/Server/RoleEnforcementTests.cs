using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace FatGuysSpeak.Tests.Server;

// Tests for Phase 1: role enforcement in ServersController and MessagesController
public class RoleEnforcementTests : IDisposable
{
    private readonly TestDb _db;
    private readonly ServersController _ctrl;
    private GuildServer _server = null!;
    private User _owner = null!;
    private User _member = null!;
    private User _moderator = null!;

    public RoleEnforcementTests()
    {
        _db = new TestDb();
        _ctrl = new ServersController(_db.Db, TestHelpers.MockHub(), TestHelpers.NullWebhooks());
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedAsync()
    {
        (_server, _owner) = await TestHelpers.SeedServerAsync(_db.Db, "owner");

        _member = new User { Username = "member", Email = "member@test.com", PasswordHash = "*" };
        _moderator = new User { Username = "mod", Email = "mod@test.com", PasswordHash = "*" };
        _db.Db.Users.AddRange(_member, _moderator);
        await _db.Db.SaveChangesAsync();

        _db.Db.ServerMembers.AddRange(
            new ServerMember { ServerId = _server.Id, UserId = _member.Id, Role = ServerRole.Member },
            new ServerMember { ServerId = _server.Id, UserId = _moderator.Id, Role = ServerRole.Moderator }
        );
        await _db.Db.SaveChangesAsync();
    }

    // ── CreateChannel ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateChannel_AsAdmin_Succeeds()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _owner.Id, _owner.Username);

        var result = await _ctrl.CreateChannel(_server.Id, new CreateChannelRequest("dev", ChannelType.Text));

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateChannel_AsMember_ReturnsForbid()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        var result = await _ctrl.CreateChannel(_server.Id, new CreateChannelRequest("dev", ChannelType.Text));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task CreateChannel_AsModerator_ReturnsForbid()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _moderator.Id, _moderator.Username);

        var result = await _ctrl.CreateChannel(_server.Id, new CreateChannelRequest("dev", ChannelType.Text));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task CreateChannel_AsAdmin_WritesAuditLog()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _owner.Id, _owner.Username);

        await _ctrl.CreateChannel(_server.Id, new CreateChannelRequest("audit-test", ChannelType.Text));

        var log = _db.Db.AuditLogs.FirstOrDefault(a => a.Action == "ChannelCreated");
        Assert.NotNull(log);
        Assert.Equal(_server.Id, log.ServerId);
        Assert.Equal(_owner.Id, log.ActorId);
        Assert.Contains("audit-test", log.Detail);
    }

    // ── DeleteChannel ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteChannel_AsAdmin_Succeeds()
    {
        await SeedAsync();
        var channel = _db.Db.Channels.First(c => c.ServerId == _server.Id && c.Type == ChannelType.Text);
        TestHelpers.SetUser(_ctrl, _owner.Id, _owner.Username);

        var result = await _ctrl.DeleteChannel(_server.Id, channel.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(_db.Db.Channels.Any(c => c.Id == channel.Id));
    }

    [Fact]
    public async Task DeleteChannel_AsMember_ReturnsForbid()
    {
        await SeedAsync();
        var channel = _db.Db.Channels.First(c => c.ServerId == _server.Id && c.Type == ChannelType.Text);
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        var result = await _ctrl.DeleteChannel(_server.Id, channel.Id);

        Assert.IsType<ForbidResult>(result);
    }

    // ── KickMember ────────────────────────────────────────────────────────────

    [Fact]
    public async Task KickMember_AsAdmin_RemovesMembership()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _owner.Id, _owner.Username);

        var result = await _ctrl.KickMember(_server.Id, _member.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(_db.Db.ServerMembers.Any(sm => sm.ServerId == _server.Id && sm.UserId == _member.Id));
    }

    [Fact]
    public async Task KickMember_AsMember_ReturnsForbid()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        var result = await _ctrl.KickMember(_server.Id, _moderator.Id);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task KickMember_CannotKickSelf_ReturnsBadRequest()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _owner.Id, _owner.Username);

        var result = await _ctrl.KickMember(_server.Id, _owner.Id);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── SetMemberRole ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SetMemberRole_AdminPromotesToModerator_Succeeds()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _owner.Id, _owner.Username);

        var result = await _ctrl.SetMemberRole(_server.Id, _member.Id, new SetRoleRequest(ServerRole.Moderator));

        Assert.IsType<NoContentResult>(result);
        var updated = _db.Db.ServerMembers.Find(_server.Id, _member.Id);
        Assert.Equal(ServerRole.Moderator, updated!.Role);
    }

    [Fact]
    public async Task SetMemberRole_AdminPromotesToAdmin_Succeeds_WhenIsOwner()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _owner.Id, _owner.Username);

        var result = await _ctrl.SetMemberRole(_server.Id, _member.Id, new SetRoleRequest(ServerRole.Admin));

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task SetMemberRole_MemberCannotChangeRoles_ReturnsForbid()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        var result = await _ctrl.SetMemberRole(_server.Id, _moderator.Id, new SetRoleRequest(ServerRole.Member));

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task SetMemberRole_CannotChangeSelfRole_ReturnsBadRequest()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _owner.Id, _owner.Username);

        var result = await _ctrl.SetMemberRole(_server.Id, _owner.Id, new SetRoleRequest(ServerRole.Member));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SetMemberRole_WritesAuditLog()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _owner.Id, _owner.Username);

        await _ctrl.SetMemberRole(_server.Id, _member.Id, new SetRoleRequest(ServerRole.Moderator));

        var log = _db.Db.AuditLogs.FirstOrDefault(a => a.Action == "RoleChanged");
        Assert.NotNull(log);
        Assert.Equal(_member.Id, log.TargetId);
        Assert.Contains("Moderator", log.Detail);
    }

    // ── GetMyServers returns MyRole ────────────────────────────────────────────

    [Fact]
    public async Task GetMyServers_IncludesMyRole()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        var servers = await _ctrl.GetMyServers();

        var srv = servers.FirstOrDefault(s => s.Id == _server.Id);
        Assert.NotNull(srv);
        Assert.Equal(ServerRole.Member, srv.MyRole);
    }

    [Fact]
    public async Task GetMyServers_OwnerGetsAdminRole()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _owner.Id, _owner.Username);

        var servers = await _ctrl.GetMyServers();

        var srv = servers.FirstOrDefault(s => s.Id == _server.Id);
        Assert.Equal(ServerRole.Admin, srv!.MyRole);
    }
}
