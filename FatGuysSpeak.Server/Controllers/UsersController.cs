using System.Security.Claims;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
public class UsersController(AppDbContext db) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static readonly string[] AllowedImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    [HttpGet("{userId}/profile")]
    public async Task<ActionResult<UserProfileDto>> GetProfile(int userId, [FromQuery] int? serverId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null) return NotFound();

        ServerRole? role = null;
        DateTime? joinedAt = null;
        if (serverId.HasValue)
        {
            var member = await db.ServerMembers
                .FirstOrDefaultAsync(m => m.ServerId == serverId.Value && m.UserId == userId);
            if (member is not null)
            {
                role = member.Role;
                joinedAt = member.JoinedAt;
            }
        }

        return new UserProfileDto(user.Id, user.Username, user.Status, user.CreatedAt, role, joinedAt, user.Id == UserId, user.AvatarUrl, user.Bio, user.LastSeenAt);
    }

    [HttpPost("me/avatar")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("messages")]
    [RequestSizeLimit(8 * 1024 * 1024 + 1024)]
    public async Task<ActionResult<AttachmentDto>> UploadAvatar(IFormFile file, [FromServices] IWebHostEnvironment env)
    {
        if (file is null || file.Length == 0) return BadRequest("No file provided.");
        if (file.Length > 8 * 1024 * 1024) return BadRequest("File too large. Maximum is 8 MB.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedImageExtensions.Contains(ext))
            return BadRequest($"File type not allowed. Allowed: {string.Join(", ", AllowedImageExtensions)}");

        // Verify the bytes actually match the claimed image type, not just the extension.
        if (!await FatGuysSpeak.Server.Services.ImageValidation.IsValidImageAsync(file, ext))
            return BadRequest("File contents do not match a valid image of that type.");

        var filename = $"avatar_{UserId}_{Guid.NewGuid():N}{ext}";
        var uploadsDir = Path.Combine(env.ContentRootPath, "uploads");
        Directory.CreateDirectory(uploadsDir);

        using (var stream = System.IO.File.Create(Path.Combine(uploadsDir, filename)))
            await file.CopyToAsync(stream);

        var url = $"{Request.Scheme}://{Request.Host}/uploads/{filename}";
        var user = await db.Users.FindAsync(UserId);
        if (user is null) return NotFound();
        user.AvatarUrl = url;
        await db.SaveChangesAsync();

        return Ok(new AttachmentDto(url));
    }

    [HttpPut("me/bio")]
    public async Task<IActionResult> UpdateBio(UpdateBioRequest req)
    {
        if (req.Bio is not null && req.Bio.Length > 300)
            return BadRequest("Bio must be 300 characters or fewer.");
        var user = await db.Users.FindAsync(UserId);
        if (user is null) return NotFound();
        user.Bio = req.Bio?.Trim().Length == 0 ? null : req.Bio?.Trim();
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("me/username")]
    public async Task<IActionResult> UpdateUsername(UpdateUsernameRequest req)
    {
        var name = req.Username?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name) || name.Length > 32)
            return BadRequest("Username must be 1–32 characters.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9._\-]+$"))
            return BadRequest("Username may only contain letters, digits, underscores, hyphens, and periods.");

        var user = await db.Users.FindAsync(UserId);
        if (user is null) return NotFound();
        if (name != user.Username && await db.Users.AnyAsync(u => u.Username == name))
            return Conflict("Username already taken.");

        user.Username = name;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("me/status")]
    public async Task<IActionResult> UpdateStatus(UpdateStatusRequest req, [FromServices] IHubContext<ChatHub> hub)
    {
        var user = await db.Users.FindAsync(UserId);
        if (user is null) return NotFound();
        user.Status = req.Status;
        await db.SaveChangesAsync();

        var serverIds = await db.ServerMembers
            .Where(m => m.UserId == UserId)
            .Select(m => m.ServerId)
            .ToListAsync();

        foreach (var sid in serverIds)
            await hub.Clients.Group($"server-{sid}").SendAsync("UserStatusChanged", UserId, req.Status);

        return NoContent();
    }
}
