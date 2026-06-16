using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Tests.Server;

public class UsersControllerTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly UsersController _controller;
    private GuildServer _server = null!;
    private User _owner = null!;
    private User _other = null!;

    public UsersControllerTests()
    {
        _testDb = new TestDb();
        _controller = new UsersController(_testDb.Db);
    }

    public void Dispose() => _testDb.Dispose();

    private async Task SeedAsync()
    {
        (_server, _owner) = await TestHelpers.SeedServerAsync(_testDb.Db, "owner");
        _other = new User { Username = "other", Email = "other@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(_other);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember
        {
            ServerId = _server.Id,
            UserId = _other.Id,
            Role = ServerRole.Member
        });
        await _testDb.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetProfile_ReturnsUserInfo()
    {
        await SeedAsync();
        TestHelpers.SetUser(_controller, _owner.Id, _owner.Username);

        var result = await _controller.GetProfile(_other.Id, _server.Id);

        var dto = result.Value;
        Assert.NotNull(dto);
        Assert.Equal("other", dto.Username);
        Assert.Equal(ServerRole.Member, dto.Role);
        Assert.False(dto.IsCurrentUser);
    }

    [Fact]
    public async Task GetProfile_OwnProfile_IsCurrentUserTrue()
    {
        await SeedAsync();
        TestHelpers.SetUser(_controller, _owner.Id, _owner.Username);

        var result = await _controller.GetProfile(_owner.Id, _server.Id);

        var dto = result.Value;
        Assert.NotNull(dto);
        Assert.True(dto.IsCurrentUser);
        Assert.Equal(ServerRole.Admin, dto.Role);
    }

    [Fact]
    public async Task GetProfile_WithoutServerId_HasNullRole()
    {
        await SeedAsync();
        TestHelpers.SetUser(_controller, _owner.Id, _owner.Username);

        var result = await _controller.GetProfile(_other.Id, null);

        var dto = result.Value;
        Assert.NotNull(dto);
        Assert.Null(dto.Role);
        Assert.Null(dto.JoinedAt);
    }

    [Fact]
    public async Task GetProfile_UnknownUser_ReturnsNotFound()
    {
        await SeedAsync();
        TestHelpers.SetUser(_controller, _owner.Id, _owner.Username);

        var result = await _controller.GetProfile(999_999, null);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdateStatus_ChangesStatus()
    {
        await SeedAsync();
        TestHelpers.SetUser(_controller, _owner.Id, _owner.Username);

        var result = await _controller.UpdateStatus(new UpdateStatusRequest(UserStatus.Away), TestHelpers.MockHub());

        Assert.IsType<NoContentResult>(result);
        var user = await _testDb.Db.Users.FindAsync(_owner.Id);
        Assert.Equal(UserStatus.Away, user!.Status);
    }

    [Fact]
    public async Task UpdateStatus_AllStatuses_Persist()
    {
        await SeedAsync();
        TestHelpers.SetUser(_controller, _owner.Id, _owner.Username);

        foreach (var status in Enum.GetValues<UserStatus>())
        {
            await _controller.UpdateStatus(new UpdateStatusRequest(status), TestHelpers.MockHub());
            var user = await _testDb.Db.Users.FindAsync(_owner.Id);
            Assert.Equal(status, user!.Status);
        }
    }

    [Fact]
    public async Task GetProfile_WhenLastSeenSet_ReturnsLastSeenAt()
    {
        await SeedAsync();
        var expectedTime = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        _other.LastSeenAt = expectedTime;
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_controller, _owner.Id, _owner.Username);

        var result = await _controller.GetProfile(_other.Id, _server.Id);

        Assert.Equal(expectedTime, result.Value!.LastSeenAt);
    }

    [Fact]
    public async Task GetProfile_WhenLastSeenNotSet_ReturnsNullLastSeenAt()
    {
        await SeedAsync();
        TestHelpers.SetUser(_controller, _owner.Id, _owner.Username);

        var result = await _controller.GetProfile(_other.Id, _server.Id);

        Assert.Null(result.Value!.LastSeenAt);
    }

    [Fact]
    public async Task UpdateUsername_Success_ChangesName()
    {
        var user = new User { Username = "oldname", Email = "u@test.com", PasswordHash = "x" };
        _testDb.Db.Users.Add(user);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_controller, user.Id);

        var result = await _controller.UpdateUsername(new UpdateUsernameRequest("newname"));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("newname", _testDb.Db.Users.Single(u => u.Id == user.Id).Username);
    }

    [Fact]
    public async Task UpdateUsername_InvalidChars_ReturnsBadRequest()
    {
        var user = new User { Username = "oldname", Email = "u@test.com", PasswordHash = "x" };
        _testDb.Db.Users.Add(user);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_controller, user.Id);

        var result = await _controller.UpdateUsername(new UpdateUsernameRequest("bad name!"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateUsername_Taken_ReturnsConflict()
    {
        var user = new User { Username = "oldname", Email = "u@test.com", PasswordHash = "x" };
        var other = new User { Username = "taken", Email = "o@test.com", PasswordHash = "x" };
        _testDb.Db.Users.AddRange(user, other);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_controller, user.Id);

        var result = await _controller.UpdateUsername(new UpdateUsernameRequest("taken"));

        Assert.IsType<ConflictObjectResult>(result);
    }
}
