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
        _controller = new AuthController(_testDb.Db, TestHelpers.CreateTokenService(), new FatGuysSpeak.Server.Services.SessionBlacklistService());
    }

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public async Task Register_Success_ReturnsToken()
    {
        var result = await _controller.Register(new RegisterRequest("alice", "password123"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var auth = Assert.IsType<AuthResponse>(ok.Value);
        Assert.Equal("alice", auth.Username);
        Assert.False(string.IsNullOrWhiteSpace(auth.Token));
    }

    [Fact]
    public async Task Register_DuplicateUsername_ReturnsConflict()
    {
        await _controller.Register(new RegisterRequest("alice", "password123"));

        var result = await _controller.Register(new RegisterRequest("alice", "password456"));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Register_MultipleAccountsWithoutEmail_AllSucceed()
    {
        // Manual registration no longer collects an email, so every account is created with an
        // empty email. The filtered unique index must allow more than one such account.
        var first = await _controller.Register(new RegisterRequest("alice", "password123"));
        var second = await _controller.Register(new RegisterRequest("bob", "password123"));

        Assert.IsType<OkObjectResult>(first.Result);
        Assert.IsType<OkObjectResult>(second.Result);
    }

    [Fact]
    public async Task Login_Success_ReturnsToken()
    {
        await _controller.Register(new RegisterRequest("alice", "password123"));

        var result = await _controller.Login(new LoginRequest("alice", "password123"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var auth = Assert.IsType<AuthResponse>(ok.Value);
        Assert.Equal("alice", auth.Username);
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorizedWithMessage()
    {
        await _controller.Register(new RegisterRequest("alice", "correctpass"));

        var result = await _controller.Login(new LoginRequest("alice", "wrongpass"));

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

        await _controller.Register(new RegisterRequest("newuser", "password123"));

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

    [Fact]
    public async Task Login_PasswordlessAccount_ReturnsUnauthorized()
    {
        var user = new User { Username = "googleuser", Email = "g@test.com", PasswordHash = "" };
        _testDb.Db.Users.Add(user);
        await _testDb.Db.SaveChangesAsync();

        var result = await _controller.Login(new LoginRequest("googleuser", "anything"));

        var unauth = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal("Invalid credentials.", unauth.Value);
    }

    [Fact]
    public async Task ForgotPassword_PasswordlessAccount_CreatesNoResetToken()
    {
        var user = new User { Username = "googleuser", Email = "g@test.com", PasswordHash = "" };
        _testDb.Db.Users.Add(user);
        await _testDb.Db.SaveChangesAsync();

        await _controller.ForgotPassword(new ForgotPasswordRequest("g@test.com"));

        Assert.False(_testDb.Db.PasswordResetTokens.Any(t => t.UserId == user.Id));
    }
}
