using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Tests.Server;

public class UserBioTests : IDisposable
{
    private readonly TestDb _testDb;

    public UserBioTests() => _testDb = new TestDb();
    public void Dispose() => _testDb.Dispose();

    private async Task<User> CreateUserAsync(string username)
    {
        var user = new User { Username = username, Email = $"{username}@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(user);
        await _testDb.Db.SaveChangesAsync();
        return user;
    }

    private UsersController MakeController(int userId, string username)
    {
        var ctrl = new UsersController(_testDb.Db);
        TestHelpers.SetUser(ctrl, userId, username);
        return ctrl;
    }

    // ── UpdateBio ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateBio_SetsBio()
    {
        var user = await CreateUserAsync("bio-user1");
        var result = await MakeController(user.Id, user.Username)
            .UpdateBio(new UpdateBioRequest("Hello, I am a fat guy."));
        Assert.IsType<NoContentResult>(result);
        Assert.Equal("Hello, I am a fat guy.", _testDb.Db.Users.Find(user.Id)!.Bio);
    }

    [Fact]
    public async Task UpdateBio_TrimsWhitespace()
    {
        var user = await CreateUserAsync("bio-user2");
        await MakeController(user.Id, user.Username)
            .UpdateBio(new UpdateBioRequest("  trimmed  "));
        Assert.Equal("trimmed", _testDb.Db.Users.Find(user.Id)!.Bio);
    }

    [Fact]
    public async Task UpdateBio_NullClearsBio()
    {
        var user = await CreateUserAsync("bio-user3");
        user.Bio = "existing";
        await _testDb.Db.SaveChangesAsync();

        await MakeController(user.Id, user.Username).UpdateBio(new UpdateBioRequest(null));
        Assert.Null(_testDb.Db.Users.Find(user.Id)!.Bio);
    }

    [Fact]
    public async Task UpdateBio_EmptyStringClearsBio()
    {
        var user = await CreateUserAsync("bio-user4");
        user.Bio = "existing";
        await _testDb.Db.SaveChangesAsync();

        await MakeController(user.Id, user.Username).UpdateBio(new UpdateBioRequest("   "));
        Assert.Null(_testDb.Db.Users.Find(user.Id)!.Bio);
    }

    [Fact]
    public async Task UpdateBio_TooLong_ReturnsBadRequest()
    {
        var user = await CreateUserAsync("bio-user5");
        var longBio = new string('x', 301);
        var result = await MakeController(user.Id, user.Username)
            .UpdateBio(new UpdateBioRequest(longBio));
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateBio_Exactly300Chars_Succeeds()
    {
        var user = await CreateUserAsync("bio-user6");
        var bio300 = new string('a', 300);
        var result = await MakeController(user.Id, user.Username)
            .UpdateBio(new UpdateBioRequest(bio300));
        Assert.IsType<NoContentResult>(result);
    }

    // ── GetProfile includes Bio ────────────────────────────────────────────────

    [Fact]
    public async Task GetProfile_ReturnsBio()
    {
        var user = await CreateUserAsync("bio-user7");
        user.Bio = "My bio text";
        await _testDb.Db.SaveChangesAsync();

        var ctrl = MakeController(user.Id, user.Username);
        var result = await ctrl.GetProfile(user.Id, null);
        Assert.Equal("My bio text", result.Value!.Bio);
    }

    [Fact]
    public async Task GetProfile_NoBio_ReturnsNullBio()
    {
        var user = await CreateUserAsync("bio-user8");

        var ctrl = MakeController(user.Id, user.Username);
        var result = await ctrl.GetProfile(user.Id, null);
        Assert.Null(result.Value!.Bio);
    }

    [Fact]
    public async Task GetProfile_OtherUser_ReturnsBio()
    {
        var owner = await CreateUserAsync("bio-viewer");
        var target = await CreateUserAsync("bio-target");
        target.Bio = "Public bio";
        await _testDb.Db.SaveChangesAsync();

        var ctrl = MakeController(owner.Id, owner.Username);
        var result = await ctrl.GetProfile(target.Id, null);
        Assert.Equal("Public bio", result.Value!.Bio);
        Assert.False(result.Value.IsCurrentUser);
    }
}
