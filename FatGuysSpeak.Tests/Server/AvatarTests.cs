using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace FatGuysSpeak.Tests.Server;

public class AvatarTests : IDisposable
{
    private readonly TestDb _db;
    private readonly UsersController _ctrl;
    private User _user = null!;

    public AvatarTests()
    {
        _db = new TestDb();
        _ctrl = new UsersController(_db.Db);
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedAsync()
    {
        (_,  _user) = await TestHelpers.SeedServerAsync(_db.Db);
        TestHelpers.SetUser(_ctrl, _user.Id, _user.Username);
    }

    [Fact]
    public async Task GetProfile_IncludesAvatarUrl()
    {
        await SeedAsync();
        _user.AvatarUrl = "http://localhost/uploads/avatar.png";
        await _db.Db.SaveChangesAsync();

        var result = await _ctrl.GetProfile(_user.Id, null);

        var dto = Assert.IsType<UserProfileDto>(result.Value);
        Assert.Equal("http://localhost/uploads/avatar.png", dto.AvatarUrl);
    }

    [Fact]
    public async Task GetProfile_NullAvatarUrl_WhenNotSet()
    {
        await SeedAsync();

        var result = await _ctrl.GetProfile(_user.Id, null);

        var dto = Assert.IsType<UserProfileDto>(result.Value);
        Assert.Null(dto.AvatarUrl);
    }

    [Fact]
    public async Task UploadAvatar_UpdatesUserAvatarUrl()
    {
        await SeedAsync();

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var envMock = new Mock<IWebHostEnvironment>();
            envMock.Setup(e => e.ContentRootPath).Returns(tempDir);

            var fileContent = new byte[] { 0xFF, 0xD8, 0xFF, 0x00 }; // JPEG magic bytes
            var fileMock = new Mock<IFormFile>();
            fileMock.Setup(f => f.Length).Returns(fileContent.Length);
            fileMock.Setup(f => f.FileName).Returns("photo.jpg");
            fileMock.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(fileContent));
            fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
                .Callback<Stream, CancellationToken>((s, _) => s.Write(fileContent))
                .Returns(Task.CompletedTask);

            _ctrl.ControllerContext.HttpContext.Request.Host = new HostString("localhost");
            _ctrl.ControllerContext.HttpContext.Request.Scheme = "http";

            var result = await _ctrl.UploadAvatar(fileMock.Object, envMock.Object);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var dto = Assert.IsType<AttachmentDto>(ok.Value);
            Assert.Contains("/uploads/", dto.Url);

            var updated = await _db.Db.Users.FindAsync(_user.Id);
            Assert.NotNull(updated!.AvatarUrl);
            Assert.Contains("/uploads/", updated.AvatarUrl);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task UploadAvatar_RejectsBadExtension()
    {
        await SeedAsync();

        var envMock = new Mock<IWebHostEnvironment>();
        envMock.Setup(e => e.ContentRootPath).Returns(Path.GetTempPath());

        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(10);
        fileMock.Setup(f => f.FileName).Returns("virus.exe");

        var result = await _ctrl.UploadAvatar(fileMock.Object, envMock.Object);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetMessages_IncludesAuthorAvatarUrl()
    {
        await SeedAsync();
        _user.AvatarUrl = "http://localhost/uploads/me.png";
        await _db.Db.SaveChangesAsync();

        var server = _db.Db.Servers.First();
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        _db.Db.Messages.Add(new Message { Content = "hello", AuthorId = _user.Id, ChannelId = channel.Id });
        await _db.Db.SaveChangesAsync();

        var msgsCtrl = new MessagesController(_db.Db, TestHelpers.MockHub(), new FatGuysSpeak.Server.Services.ServerMetricsService(), TestHelpers.NullBot(), TestHelpers.NullAutomod(), TestHelpers.NullWebhooks());
        TestHelpers.SetUser(msgsCtrl, _user.Id, _user.Username);

        var result = await msgsCtrl.GetMessages(channel.Id);
        var msgs = result.Value!;

        Assert.Single(msgs);
        Assert.Equal("http://localhost/uploads/me.png", msgs[0].AuthorAvatarUrl);
    }
}
