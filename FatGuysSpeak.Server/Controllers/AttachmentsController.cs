using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Route("api/attachments")]
[Authorize]
public class AttachmentsController(IWebHostEnvironment env) : ControllerBase
{
    private const long MaxFileBytes = 8 * 1024 * 1024; // 8 MB
    private static readonly string[] AllowedExtensions = [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    [HttpPost]
    [RequestSizeLimit(8 * 1024 * 1024 + 1024)]
    public async Task<ActionResult<AttachmentDto>> Upload(IFormFile file)
    {
        if (file is null || file.Length == 0) return BadRequest("No file provided.");
        if (file.Length > MaxFileBytes) return BadRequest("File too large. Maximum is 8 MB.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            return BadRequest($"File type not allowed. Allowed: {string.Join(", ", AllowedExtensions)}");

        var filename = $"{Guid.NewGuid():N}{ext}";
        var uploadsDir = Path.Combine(env.ContentRootPath, "uploads");
        Directory.CreateDirectory(uploadsDir);

        using (var stream = System.IO.File.Create(Path.Combine(uploadsDir, filename)))
            await file.CopyToAsync(stream);

        var url = $"{Request.Scheme}://{Request.Host}/uploads/{filename}";
        return Ok(new AttachmentDto(url));
    }
}
