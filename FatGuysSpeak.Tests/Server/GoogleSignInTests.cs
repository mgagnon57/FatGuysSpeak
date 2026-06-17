using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Tests.Server;

public class GoogleSignInTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly AuthController _controller;
    private readonly FakeGoogleTokenValidator _validator = new();

    public GoogleSignInTests()
    {
        _testDb = new TestDb();
        _controller = new AuthController(_testDb.Db, TestHelpers.CreateTokenService(),
            new SessionBlacklistService());
    }

    public void Dispose() => _testDb.Dispose();

    private Task<ActionResult<AuthResponse>> SignIn() =>
        _controller.GoogleSignIn(new GoogleAuthRequest("any-token"), _validator);

    [Fact]
    public async Task NewGoogleUser_CreatesAccountAndReturnsToken()
    {
        _validator.Identity = new GoogleIdentity("sub-1", "jane@gmail.com", true, "Jane Doe");

        var result = await SignIn();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var auth = Assert.IsType<AuthResponse>(ok.Value);
        Assert.False(string.IsNullOrWhiteSpace(auth.Token));
        Assert.Equal("janedoe", auth.Username);
        var user = _testDb.Db.Users.Single(u => u.Email == "jane@gmail.com");
        Assert.Equal("", user.PasswordHash);
        Assert.True(_testDb.Db.ExternalLogins.Any(e => e.Provider == "google" && e.ProviderUserId == "sub-1" && e.UserId == user.Id));
    }

    [Fact]
    public async Task SameSub_SecondSignIn_ReturnsSameUserNoDuplicate()
    {
        _validator.Identity = new GoogleIdentity("sub-1", "jane@gmail.com", true, "Jane Doe");
        var first = await SignIn();
        var firstAuth = (AuthResponse)((OkObjectResult)first.Result!).Value!;

        var second = await SignIn();
        var secondAuth = (AuthResponse)((OkObjectResult)second.Result!).Value!;

        Assert.Equal(firstAuth.UserId, secondAuth.UserId);
        Assert.Equal(1, _testDb.Db.Users.Count(u => u.Email == "jane@gmail.com"));
        Assert.Equal(1, _testDb.Db.ExternalLogins.Count(e => e.ProviderUserId == "sub-1"));
    }

    [Fact]
    public async Task MatchingEmail_AutoLinksToExistingPasswordAccount()
    {
        var existing = new User { Username = "jane", Email = "jane@gmail.com", PasswordHash = BCrypt.Net.BCrypt.HashPassword("secret") };
        _testDb.Db.Users.Add(existing);
        await _testDb.Db.SaveChangesAsync();
        _validator.Identity = new GoogleIdentity("sub-1", "jane@gmail.com", true, "Jane Doe");

        var result = await SignIn();

        var auth = (AuthResponse)((OkObjectResult)result.Result!).Value!;
        Assert.Equal(existing.Id, auth.UserId);
        Assert.Equal("jane", auth.Username);
        Assert.Equal(1, _testDb.Db.Users.Count(u => u.Email == "jane@gmail.com"));
        Assert.True(_testDb.Db.ExternalLogins.Any(e => e.UserId == existing.Id && e.ProviderUserId == "sub-1"));
        var reloaded = _testDb.Db.Users.Single(u => u.Id == existing.Id);
        Assert.False(string.IsNullOrEmpty(reloaded.PasswordHash));
    }

    [Fact]
    public async Task UnverifiedEmail_ReturnsUnauthorizedAndCreatesNothing()
    {
        _validator.Identity = new GoogleIdentity("sub-1", "jane@gmail.com", false, "Jane Doe");

        var result = await SignIn();

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Empty(_testDb.Db.Users);
        Assert.Empty(_testDb.Db.ExternalLogins);
    }

    [Fact]
    public async Task InvalidToken_ReturnsUnauthorizedAndTouchesNoDb()
    {
        _validator.ThrowOnValidate = new InvalidJwtException("bad token");

        var result = await SignIn();

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Empty(_testDb.Db.Users);
        Assert.Empty(_testDb.Db.ExternalLogins);
    }

    [Fact]
    public async Task NotConfigured_ReturnsServiceUnavailable()
    {
        _validator.ThrowOnValidate = new InvalidOperationException("Google sign-in is not configured (Google:ClientId is missing).");

        var result = await SignIn();

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, status.StatusCode);
        Assert.Empty(_testDb.Db.Users);
        Assert.Empty(_testDb.Db.ExternalLogins);
    }

    [Fact]
    public async Task UsernameCollision_AppendsSuffix()
    {
        _testDb.Db.Users.Add(new User { Username = "janedoe", Email = "other@gmail.com", PasswordHash = "x" });
        await _testDb.Db.SaveChangesAsync();
        _validator.Identity = new GoogleIdentity("sub-1", "jane@gmail.com", true, "Jane Doe");

        var result = await SignIn();

        var auth = (AuthResponse)((OkObjectResult)result.Result!).Value!;
        Assert.Equal("janedoe1", auth.Username);
    }

    [Fact]
    public async Task NewGoogleUser_AutoJoinsDefaultServer()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_testDb.Db, "owner");
        _validator.Identity = new GoogleIdentity("sub-1", "jane@gmail.com", true, "Jane Doe");

        var result = await SignIn();

        var auth = (AuthResponse)((OkObjectResult)result.Result!).Value!;
        Assert.True(_testDb.Db.ServerMembers.Any(sm => sm.ServerId == server.Id && sm.UserId == auth.UserId));
    }
}
