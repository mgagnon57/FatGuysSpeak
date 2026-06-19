using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Tests.Server;

// Tests for Phase 4: channel permission enforcement
public class ChannelPermissionsTests : IDisposable
{
    private readonly TestDb _db;
    private readonly ChannelPermissionsController _ctrl;
    private GuildServer _server = null!;
    private User _owner = null!;
    private User _member = null!;
    private Channel _channel = null!;

    public ChannelPermissionsTests()
    {
        _db = new TestDb();
        _ctrl = new ChannelPermissionsController(_db.Db);
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedAsync()
    {
        (_server, _owner) = await TestHelpers.SeedServerAsync(_db.Db, "owner");
        _channel = _db.Db.Channels.First(c => c.ServerId == _server.Id && c.Type == ChannelType.Text);

        _member = new User { Username = "member", Email = "member@test.com", PasswordHash = "*" };
        _db.Db.Users.Add(_member);
        await _db.Db.SaveChangesAsync();
        _db.Db.ServerMembers.Add(new ServerMember { ServerId = _server.Id, UserId = _member.Id, Role = ServerRole.Member });
        await _db.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetPermissions_NonMember_ReturnsForbid()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, 9999, "stranger");

        var result = await _ctrl.GetPermissions(_server.Id, _channel.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetPermissions_DefaultsToMember()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        var result = await _ctrl.GetPermissions(_server.Id, _channel.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ChannelPermissionDto>(ok.Value);
        Assert.Equal(ServerRole.Member, dto.MinRoleToRead);
        Assert.Equal(ServerRole.Member, dto.MinRoleToWrite);
    }

    [Fact]
    public async Task SetPermissions_Admin_CanSetAdminWrite()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _owner.Id, _owner.Username);

        var result = await _ctrl.SetPermissions(_server.Id, _channel.Id,
            new SetChannelPermissionRequest(ServerRole.Member, ServerRole.Admin));

        Assert.IsType<OkObjectResult>(result.Result);
        var perm = _db.Db.ChannelPermissions.Find(_channel.Id);
        Assert.NotNull(perm);
        Assert.Equal(ServerRole.Admin, perm.MinRoleToWrite);
    }

    [Fact]
    public async Task SetPermissions_Member_ReturnsForbid()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        var result = await _ctrl.SetPermissions(_server.Id, _channel.Id,
            new SetChannelPermissionRequest(ServerRole.Admin, ServerRole.Admin));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task SetPermissions_UpdatesExistingRow()
    {
        await SeedAsync();
        _db.Db.ChannelPermissions.Add(new ChannelPermission
        {
            ChannelId = _channel.Id,
            MinRoleToRead = ServerRole.Member,
            MinRoleToWrite = ServerRole.Member
        });
        await _db.Db.SaveChangesAsync();

        TestHelpers.SetUser(_ctrl, _owner.Id, _owner.Username);
        await _ctrl.SetPermissions(_server.Id, _channel.Id,
            new SetChannelPermissionRequest(ServerRole.Admin, ServerRole.Admin));

        var perm = _db.Db.ChannelPermissions.Find(_channel.Id);
        Assert.Equal(ServerRole.Admin, perm!.MinRoleToRead);
        Assert.Equal(ServerRole.Admin, perm.MinRoleToWrite);
    }

    [Fact]
    public async Task SetPermissions_WrongServer_ReturnsNotFound()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _owner.Id, _owner.Username);

        var result = await _ctrl.SetPermissions(_server.Id, 9999,
            new SetChannelPermissionRequest(ServerRole.Member, ServerRole.Member));

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task SetPermissions_WritesAuditLog()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _owner.Id, _owner.Username);

        await _ctrl.SetPermissions(_server.Id, _channel.Id,
            new SetChannelPermissionRequest(ServerRole.Admin, ServerRole.Admin));

        var log = _db.Db.AuditLogs.FirstOrDefault(a => a.Action == "ChannelPermissionsChanged");
        Assert.NotNull(log);
        Assert.Equal(_channel.Id, log.TargetId);
        Assert.Contains("Admin", log.Detail);
    }

    [Fact]
    public async Task GetPermissions_ReflectsSetPermissions()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _owner.Id, _owner.Username);

        await _ctrl.SetPermissions(_server.Id, _channel.Id,
            new SetChannelPermissionRequest(ServerRole.Admin, ServerRole.Admin));

        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);
        var result = await _ctrl.GetPermissions(_server.Id, _channel.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ChannelPermissionDto>(ok.Value);
        Assert.Equal(ServerRole.Admin, dto.MinRoleToRead);
        Assert.Equal(ServerRole.Admin, dto.MinRoleToWrite);
    }
}
