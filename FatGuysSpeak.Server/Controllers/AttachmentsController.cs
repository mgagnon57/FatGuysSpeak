using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Route("api/attachments")]
[Authorize]
public class AttachmentsController(IWebHostEnvironment env) : ControllerBase
{
    private const long MaxImageBytes = 8 * 1024 * 1024;   // 8 MB for images
    private const long MaxFileBytes  = 25 * 1024 * 1024;  // 25 MB for other files

    private static readonly HashSet<string> ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    private static readonly HashSet<string> FileExtensions =
        [".pdf", ".txt", ".csv", ".md",
         ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
         ".zip", ".7z", ".rar", ".tar", ".gz",
         ".mp3", ".wav", ".ogg",
         ".mp4", ".mov", ".mkv", ".webm"];

    [HttpPost]
    [RequestSizeLimit(25 * 1024 * 1024 + 1024)]
    public async Task<ActionResult<AttachmentDto>> Upload(IFormFile file)
    {
        if (file is null || file.Length == 0) return BadRequest("No file provided.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        bool isImage = ImageExtensions.Contains(ext);
        bool isFile  = FileExtensions.Contains(ext);

        if (!isImage && !isFile)
            return BadRequest($"File type not allowed. Supported: images (jpg, png, gif, webp) and files (pdf, zip, docx, mp4, and more).");

        long limit = isImage ? MaxImageBytes : MaxFileBytes;
        if (file.Length > limit)
            return BadRequest($"File too large. Maximum is {limit / (1024 * 1024)} MB for {(isImage ? "images" : "this file type")}.");

        // Sanitize the original filename — keep extension, strip path separators
        var originalName = Path.GetFileName(file.FileName);
        var safeName     = string.Concat(originalName.Where(c => c != '/' && c != '\\' && c != '\0'));
        if (safeName.Length > 200) safeName = safeName[^200..]; // cap length

        var storedName = $"{Guid.NewGuid():N}{ext}";
        var uploadsDir = Path.Combine(env.ContentRootPath, "uploads");
        Directory.CreateDirectory(uploadsDir);

        using (var stream = System.IO.File.Create(Path.Combine(uploadsDir, storedName)))
            await file.CopyToAsync(stream);

        var url = $"{Request.Scheme}://{Request.Host}/uploads/{storedName}";
        return Ok(new AttachmentDto(url, safeName, file.ContentType));
    }
}
