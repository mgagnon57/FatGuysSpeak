using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace FatGuysSpeak.Tests.Server;

public class CategoriesControllerTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly List<(string Target, string Method, object[] Args)> _hubCalls = [];
    private readonly IHubContext<FatGuysSpeak.Server.Hubs.ChatHub> _hub;
    private GuildServer _server = null!;
    private User _admin = null!;

    public CategoriesControllerTests()
    {
        _testDb = new TestDb();

        string lastTarget = "";
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
             .Callback<string, object[], CancellationToken>((m, a, _) => _hubCalls.Add((lastTarget, m, a)))
             .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>()))
               .Callback<string>(g => lastTarget = g)
               .Returns(proxy.Object);
        var hub = new Mock<IHubContext<FatGuysSpeak.Server.Hubs.ChatHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        _hub = hub.Object;
    }

    public void Dispose() => _testDb.Dispose();

    private async Task SeedAsync()
    {
        (_server, _admin) = await TestHelpers.SeedServerAsync(_testDb.Db, "cat-admin");
    }

    private CategoriesController MakeController(int userId, string username)
    {
        var ctrl = new CategoriesController(_testDb.Db, _hub);
        TestHelpers.SetUser(ctrl, userId, username);
        return ctrl;
    }

    // ── GetCategories ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCategories_EmptyServer_ReturnsEmpty()
    {
        await SeedAsync();
        var result = await MakeController(_admin.Id, _admin.Username).GetCategories(_server.Id);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task GetCategories_NonMember_ReturnsForbid()
    {
        await SeedAsync();
        var outsider = new User { Username = "cat-outsider", Email = "co@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(outsider);
        await _testDb.Db.SaveChangesAsync();

        var result = await MakeController(outsider.Id, "cat-outsider").GetCategories(_server.Id);
        Assert.IsType<ForbidResult>(result.Result);
    }

    // ── CreateCategory ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateCategory_Admin_ReturnsDto()
    {
        await SeedAsync();
        var result = await MakeController(_admin.Id, _admin.Username)
            .CreateCategory(_server.Id, new CreateCategoryRequest("General"));
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CategoryDto>(ok.Value);
        Assert.Equal("General", dto.Name);
        Assert.Equal(_server.Id, dto.ServerId);
    }

    [Fact]
    public async Task CreateCategory_Admin_PersistsToDb()
    {
        await SeedAsync();
        await MakeController(_admin.Id, _admin.Username)
            .CreateCategory(_server.Id, new CreateCategoryRequest("Persist Test"));
        Assert.True(_testDb.Db.Categories.Any(c => c.Name == "Persist Test" && c.ServerId == _server.Id));
    }

    [Fact]
    public async Task CreateCategory_Admin_BroadcastsCategoryCreated()
    {
        await SeedAsync();
        _hubCalls.Clear();
        await MakeController(_admin.Id, _admin.Username)
            .CreateCategory(_server.Id, new CreateCategoryRequest("Hub Test"));
        Assert.Contains(_hubCalls, c =>
            c.Method == "CategoryCreated" && c.Target == $"server-{_server.Id}");
    }

    [Fact]
    public async Task CreateCategory_NonAdmin_ReturnsForbid()
    {
        await SeedAsync();
        var member = new User { Username = "cat-member", Email = "cm@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(member);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = _server.Id, UserId = member.Id });
        await _testDb.Db.SaveChangesAsync();

        var result = await MakeController(member.Id, "cat-member")
            .CreateCategory(_server.Id, new CreateCategoryRequest("Forbidden"));
        Assert.IsType<ForbidResult>(result.Result);
    }

    // ── RenameCategory ────────────────────────────────────────────────────────

    [Fact]
    public async Task RenameCategory_Admin_UpdatesName()
    {
        await SeedAsync();
        var ctrl = MakeController(_admin.Id, _admin.Username);
        var created = (CategoryDto)((OkObjectResult)(await ctrl.CreateCategory(_server.Id, new CreateCategoryRequest("Old Name"))).Result!).Value!;

        var result = await ctrl.RenameCategory(_server.Id, created.Id, new RenameCategoryRequest("New Name"));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("New Name", _testDb.Db.Categories.Find(created.Id)!.Name);
    }

    [Fact]
    public async Task RenameCategory_Admin_BroadcastsCategoryRenamed()
    {
        await SeedAsync();
        var ctrl = MakeController(_admin.Id, _admin.Username);
        var created = (CategoryDto)((OkObjectResult)(await ctrl.CreateCategory(_server.Id, new CreateCategoryRequest("Before"))).Result!).Value!;
        _hubCalls.Clear();
        await ctrl.RenameCategory(_server.Id, created.Id, new RenameCategoryRequest("After"));

        Assert.Contains(_hubCalls, c =>
            c.Method == "CategoryRenamed"
            && c.Target == $"server-{_server.Id}"
            && (int)c.Args[0] == created.Id
            && (string)c.Args[1] == "After");
    }

    [Fact]
    public async Task RenameCategory_WrongServer_ReturnsNotFound()
    {
        await SeedAsync();
        var result = await MakeController(_admin.Id, _admin.Username)
            .RenameCategory(_server.Id, 9999, new RenameCategoryRequest("X"));
        Assert.IsType<NotFoundResult>(result);
    }

    // ── DeleteCategory ────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteCategory_Admin_RemovesFromDb()
    {
        await SeedAsync();
        var ctrl = MakeController(_admin.Id, _admin.Username);
        var created = (CategoryDto)((OkObjectResult)(await ctrl.CreateCategory(_server.Id, new CreateCategoryRequest("To Delete"))).Result!).Value!;

        await ctrl.DeleteCategory(_server.Id, created.Id);

        Assert.False(_testDb.Db.Categories.Any(c => c.Id == created.Id));
    }

    [Fact]
    public async Task DeleteCategory_NullsChannelCategoryId()
    {
        await SeedAsync();
        var ctrl = MakeController(_admin.Id, _admin.Username);
        var created = (CategoryDto)((OkObjectResult)(await ctrl.CreateCategory(_server.Id, new CreateCategoryRequest("WithChannels"))).Result!).Value!;

        var channel = _testDb.Db.Channels.First(c => c.ServerId == _server.Id);
        channel.CategoryId = created.Id;
        await _testDb.Db.SaveChangesAsync();

        await ctrl.DeleteCategory(_server.Id, created.Id);

        Assert.Null(_testDb.Db.Channels.Find(channel.Id)!.CategoryId);
    }

    [Fact]
    public async Task DeleteCategory_BroadcastsCategoryDeleted()
    {
        await SeedAsync();
        var ctrl = MakeController(_admin.Id, _admin.Username);
        var created = (CategoryDto)((OkObjectResult)(await ctrl.CreateCategory(_server.Id, new CreateCategoryRequest("Deleted"))).Result!).Value!;
        _hubCalls.Clear();
        await ctrl.DeleteCategory(_server.Id, created.Id);

        Assert.Contains(_hubCalls, c =>
            c.Method == "CategoryDeleted"
            && c.Target == $"server-{_server.Id}"
            && (int)c.Args[0] == created.Id);
    }

    // ── SetChannelCategory ────────────────────────────────────────────────────

    [Fact]
    public async Task SetChannelCategory_Admin_AssignsChannel()
    {
        await SeedAsync();
        var ctrl = MakeController(_admin.Id, _admin.Username);
        var cat = (CategoryDto)((OkObjectResult)(await ctrl.CreateCategory(_server.Id, new CreateCategoryRequest("Assign Target"))).Result!).Value!;
        var channel = _testDb.Db.Channels.First(c => c.ServerId == _server.Id);

        var result = await ctrl.SetChannelCategory(_server.Id, channel.Id, new SetChannelCategoryRequest(cat.Id));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(cat.Id, _testDb.Db.Channels.Find(channel.Id)!.CategoryId);
    }

    [Fact]
    public async Task SetChannelCategory_Admin_BroadcastsChannelCategoryChanged()
    {
        await SeedAsync();
        var ctrl = MakeController(_admin.Id, _admin.Username);
        var cat = (CategoryDto)((OkObjectResult)(await ctrl.CreateCategory(_server.Id, new CreateCategoryRequest("Broadcast Cat"))).Result!).Value!;
        var channel = _testDb.Db.Channels.First(c => c.ServerId == _server.Id);
        _hubCalls.Clear();
        await ctrl.SetChannelCategory(_server.Id, channel.Id, new SetChannelCategoryRequest(cat.Id));

        Assert.Contains(_hubCalls, c =>
            c.Method == "ChannelCategoryChanged"
            && c.Target == $"server-{_server.Id}"
            && (int)c.Args[0] == channel.Id);
    }

    [Fact]
    public async Task SetChannelCategory_NullCategoryId_UncategorizesChannel()
    {
        await SeedAsync();
        var ctrl = MakeController(_admin.Id, _admin.Username);
        var cat = (CategoryDto)((OkObjectResult)(await ctrl.CreateCategory(_server.Id, new CreateCategoryRequest("Unassign"))).Result!).Value!;
        var channel = _testDb.Db.Channels.First(c => c.ServerId == _server.Id);
        channel.CategoryId = cat.Id;
        await _testDb.Db.SaveChangesAsync();

        var result = await ctrl.SetChannelCategory(_server.Id, channel.Id, new SetChannelCategoryRequest(null));

        Assert.IsType<NoContentResult>(result);
        Assert.Null(_testDb.Db.Channels.Find(channel.Id)!.CategoryId);
    }

    [Fact]
    public async Task SetChannelCategory_NonExistentCategory_ReturnsBadRequest()
    {
        await SeedAsync();
        var channel = _testDb.Db.Channels.First(c => c.ServerId == _server.Id);
        var result = await MakeController(_admin.Id, _admin.Username)
            .SetChannelCategory(_server.Id, channel.Id, new SetChannelCategoryRequest(9999));
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── GetCategories returns data ─────────────────────────────────────────────

    [Fact]
    public async Task GetCategories_AfterCreate_ReturnsList()
    {
        await SeedAsync();
        var ctrl = MakeController(_admin.Id, _admin.Username);
        await ctrl.CreateCategory(_server.Id, new CreateCategoryRequest("A"));
        await ctrl.CreateCategory(_server.Id, new CreateCategoryRequest("B"));

        var result = await ctrl.GetCategories(_server.Id);
        Assert.Equal(2, result.Value!.Count);
    }
}
