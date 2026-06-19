using System.Net;
using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Tests.Server;

public class UserProfileEndpointTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly AdminController _controller;
    private readonly OnlineTimeTracker _tracker = new();

    public UserProfileEndpointTests()
    {
        _testDb = new TestDb();
        _controller = new AdminController(_testDb.Db, TestHelpers.MockHub(), new ServerMetricsService());
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Connection = { RemoteIpAddress = IPAddress.Loopback } }
        };
    }

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public async Task UnknownUser_Returns404()
    {
        var result = await _controller.GetUserProfile(999, _tracker, null);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ReturnsAggregateForSeededUser()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_testDb.Db, "owner");
        var user = new User { Username = "jane", Email = "jane@test.com", PasswordHash = "x",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), TotalOnlineSeconds = 3600 };
        _testDb.Db.Users.Add(user);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = user.Id, Role = ServerRole.Admin });
        _testDb.Db.UserSessions.Add(new UserSession { UserId = user.Id, TokenHash = "h1", IpAddress = "1.2.3.4", UserAgent = "UA", CreatedAt = DateTime.UtcNow });
        var channel = _testDb.Db.Channels.First();
        _testDb.Db.Messages.Add(new Message { AuthorId = user.Id, ChannelId = channel.Id, Content = "hi" });
        await _testDb.Db.SaveChangesAsync();

        var result = await _controller.GetUserProfile(user.Id, _tracker, server.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<UserProfileAdminDto>(ok.Value);
        Assert.Equal("jane", dto.Username);
        Assert.Equal("Admin", dto.Role);
        Assert.Equal(1, dto.MessageCount);
        Assert.Equal("1.2.3.4", dto.LastLoginIp);
        Assert.Equal(1, dto.ActiveSessionCount);
        Assert.Equal(3600, dto.TotalOnlineSeconds);
    }

    [Fact]
    public async Task NonLoopback_Forbidden()
    {
        _controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("8.8.8.8");
        var result = await _controller.GetUserProfile(1, _tracker, null);
        Assert.IsType<ForbidResult>(result);
    }
}
