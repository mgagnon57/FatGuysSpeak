using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Tests.Server;

public class ServerIconTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly ServersController _ctrl;
    private GuildServer _server = null!;
    private User _admin = null!;
    private User _member = null!;

    public ServerIconTests()
    {
        _testDb = new TestDb();
        _ctrl = new ServersController(_testDb.Db, TestHelpers.MockHub(), TestHelpers.NullWebhooks());
    }

    public void Dispose() => _testDb.Dispose();

    private async Task SeedAsync()
    {
        (_server, _admin) = await TestHelpers.SeedServerAsync(_testDb.Db);
        _member = new User { Username = "member", Email = "m@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(_member);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember
            { ServerId = _server.Id, UserId = _member.Id, Role = ServerRole.Member });
        await _testDb.Db.SaveChangesAsync();
    }

    private static IFormFile CreateFormFile(byte[] content, string contentType = "image/png", string fileName = "icon.png")
    {
        var ms = new MemoryStream(content);
        return new FormFile(ms, 0, content.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }

    [Fact]
    public async Task GetIcon_WhenNotSet_ReturnsNotFound()
    {
        await SeedAsync();

        var result = await _ctrl.GetIcon(_server.Id);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task UploadIcon_AsAdmin_StoresDataAndMimeType()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _admin.Id, _admin.Username);
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG magic bytes

        await _ctrl.UploadIcon(_server.Id, CreateFormFile(bytes));

        var saved = await _testDb.Db.Servers.FindAsync(_server.Id);
        Assert.Equal(bytes, saved!.IconData);
        Assert.Equal("image/png", saved.IconMimeType);
    }

    [Fact]
    public async Task GetIcon_AfterUpload_ReturnsBytesWithCorrectContentType()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _admin.Id, _admin.Username);
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        await _ctrl.UploadIcon(_server.Id, CreateFormFile(bytes, "image/png"));

        var result = await _ctrl.GetIcon(_server.Id);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(bytes, file.FileContents);
        Assert.Equal("image/png", file.ContentType);
    }

    [Fact]
    public async Task UploadIcon_AsMember_ReturnsForbid()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        var result = await _ctrl.UploadIcon(_server.Id, CreateFormFile(new byte[10]));

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task UploadIcon_OversizedFile_ReturnsBadRequest()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _admin.Id, _admin.Username);

        var result = await _ctrl.UploadIcon(_server.Id, CreateFormFile(new byte[1024 * 1024 + 1]));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UploadIcon_NonImageContentType_ReturnsBadRequest()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _admin.Id, _admin.Username);

        var result = await _ctrl.UploadIcon(_server.Id, CreateFormFile(new byte[10], "application/octet-stream", "file.bin"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task DeleteIcon_AsAdmin_ClearsIconData()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _admin.Id, _admin.Username);
        await _ctrl.UploadIcon(_server.Id, CreateFormFile(new byte[] { 1, 2, 3 }));

        await _ctrl.DeleteIcon(_server.Id);

        var saved = await _testDb.Db.Servers.FindAsync(_server.Id);
        Assert.Null(saved!.IconData);
        Assert.Null(saved.IconMimeType);
    }

    [Fact]
    public async Task DeleteIcon_AsMember_ReturnsForbid()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        var result = await _ctrl.DeleteIcon(_server.Id);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetMyServers_ReturnsHasIconTrue_WhenIconSet()
    {
        await SeedAsync();
        var server = await _testDb.Db.Servers.FindAsync(_server.Id);
        server!.IconData = new byte[] { 1, 2, 3 };
        server.IconMimeType = "image/png";
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_ctrl, _admin.Id, _admin.Username);

        var result = await _ctrl.GetMyServers();

        Assert.True(result.Single(s => s.Id == _server.Id).HasIcon);
    }

    [Fact]
    public async Task GetMyServers_ReturnsHasIconFalse_WhenNoIcon()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _admin.Id, _admin.Username);

        var result = await _ctrl.GetMyServers();

        Assert.False(result.Single(s => s.Id == _server.Id).HasIcon);
    }
}
