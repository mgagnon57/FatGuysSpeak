using System.Security.Claims;
using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace FatGuysSpeak.Tests.Server;

public class AttachmentsControllerTests : IDisposable
{
    private readonly string _contentRoot;
    private readonly string _uploadsDir;
    private readonly AttachmentsController _controller;

    public AttachmentsControllerTests()
    {
        // Controller writes to {contentRootPath}/uploads so give it a temp dir
        _contentRoot = Path.Combine(Path.GetTempPath(), $"fgs_att_{Guid.NewGuid():N}");
        _uploadsDir = Path.Combine(_contentRoot, "uploads");
        Directory.CreateDirectory(_contentRoot);

        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.ContentRootPath).Returns(_contentRoot);

        _controller = new AttachmentsController(env.Object);
        SetupHttpContext(_controller);
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot))
            Directory.Delete(_contentRoot, recursive: true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void SetupHttpContext(ControllerBase ctrl, string scheme = "https", string host = "localhost:5238")
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ClaimTypes.Name, "testuser")
        };
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
        };
        httpContext.Request.Scheme = scheme;
        httpContext.Request.Host = new HostString(host);
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private static IFormFile MakeFile(string filename, string content = "fake image bytes", long? overrideLength = null)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var stream = new MemoryStream(bytes);
        long length = overrideLength ?? bytes.Length;

        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.FileName).Returns(filename);
        fileMock.Setup(f => f.Length).Returns(length);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, CancellationToken>((dest, _) => stream.CopyTo(dest))
            .Returns(Task.CompletedTask);
        return fileMock.Object;
    }

    // ── Success cases ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_ValidPng_ReturnsOkWithUrl()
    {
        var result = await _controller.Upload(MakeFile("photo.png"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<AttachmentDto>(ok.Value);
        Assert.False(string.IsNullOrWhiteSpace(dto.Url));
    }

    [Fact]
    public async Task Upload_ValidPng_UrlContainsUploadsPathSegment()
    {
        var result = await _controller.Upload(MakeFile("photo.png"));

        var dto = ((OkObjectResult)result.Result!).Value as AttachmentDto;
        Assert.Contains("/uploads/", dto!.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upload_ValidPng_UrlContainsSchemeAndHost()
    {
        var result = await _controller.Upload(MakeFile("photo.png"));

        var dto = ((OkObjectResult)result.Result!).Value as AttachmentDto;
        Assert.StartsWith("https://localhost:5238", dto!.Url);
    }

    [Fact]
    public async Task Upload_ValidPng_FileIsSavedToUploadsDirectory()
    {
        await _controller.Upload(MakeFile("saved.png"));

        var files = Directory.GetFiles(_uploadsDir);
        Assert.Single(files);
        Assert.EndsWith(".png", files[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upload_TwoFiles_BothGetUniqueNames()
    {
        await _controller.Upload(MakeFile("a.jpg"));
        await _controller.Upload(MakeFile("b.jpg"));

        var files = Directory.GetFiles(_uploadsDir);
        Assert.Equal(2, files.Length);
        Assert.NotEqual(Path.GetFileName(files[0]), Path.GetFileName(files[1]));
    }

    [Theory]
    [InlineData("image.jpg")]
    [InlineData("image.jpeg")]
    [InlineData("image.png")]
    [InlineData("image.gif")]
    [InlineData("image.webp")]
    public async Task Upload_AllowedExtension_ReturnsOk(string filename)
    {
        var result = await _controller.Upload(MakeFile(filename));

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Upload_CreatesUploadsDirIfMissing()
    {
        // Verify the controller auto-creates the uploads subdirectory
        Assert.False(Directory.Exists(_uploadsDir));

        await _controller.Upload(MakeFile("create.png"));

        Assert.True(Directory.Exists(_uploadsDir));
    }

    // ── Validation rejections ─────────────────────────────────────────────────

    [Fact]
    public async Task Upload_NullFile_ReturnsBadRequest()
    {
        var result = await _controller.Upload(null!);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Upload_ZeroByteFile_ReturnsBadRequest()
    {
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.FileName).Returns("empty.png");
        fileMock.Setup(f => f.Length).Returns(0L);

        var result = await _controller.Upload(fileMock.Object);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Upload_FileTooLarge_ReturnsBadRequest()
    {
        var oversized = MakeFile("huge.png", overrideLength: 9 * 1024 * 1024L); // 9 MB > 8 MB

        var result = await _controller.Upload(oversized);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Theory]
    [InlineData("virus.exe")]
    [InlineData("script.js")]
    [InlineData("archive.zip")]
    [InlineData("document.pdf")]
    [InlineData("file.bmp")]
    public async Task Upload_DisallowedExtension_ReturnsBadRequest(string filename)
    {
        var result = await _controller.Upload(MakeFile(filename));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Upload_FileTooLarge_DoesNotWriteAnythingToUploadsDir()
    {
        var oversized = MakeFile("huge.jpg", overrideLength: 9 * 1024 * 1024L);
        Directory.CreateDirectory(_uploadsDir);

        await _controller.Upload(oversized);

        Assert.Empty(Directory.GetFiles(_uploadsDir));
    }

    [Fact]
    public async Task Upload_DisallowedExtension_DoesNotWriteAnythingToUploadsDir()
    {
        Directory.CreateDirectory(_uploadsDir);

        await _controller.Upload(MakeFile("hack.exe"));

        Assert.Empty(Directory.GetFiles(_uploadsDir));
    }
}
