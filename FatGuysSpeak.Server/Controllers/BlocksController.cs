using System.Security.Claims;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Route("api/users/me/blocks")]
[Authorize]
public class BlocksController(AppDbContext db) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<List<BlockedUserDto>>> GetBlocked()
    {
        var blocks = await db.UserBlocks
            .Where(b => b.BlockerId == UserId)
            .Include(b => b.Blocked)
            .OrderBy(b => b.CreatedAt)
            .Select(b => new BlockedUserDto(b.BlockedId, b.Blocked.Username, b.CreatedAt))
            .ToListAsync();
        return blocks;
    }

    [HttpPost("{userId}")]
    public async Task<IActionResult> BlockUser(int userId)
    {
        if (userId == UserId) return BadRequest("Cannot block yourself.");
        var target = await db.Users.FindAsync(userId);
        if (target is null) return NotFound();
        // Admins can't be blocked by anyone — not even another admin.
        if (await db.ServerMembers.AnyAsync(m => m.UserId == userId && m.Role == ServerRole.Admin))
            return BadRequest("Admins can't be blocked.");
        if (await db.UserBlocks.AnyAsync(b => b.BlockerId == UserId && b.BlockedId == userId))
            return NoContent(); // already blocked — idempotent
        db.UserBlocks.Add(new UserBlock { BlockerId = UserId, BlockedId = userId });
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{userId}")]
    public async Task<IActionResult> UnblockUser(int userId)
    {
        var block = await db.UserBlocks.FindAsync(UserId, userId);
        if (block is null) return NoContent(); // already unblocked — idempotent
        db.UserBlocks.Remove(block);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
