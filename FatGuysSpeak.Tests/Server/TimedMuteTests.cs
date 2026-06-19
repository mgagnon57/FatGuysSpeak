using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Tests.Server;

[Collection("HubTests")]
public class TimedMuteTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly ServersController _serverCtrl;
    private readonly MessagesController _msgCtrl;
    private GuildServer _server = null!;
    private User _admin = null!;
    private User _member = null!;

    public TimedMuteTests()
    {
        _testDb = new TestDb();
        _serverCtrl = new ServersController(_testDb.Db, TestHelpers.MockHub(), TestHelpers.NullWebhooks());
        _msgCtrl = new MessagesController(_testDb.Db, TestHelpers.MockHub(),
            new FatGuysSpeak.Server.Services.ServerMetricsService(), TestHelpers.NullBot(), TestHelpers.NullAutomod(), TestHelpers.NullWebhooks());
    }

    public void Dispose() => _testDb.Dispose();

    private async Task SeedAsync()
    {
        (_server, _admin) = await TestHelpers.SeedServerAsync(_testDb.Db);

        _member = new User { Username = "member", Email = "m@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(_member);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = _server.Id, UserId = _member.Id, Role = ServerRole.Member });
        await _testDb.Db.SaveChangesAsync();
    }

    private int ChannelId => _testDb.Db.Channels.First(c => c.ServerId == _server.Id && c.Type == ChannelType.Text).Id;

    [Fact]
    public async Task MuteUser_AsAdmin_SetsMutedUntil()
    {
        await SeedAsync();
        TestHelpers.SetUser(_serverCtrl, _admin.Id, _admin.Username);

        var result = await _serverCtrl.MuteUser(_server.Id, _member.Id, new MuteUserRequest(300));

        Assert.IsType<NoContentResult>(result);
        var updated = await _testDb.Db.ServerMembers.FindAsync(_server.Id, _member.Id);
        Assert.NotNull(updated!.MutedUntil);
        Assert.True(updated.MutedUntil > DateTime.UtcNow);
    }

    [Fact]
    public async Task MuteUser_WithZeroSeconds_ClearsMute()
    {
        await SeedAsync();
        var sm = await _testDb.Db.ServerMembers.FindAsync(_server.Id, _member.Id);
        sm!.MutedUntil = DateTime.UtcNow.AddHours(1);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_serverCtrl, _admin.Id, _admin.Username);

        var result = await _serverCtrl.MuteUser(_server.Id, _member.Id, new MuteUserRequest(0));

        Assert.IsType<NoContentResult>(result);
        var updated = await _testDb.Db.ServerMembers.FindAsync(_server.Id, _member.Id);
        Assert.Null(updated!.MutedUntil);
    }

    [Fact]
    public async Task MuteUser_AsMember_ReturnsForbid()
    {
        await SeedAsync();
        TestHelpers.SetUser(_serverCtrl, _member.Id, _member.Username);

        var result = await _serverCtrl.MuteUser(_server.Id, _admin.Id, new MuteUserRequest(300));

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task MuteUser_CannotMuteHigherRole_ReturnsForbid()
    {
        await SeedAsync();
        // A second admin cannot mute a peer admin (mute requires the target's role be strictly lower).
        var other = new User { Username = "admin2", Email = "admin2@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(other);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = _server.Id, UserId = other.Id, Role = ServerRole.Admin });
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_serverCtrl, other.Id, other.Username);

        var result = await _serverCtrl.MuteUser(_server.Id, _admin.Id, new MuteUserRequest(300));

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task SendMessage_WhileMuted_Returns403()
    {
        await SeedAsync();
        var sm = await _testDb.Db.ServerMembers.FindAsync(_server.Id, _member.Id);
        sm!.MutedUntil = DateTime.UtcNow.AddHours(1);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_msgCtrl, _member.Id, _member.Username);

        var result = await _msgCtrl.SendMessage(ChannelId, new SendMessageRequest("hello"));

        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, statusResult.StatusCode);
    }

    [Fact]
    public async Task SendMessage_AfterMuteExpires_Succeeds()
    {
        await SeedAsync();
        var sm = await _testDb.Db.ServerMembers.FindAsync(_server.Id, _member.Id);
        sm!.MutedUntil = DateTime.UtcNow.AddSeconds(-1);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_msgCtrl, _member.Id, _member.Username);

        var result = await _msgCtrl.SendMessage(ChannelId, new SendMessageRequest("hello"));

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task TempBanUser_AsAdmin_RemovesMember()
    {
        await SeedAsync();
        TestHelpers.SetUser(_serverCtrl, _admin.Id, _admin.Username);

        var result = await _serverCtrl.TempBanUser(_server.Id, _member.Id, new TempBanRequest(3600));

        Assert.IsType<NoContentResult>(result);
        var stillMember = await _testDb.Db.ServerMembers.FindAsync(_server.Id, _member.Id);
        Assert.Null(stillMember);
    }

    [Fact]
    public async Task TempBanUser_AsAdmin_CreatesTempBanRecord()
    {
        await SeedAsync();
        TestHelpers.SetUser(_serverCtrl, _admin.Id, _admin.Username);

        await _serverCtrl.TempBanUser(_server.Id, _member.Id, new TempBanRequest(3600));

        var ban = await _testDb.Db.TempBans.FirstOrDefaultAsync(b =>
            b.ServerId == _server.Id && b.UserId == _member.Id);
        Assert.NotNull(ban);
        Assert.True(ban!.ExpiresAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task TempBanUser_AsMember_ReturnsForbid()
    {
        await SeedAsync();
        TestHelpers.SetUser(_serverCtrl, _member.Id, _member.Username);

        var result = await _serverCtrl.TempBanUser(_server.Id, _admin.Id, new TempBanRequest(3600));

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task TempBanUser_CannotBanSelf_ReturnsBadRequest()
    {
        await SeedAsync();
        TestHelpers.SetUser(_serverCtrl, _admin.Id, _admin.Username);

        var result = await _serverCtrl.TempBanUser(_server.Id, _admin.Id, new TempBanRequest(3600));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task TempBanUser_BanRecordHasCorrectExpiry()
    {
        await SeedAsync();
        TestHelpers.SetUser(_serverCtrl, _admin.Id, _admin.Username);
        var before = DateTime.UtcNow;

        await _serverCtrl.TempBanUser(_server.Id, _member.Id, new TempBanRequest(7200));

        var ban = await _testDb.Db.TempBans.FirstAsync(b => b.ServerId == _server.Id && b.UserId == _member.Id);
        Assert.True(ban.ExpiresAt >= before.AddSeconds(7200 - 2));
        Assert.True(ban.ExpiresAt <= before.AddSeconds(7202));
    }
}
