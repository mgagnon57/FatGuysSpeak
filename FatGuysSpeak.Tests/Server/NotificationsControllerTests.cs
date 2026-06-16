using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Tests.Server;

public class NotificationsControllerTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly NotificationsController _ctrl;
    private readonly ServersController _serversCtrl;
    private GuildServer _server = null!;
    private User _member = null!;
    private User _outsider = null!;
    private Channel _channel = null!;

    public NotificationsControllerTests()
    {
        _testDb = new TestDb();
        _ctrl = new NotificationsController(_testDb.Db);
        _serversCtrl = new ServersController(_testDb.Db, TestHelpers.MockHub(), TestHelpers.NullWebhooks());
    }

    public void Dispose() => _testDb.Dispose();

    private async Task SeedAsync()
    {
        (_server, _member) = await TestHelpers.SeedServerAsync(_testDb.Db);
        _channel = _testDb.Db.Channels.First(c => c.ServerId == _server.Id);

        _outsider = new User { Username = "outsider", Email = "out@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(_outsider);
        await _testDb.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetServerNotif_WhenNoPreference_ReturnsNull()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        var result = await _ctrl.GetServerNotif(_server.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Null(ok.Value);
    }

    [Fact]
    public async Task SetServerNotif_AsMember_StoresLevel()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        await _ctrl.SetServerNotif(_server.Id, new SetNotifLevelRequest(NotifLevel.Muted));

        var saved = await _testDb.Db.UserServerNotifs.FindAsync(_member.Id, _server.Id);
        Assert.Equal(NotifLevel.Muted, saved!.Level);
    }

    [Fact]
    public async Task GetServerNotif_AfterSet_ReturnsStoredLevel()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);
        await _ctrl.SetServerNotif(_server.Id, new SetNotifLevelRequest(NotifLevel.OnlyMentions));

        var result = await _ctrl.GetServerNotif(_server.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(NotifLevel.OnlyMentions, ok.Value);
    }

    [Fact]
    public async Task SetServerNotif_AsNonMember_ReturnsForbid()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _outsider.Id, _outsider.Username);

        var result = await _ctrl.SetServerNotif(_server.Id, new SetNotifLevelRequest(NotifLevel.Muted));

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task SetServerNotif_Twice_UpdatesExisting()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);
        await _ctrl.SetServerNotif(_server.Id, new SetNotifLevelRequest(NotifLevel.Muted));

        await _ctrl.SetServerNotif(_server.Id, new SetNotifLevelRequest(NotifLevel.All));

        var saved = await _testDb.Db.UserServerNotifs.FindAsync(_member.Id, _server.Id);
        Assert.Equal(NotifLevel.All, saved!.Level);
    }

    [Fact]
    public async Task GetChannelNotif_WhenNoPreference_ReturnsNull()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        var result = await _ctrl.GetChannelNotif(_channel.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Null(ok.Value);
    }

    [Fact]
    public async Task SetChannelNotif_AsMember_StoresLevel()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        await _ctrl.SetChannelNotif(_channel.Id, new SetNotifLevelRequest(NotifLevel.OnlyMentions));

        var saved = await _testDb.Db.UserChannelNotifs.FindAsync(_member.Id, _channel.Id);
        Assert.Equal(NotifLevel.OnlyMentions, saved!.Level);
    }

    [Fact]
    public async Task SetChannelNotif_AsNonMember_ReturnsForbid()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _outsider.Id, _outsider.Username);

        var result = await _ctrl.SetChannelNotif(_channel.Id, new SetNotifLevelRequest(NotifLevel.Muted));

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetChannels_IncludesUserNotifLevel()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);
        await _ctrl.SetChannelNotif(_channel.Id, new SetNotifLevelRequest(NotifLevel.Muted));
        TestHelpers.SetUser(_serversCtrl, _member.Id, _member.Username);

        var result = await _serversCtrl.GetChannels(_server.Id);

        var ch = result.Value!.First(c => c.Id == _channel.Id);
        Assert.Equal(NotifLevel.Muted, ch.UserNotifLevel);
    }

    [Fact]
    public async Task GetChannels_WhenNoPreference_UserNotifLevelIsNull()
    {
        await SeedAsync();
        TestHelpers.SetUser(_serversCtrl, _member.Id, _member.Username);

        var result = await _serversCtrl.GetChannels(_server.Id);

        Assert.All(result.Value!, c => Assert.Null(c.UserNotifLevel));
    }

    [Fact]
    public async Task GetMyServers_IncludesUserNotifLevel()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);
        await _ctrl.SetServerNotif(_server.Id, new SetNotifLevelRequest(NotifLevel.OnlyMentions));
        TestHelpers.SetUser(_serversCtrl, _member.Id, _member.Username);

        var result = await _serversCtrl.GetMyServers();

        var srv = result.First(s => s.Id == _server.Id);
        Assert.Equal(NotifLevel.OnlyMentions, srv.UserNotifLevel);
    }
}
