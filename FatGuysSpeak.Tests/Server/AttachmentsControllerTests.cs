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

    // Magic-number prefixes so image uploads pass server-side content validation.
    private static byte[] ImageMagic(string ext) => ext switch
    {
        ".png"            => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
        ".jpg" or ".jpeg" => new byte[] { 0xFF, 0xD8, 0xFF },
        ".gif"            => System.Text.Encoding.ASCII.GetBytes("GIF89a"),
        ".webp"           => System.Text.Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBP"),
        _                 => Array.Empty<byte>()
    };

    private static IFormFile MakeFile(string filename, string content = "fake image bytes", long? overrideLength = null, string? contentType = null)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        var bytes = ImageMagic(ext).Concat(System.Text.Encoding.UTF8.GetBytes(content)).ToArray();
        long length = overrideLength ?? bytes.Length;
        var ct = contentType ?? ext switch
        {
            ".pdf"  => "application/pdf",
            ".zip"  => "application/zip",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".mp4"  => "video/mp4",
            ".mp3"  => "audio/mpeg",
            _       => "image/" + ext.TrimStart('.')
        };

        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.FileName).Returns(filename);
        fileMock.Setup(f => f.Length).Returns(length);
        fileMock.Setup(f => f.ContentType).Returns(ct);
        fileMock.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(bytes));
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns<Stream, CancellationToken>((dest, ct2) => new MemoryStream(bytes).CopyToAsync(dest, ct2));
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
    [InlineData("file.bmp")]
    [InlineData("hack.sh")]
    [InlineData("data.xml")]
    public async Task Upload_DisallowedExtension_ReturnsBadRequest(string filename)
    {
        var result = await _controller.Upload(MakeFile(filename));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Upload_PngExtensionButNonImageContent_ReturnsBadRequest()
    {
        // A non-image disguised with a .png extension must be rejected by magic-byte validation.
        var fileMock = new Mock<IFormFile>();
        var bytes = System.Text.Encoding.UTF8.GetBytes("MZ this is actually an executable");
        fileMock.Setup(f => f.FileName).Returns("payload.png");
        fileMock.Setup(f => f.Length).Returns(bytes.Length);
        fileMock.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(bytes));
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns<Stream, CancellationToken>((dest, ct) => new MemoryStream(bytes).CopyToAsync(dest, ct));

        var result = await _controller.Upload(fileMock.Object);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        // Rejected before any file is written (uploads dir is only created on success).
        Assert.True(!Directory.Exists(_uploadsDir) || Directory.GetFiles(_uploadsDir).Length == 0);
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

    // ── Non-image file types ──────────────────────────────────────────────────

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("archive.zip")]
    [InlineData("data.csv")]
    [InlineData("notes.docx")]
    [InlineData("sheet.xlsx")]
    [InlineData("video.mp4")]
    [InlineData("audio.mp3")]
    public async Task Upload_AllowedNonImageFile_ReturnsOk(string filename)
    {
        var result = await _controller.Upload(MakeFile(filename));

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Upload_PdfFile_OriginalFileNameReturnedInDto()
    {
        var result = await _controller.Upload(MakeFile("my-report.pdf"));

        var dto = ((OkObjectResult)result.Result!).Value as AttachmentDto;
        Assert.Equal("my-report.pdf", dto!.OriginalFileName);
    }

    [Fact]
    public async Task Upload_ZipFile_ContentTypeReturnedInDto()
    {
        var result = await _controller.Upload(MakeFile("data.zip"));

        // Non-image uploads report a server-determined generic MIME type to prevent
        // client-supplied Content-Type spoofing (the client sends "application/zip").
        var dto = ((OkObjectResult)result.Result!).Value as AttachmentDto;
        Assert.Equal("application/octet-stream", dto!.ContentType);
    }

    [Fact]
    public async Task Upload_NonImageFile_StoredWithUuidName()
    {
        await _controller.Upload(MakeFile("secret.pdf"));

        var files = Directory.GetFiles(_uploadsDir);
        Assert.Single(files);
        // Stored name should be a UUID (32 hex chars) + ".pdf", not the original name
        var storedName = Path.GetFileNameWithoutExtension(files[0]);
        Assert.Equal(32, storedName.Length);
        Assert.All(storedName, c => Assert.True(char.IsAsciiHexDigit(c)));
    }

    [Fact]
    public async Task Upload_OversizedNonImageFile_ReturnsBadRequest()
    {
        // 26 MB exceeds 25 MB limit for non-image files
        var oversized = MakeFile("huge.zip", overrideLength: 26 * 1024 * 1024L);

        var result = await _controller.Upload(oversized);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Upload_NonImageFileBelowLimit_ReturnsOk()
    {
        // 24 MB is under the 25 MB limit for non-image files
        var file = MakeFile("big-but-ok.zip", overrideLength: 24 * 1024 * 1024L);

        var result = await _controller.Upload(file);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Upload_ImageAtImageLimit_ReturnsOk()
    {
        // Exactly 8 MB — right at the image cap
        var file = MakeFile("maximage.png", overrideLength: 8 * 1024 * 1024L);

        var result = await _controller.Upload(file);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Upload_ImageOneByteOverLimit_ReturnsBadRequest()
    {
        var file = MakeFile("toobig.png", overrideLength: 8 * 1024 * 1024L + 1);

        var result = await _controller.Upload(file);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Upload_FileWithPathSeparatorsInName_StrippedSafely()
    {
        // filename with path traversal characters should have them stripped, not stored
        var file = MakeFile("../../etc/passwd.pdf");

        var result = await _controller.Upload(file);

        var dto = ((OkObjectResult)result.Result!).Value as AttachmentDto;
        Assert.DoesNotContain("..", dto!.OriginalFileName ?? "");
    }
}
