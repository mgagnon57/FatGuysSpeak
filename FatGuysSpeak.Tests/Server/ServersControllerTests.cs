using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Tests.Server;

public class ServersControllerTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly ServersController _controller;
    private GuildServer _server = null!;
    private User _user = null!;

    public ServersControllerTests()
    {
        _testDb = new TestDb();
        _controller = new ServersController(_testDb.Db, TestHelpers.MockHub());
    }

    public void Dispose() => _testDb.Dispose();

    private async Task SeedAsync()
    {
        (_server, _user) = await TestHelpers.SeedServerAsync(_testDb.Db);
        TestHelpers.SetUser(_controller, _user.Id, _user.Username);
    }

    [Fact]
    public async Task GetMyServers_ReturnsMemberServers()
    {
        await SeedAsync();

        var result = await _controller.GetMyServers();

        Assert.Single(result);
        Assert.Equal("FatGuysSpeak", result[0].Name);
    }

    [Fact]
    public async Task GetMyServers_ExcludesNonMemberServers()
    {
        await SeedAsync();
        var other = new User { Username = "other", Email = "other@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(other);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_controller, other.Id, other.Username);

        var result = await _controller.GetMyServers();

        Assert.Empty(result);
    }

    [Fact]
    public async Task CreateServer_CreatesServerWithDefaultChannels()
    {
        await SeedAsync();

        var result = await _controller.CreateServer(new CreateServerRequest("MyServer", null));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ServerDto>(ok.Value);
        Assert.Equal("MyServer", dto.Name);

        var channels = _testDb.Db.Channels.Where(c => c.ServerId == dto.Id).ToList();
        Assert.Equal(2, channels.Count);
        Assert.Contains(channels, c => c.Type == ChannelType.Text);
        Assert.Contains(channels, c => c.Type == ChannelType.Voice);
    }

    [Fact]
    public async Task JoinServer_Success_AddsMembership()
    {
        await SeedAsync();
        var newUser = new User { Username = "newbie", Email = "newbie@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(newUser);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_controller, newUser.Id, newUser.Username);

        var result = await _controller.JoinServer(_server.Id);

        Assert.IsType<OkResult>(result);
        Assert.True(_testDb.Db.ServerMembers.Any(sm => sm.ServerId == _server.Id && sm.UserId == newUser.Id));
    }

    [Fact]
    public async Task JoinServer_AlreadyMember_ReturnsConflict()
    {
        await SeedAsync();

        var result = await _controller.JoinServer(_server.Id);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task JoinServer_NotFound_ReturnsNotFound()
    {
        await SeedAsync();

        var result = await _controller.JoinServer(999);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetChannels_ReturnsMemberChannels()
    {
        await SeedAsync();

        var result = await _controller.GetChannels(_server.Id);

        var channels = result.Value ?? (result.Result as OkObjectResult)?.Value as List<ChannelDto>;
        Assert.NotNull(channels);
        Assert.Equal(2, channels.Count);
    }

    [Fact]
    public async Task GetChannels_NonMember_ReturnsForbid()
    {
        await SeedAsync();
        var other = new User { Username = "stranger", Email = "stranger@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(other);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_controller, other.Id, other.Username);

        var result = await _controller.GetChannels(_server.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task CreateChannel_Success_AddsChannel()
    {
        await SeedAsync();

        var result = await _controller.CreateChannel(_server.Id, new CreateChannelRequest("announcements", ChannelType.Text));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ChannelDto>(ok.Value);
        Assert.Equal("announcements", dto.Name);
        Assert.Equal(ChannelType.Text, dto.Type);
    }

    [Fact]
    public async Task CreateChannel_NonMember_ReturnsForbid()
    {
        await SeedAsync();
        var stranger = new User { Username = "stranger", Email = "s@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(stranger);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_controller, stranger.Id);

        var result = await _controller.CreateChannel(_server.Id, new CreateChannelRequest("hack", ChannelType.Text));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetMembers_ReturnsAllServerMembers()
    {
        await SeedAsync();
        var extra = new User { Username = "extra", Email = "extra@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(extra);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = _server.Id, UserId = extra.Id });
        await _testDb.Db.SaveChangesAsync();

        var result = await _controller.GetMembers(_server.Id);

        var members = result.Value ?? (result.Result as OkObjectResult)?.Value as List<UserDto>;
        Assert.NotNull(members);
        Assert.Equal(2, members.Count);
    }
}
