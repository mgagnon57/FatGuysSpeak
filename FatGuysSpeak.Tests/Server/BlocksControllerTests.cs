using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Tests.Server;

public class BlocksControllerTests : IDisposable
{
    private readonly TestDb _testDb;

    public BlocksControllerTests() => _testDb = new TestDb();
    public void Dispose() => _testDb.Dispose();

    private async Task<User> CreateUserAsync(string username)
    {
        var user = new User { Username = username, Email = $"{username}@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(user);
        await _testDb.Db.SaveChangesAsync();
        return user;
    }

    private BlocksController MakeController(int userId, string username)
    {
        var ctrl = new BlocksController(_testDb.Db);
        TestHelpers.SetUser(ctrl, userId, username);
        return ctrl;
    }

    // ── GetBlocked ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetBlocked_EmptyByDefault()
    {
        var user = await CreateUserAsync("blocker1");
        var result = await MakeController(user.Id, user.Username).GetBlocked();
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task GetBlocked_ReturnsBlockedUsers()
    {
        var blocker = await CreateUserAsync("blocker2");
        var blocked = await CreateUserAsync("blocked2");
        _testDb.Db.UserBlocks.Add(new UserBlock { BlockerId = blocker.Id, BlockedId = blocked.Id });
        await _testDb.Db.SaveChangesAsync();

        var result = await MakeController(blocker.Id, blocker.Username).GetBlocked();
        var list = result.Value!;
        Assert.Single(list);
        Assert.Equal(blocked.Id, list[0].UserId);
        Assert.Equal(blocked.Username, list[0].Username);
    }

    [Fact]
    public async Task GetBlocked_DoesNotReturnOtherUsersBlocks()
    {
        var u1 = await CreateUserAsync("blocker3");
        var u2 = await CreateUserAsync("blocker4");
        var u3 = await CreateUserAsync("blocked3");
        _testDb.Db.UserBlocks.Add(new UserBlock { BlockerId = u2.Id, BlockedId = u3.Id });
        await _testDb.Db.SaveChangesAsync();

        var result = await MakeController(u1.Id, u1.Username).GetBlocked();
        Assert.Empty(result.Value!);
    }

    // ── BlockUser ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task BlockUser_CreatesBlock()
    {
        var blocker = await CreateUserAsync("blocker5");
        var target = await CreateUserAsync("blocked4");

        var result = await MakeController(blocker.Id, blocker.Username).BlockUser(target.Id);
        Assert.IsType<NoContentResult>(result);
        Assert.True(_testDb.Db.UserBlocks.Any(b => b.BlockerId == blocker.Id && b.BlockedId == target.Id));
    }

    [Fact]
    public async Task BlockUser_CannotBlockSelf()
    {
        var user = await CreateUserAsync("selfblocker");
        var result = await MakeController(user.Id, user.Username).BlockUser(user.Id);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task BlockUser_NonExistentUser_ReturnsNotFound()
    {
        var user = await CreateUserAsync("blocker6");
        var result = await MakeController(user.Id, user.Username).BlockUser(99999);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task BlockUser_Idempotent_AlreadyBlocked()
    {
        var blocker = await CreateUserAsync("blocker7");
        var target = await CreateUserAsync("blocked5");
        _testDb.Db.UserBlocks.Add(new UserBlock { BlockerId = blocker.Id, BlockedId = target.Id });
        await _testDb.Db.SaveChangesAsync();

        var result = await MakeController(blocker.Id, blocker.Username).BlockUser(target.Id);
        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, _testDb.Db.UserBlocks.Count(b => b.BlockerId == blocker.Id && b.BlockedId == target.Id));
    }

    // ── UnblockUser ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UnblockUser_RemovesBlock()
    {
        var blocker = await CreateUserAsync("blocker8");
        var target = await CreateUserAsync("blocked6");
        _testDb.Db.UserBlocks.Add(new UserBlock { BlockerId = blocker.Id, BlockedId = target.Id });
        await _testDb.Db.SaveChangesAsync();

        var result = await MakeController(blocker.Id, blocker.Username).UnblockUser(target.Id);
        Assert.IsType<NoContentResult>(result);
        Assert.False(_testDb.Db.UserBlocks.Any(b => b.BlockerId == blocker.Id && b.BlockedId == target.Id));
    }

    [Fact]
    public async Task UnblockUser_Idempotent_NotBlocked()
    {
        var blocker = await CreateUserAsync("blocker9");
        var target = await CreateUserAsync("blocked7");

        var result = await MakeController(blocker.Id, blocker.Username).UnblockUser(target.Id);
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task UnblockUser_DoesNotAffectOtherBlocks()
    {
        var u1 = await CreateUserAsync("blocker10");
        var u2 = await CreateUserAsync("blocker11");
        var target = await CreateUserAsync("blocked8");
        _testDb.Db.UserBlocks.Add(new UserBlock { BlockerId = u1.Id, BlockedId = target.Id });
        _testDb.Db.UserBlocks.Add(new UserBlock { BlockerId = u2.Id, BlockedId = target.Id });
        await _testDb.Db.SaveChangesAsync();

        await MakeController(u1.Id, u1.Username).UnblockUser(target.Id);
        Assert.True(_testDb.Db.UserBlocks.Any(b => b.BlockerId == u2.Id && b.BlockedId == target.Id));
    }
}
