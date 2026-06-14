using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Tests.Server;

public class InviteLinkTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly ServersController _controller;

    public InviteLinkTests()
    {
        _testDb = new TestDb();
        _controller = new ServersController(_testDb.Db, TestHelpers.MockHub());
    }

    public void Dispose() => _testDb.Dispose();

    private async Task<(GuildServer server, User admin, User member)> SeedAsync()
    {
        var (server, admin) = await TestHelpers.SeedServerAsync(_testDb.Db, "invite-admin");

        var member = new User { Username = "invite-member", Email = "inv-mem@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(member);
        await _testDb.Db.SaveChangesAsync();

        return (server, admin, member);
    }

    // ── GetInvite ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetInvite_Admin_ReturnsCode()
    {
        var (server, admin, _) = await SeedAsync();
        TestHelpers.SetUser(_controller, admin.Id, admin.Username);

        var result = await _controller.GetInvite(server.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ServerInviteDto>(ok.Value);
        Assert.NotNull(dto.Code);
        Assert.True(dto.Code.Length >= 8);
        Assert.Equal(server.Id, dto.ServerId);
        Assert.Equal(server.Name, dto.ServerName);
    }

    [Fact]
    public async Task GetInvite_CalledTwice_ReturnsSameCode()
    {
        var (server, admin, _) = await SeedAsync();
        TestHelpers.SetUser(_controller, admin.Id, admin.Username);

        var r1 = (await _controller.GetInvite(server.Id)).Result as OkObjectResult;
        var r2 = (await _controller.GetInvite(server.Id)).Result as OkObjectResult;

        var code1 = ((ServerInviteDto)r1!.Value!).Code;
        var code2 = ((ServerInviteDto)r2!.Value!).Code;
        Assert.Equal(code1, code2);
    }

    [Fact]
    public async Task GetInvite_NonAdmin_ReturnsForbid()
    {
        var (server, _, member) = await SeedAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = member.Id, Role = ServerRole.Member });
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_controller, member.Id, member.Username);

        var result = await _controller.GetInvite(server.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetInvite_NonMember_ReturnsForbid()
    {
        var (server, _, member) = await SeedAsync();
        TestHelpers.SetUser(_controller, member.Id, member.Username);

        var result = await _controller.GetInvite(server.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // ── ResetInvite ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetInvite_Admin_ReturnsNewCode()
    {
        var (server, admin, _) = await SeedAsync();
        TestHelpers.SetUser(_controller, admin.Id, admin.Username);

        var first = ((OkObjectResult)(await _controller.GetInvite(server.Id)).Result!).Value as ServerInviteDto;
        var reset = ((OkObjectResult)(await _controller.ResetInvite(server.Id)).Result!).Value as ServerInviteDto;

        Assert.NotNull(reset);
        Assert.NotEqual(first!.Code, reset!.Code);
    }

    [Fact]
    public async Task ResetInvite_NonAdmin_ReturnsForbid()
    {
        var (server, _, member) = await SeedAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = member.Id, Role = ServerRole.Member });
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_controller, member.Id, member.Username);

        var result = await _controller.ResetInvite(server.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // ── PreviewInvite ─────────────────────────────────────────────────────────

    [Fact]
    public async Task PreviewInvite_ValidCode_ReturnsServerInfo()
    {
        var (server, admin, member) = await SeedAsync();
        TestHelpers.SetUser(_controller, admin.Id, admin.Username);
        var invite = ((OkObjectResult)(await _controller.GetInvite(server.Id)).Result!).Value as ServerInviteDto;

        TestHelpers.SetUser(_controller, member.Id, member.Username);
        var result = await _controller.PreviewInvite(invite!.Code);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ServerInviteDto>(ok.Value);
        Assert.Equal(server.Name, dto.ServerName);
        Assert.Equal(invite.Code, dto.Code);
    }

    [Fact]
    public async Task PreviewInvite_InvalidCode_ReturnsNotFound()
    {
        var (_, _, member) = await SeedAsync();
        TestHelpers.SetUser(_controller, member.Id, member.Username);

        var result = await _controller.PreviewInvite("doesnotexist");

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // ── JoinByInvite ──────────────────────────────────────────────────────────

    [Fact]
    public async Task JoinByInvite_ValidCode_AddsMemberAndReturnsServer()
    {
        var (server, admin, member) = await SeedAsync();
        TestHelpers.SetUser(_controller, admin.Id, admin.Username);
        var invite = ((OkObjectResult)(await _controller.GetInvite(server.Id)).Result!).Value as ServerInviteDto;

        TestHelpers.SetUser(_controller, member.Id, member.Username);
        var result = await _controller.JoinByInvite(invite!.Code);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ServerDto>(ok.Value);
        Assert.Equal(server.Id, dto.Id);

        var isMember = _testDb.Db.ServerMembers.Any(sm => sm.ServerId == server.Id && sm.UserId == member.Id);
        Assert.True(isMember);
    }

    [Fact]
    public async Task JoinByInvite_AlreadyMember_ReturnsConflict()
    {
        var (server, admin, _) = await SeedAsync();
        TestHelpers.SetUser(_controller, admin.Id, admin.Username);
        var invite = ((OkObjectResult)(await _controller.GetInvite(server.Id)).Result!).Value as ServerInviteDto;

        // Admin tries to join their own server via invite
        var result = await _controller.JoinByInvite(invite!.Code);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task JoinByInvite_InvalidCode_ReturnsNotFound()
    {
        var (_, _, member) = await SeedAsync();
        TestHelpers.SetUser(_controller, member.Id, member.Username);

        var result = await _controller.JoinByInvite("badcode");

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task JoinByInvite_AfterReset_OldCodeNoLongerWorks()
    {
        var (server, admin, member) = await SeedAsync();
        TestHelpers.SetUser(_controller, admin.Id, admin.Username);
        var oldInvite = ((OkObjectResult)(await _controller.GetInvite(server.Id)).Result!).Value as ServerInviteDto;
        await _controller.ResetInvite(server.Id); // invalidates old code

        TestHelpers.SetUser(_controller, member.Id, member.Username);
        var result = await _controller.JoinByInvite(oldInvite!.Code);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }
}
