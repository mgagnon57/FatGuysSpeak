using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Tests.Server;

public class AuthControllerTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly AuthController _controller;

    public AuthControllerTests()
    {
        _testDb = new TestDb();
        _controller = new AuthController(_testDb.Db, TestHelpers.CreateTokenService());
    }

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public async Task Register_Success_ReturnsToken()
    {
        var result = await _controller.Register(new RegisterRequest("alice", "pass123", "alice@test.com"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var auth = Assert.IsType<AuthResponse>(ok.Value);
        Assert.Equal("alice", auth.Username);
        Assert.False(string.IsNullOrWhiteSpace(auth.Token));
    }

    [Fact]
    public async Task Register_DuplicateUsername_ReturnsConflict()
    {
        await _controller.Register(new RegisterRequest("alice", "pass", "alice@test.com"));

        var result = await _controller.Register(new RegisterRequest("alice", "other", "other@test.com"));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Register_DuplicateEmail_ReturnsConflict()
    {
        await _controller.Register(new RegisterRequest("alice", "pass", "shared@test.com"));

        var result = await _controller.Register(new RegisterRequest("bob", "pass", "shared@test.com"));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Login_Success_ReturnsToken()
    {
        await _controller.Register(new RegisterRequest("alice", "pass123", "alice@test.com"));

        var result = await _controller.Login(new LoginRequest("alice", "pass123"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var auth = Assert.IsType<AuthResponse>(ok.Value);
        Assert.Equal("alice", auth.Username);
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorizedWithMessage()
    {
        await _controller.Register(new RegisterRequest("alice", "correct", "alice@test.com"));

        var result = await _controller.Login(new LoginRequest("alice", "wrong"));

        var unauth = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal("Invalid credentials.", unauth.Value);
    }

    [Fact]
    public async Task Login_UnknownUser_ReturnsUnauthorizedWithMessage()
    {
        var result = await _controller.Login(new LoginRequest("ghost", "pass"));

        var unauth = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal("Invalid credentials.", unauth.Value);
    }

    [Fact]
    public async Task Register_AutoJoinsDefaultServer()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_testDb.Db, "owner");

        await _controller.Register(new RegisterRequest("newuser", "pass", "new@test.com"));

        var newUser = _testDb.Db.Users.First(u => u.Username == "newuser");
        var isMember = _testDb.Db.ServerMembers
            .Any(sm => sm.ServerId == server.Id && sm.UserId == newUser.Id);
        Assert.True(isMember);
    }

    [Fact]
    public async Task Login_AutoJoinsDefaultServerIfNotMember()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_testDb.Db, "owner");

        // Register manually (no default server yet at time of register)
        var user = new User { Username = "late", Email = "late@test.com", PasswordHash = BCrypt.Net.BCrypt.HashPassword("pass") };
        _testDb.Db.Users.Add(user);
        await _testDb.Db.SaveChangesAsync();

        await _controller.Login(new LoginRequest("late", "pass"));

        var isMember = _testDb.Db.ServerMembers.Any(sm => sm.ServerId == server.Id && sm.UserId == user.Id);
        Assert.True(isMember);
    }
}
