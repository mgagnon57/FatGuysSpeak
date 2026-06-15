using System.Security.Claims;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Route("api/servers")]
[Authorize]
public class CategoriesController(AppDbContext db, IHubContext<ChatHub> hub) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<bool> IsMemberAsync(int serverId) =>
        await db.ServerMembers.AnyAsync(sm => sm.ServerId == serverId && sm.UserId == UserId);

    private async Task<bool> IsAdminAsync(int serverId)
    {
        var m = await db.ServerMembers.FindAsync(serverId, UserId);
        return m is not null && m.Role >= ServerRole.Admin;
    }

    [HttpGet("{serverId}/categories")]
    public async Task<ActionResult<List<CategoryDto>>> GetCategories(int serverId)
    {
        if (!await IsMemberAsync(serverId)) return Forbid();
        return await db.Categories
            .Where(c => c.ServerId == serverId)
            .OrderBy(c => c.Position)
            .Select(c => new CategoryDto(c.Id, c.ServerId, c.Name, c.Position))
            .ToListAsync();
    }

    [HttpPost("{serverId}/categories")]
    public async Task<ActionResult<CategoryDto>> CreateCategory(int serverId, CreateCategoryRequest req)
    {
        if (!await IsAdminAsync(serverId)) return Forbid();
        var pos = await db.Categories.CountAsync(c => c.ServerId == serverId);
        var cat = new Category { ServerId = serverId, Name = req.Name, Position = pos };
        db.Categories.Add(cat);
        await db.SaveChangesAsync();
        var dto = new CategoryDto(cat.Id, cat.ServerId, cat.Name, cat.Position);
        await hub.Clients.Group($"server-{serverId}").SendAsync("CategoryCreated", dto);
        return Ok(dto);
    }

    [HttpPut("{serverId}/categories/{categoryId}")]
    public async Task<IActionResult> RenameCategory(int serverId, int categoryId, RenameCategoryRequest req)
    {
        if (!await IsAdminAsync(serverId)) return Forbid();
        var cat = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId && c.ServerId == serverId);
        if (cat is null) return NotFound();
        cat.Name = req.Name;
        await db.SaveChangesAsync();
        await hub.Clients.Group($"server-{serverId}").SendAsync("CategoryRenamed", categoryId, req.Name);
        return NoContent();
    }

    [HttpDelete("{serverId}/categories/{categoryId}")]
    public async Task<IActionResult> DeleteCategory(int serverId, int categoryId)
    {
        if (!await IsAdminAsync(serverId)) return Forbid();
        var cat = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId && c.ServerId == serverId);
        if (cat is null) return NotFound();

        var channels = await db.Channels.Where(c => c.CategoryId == categoryId).ToListAsync();
        foreach (var ch in channels) ch.CategoryId = null;

        db.Categories.Remove(cat);
        await db.SaveChangesAsync();
        await hub.Clients.Group($"server-{serverId}").SendAsync("CategoryDeleted", categoryId);
        return NoContent();
    }

    [HttpPut("{serverId}/channels/{channelId}/category")]
    public async Task<IActionResult> SetChannelCategory(int serverId, int channelId, SetChannelCategoryRequest req)
    {
        if (!await IsAdminAsync(serverId)) return Forbid();
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.ServerId == serverId);
        if (channel is null) return NotFound();

        if (req.CategoryId.HasValue)
        {
            if (!await db.Categories.AnyAsync(c => c.Id == req.CategoryId && c.ServerId == serverId))
                return BadRequest("Category not found in this server.");
        }

        channel.CategoryId = req.CategoryId;
        await db.SaveChangesAsync();
        await hub.Clients.Group($"server-{serverId}").SendAsync("ChannelCategoryChanged", channelId, req.CategoryId);
        return NoContent();
    }
}
