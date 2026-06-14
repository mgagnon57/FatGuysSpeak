using System.Security.Claims;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using FatGuysSpeak.Server.Hubs;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Route("api/channels/{channelId}/messages")]
[Authorize]
public class MessagesController(AppDbContext db, IHubContext<ChatHub> hub) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<List<MessageDto>>> GetMessages(int channelId, [FromQuery] int limit = 50)
    {
        var channel = await db.Channels.Include(c => c.Server).ThenInclude(s => s.Members)
            .FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return NotFound();
        if (!channel.Server.Members.Any(m => m.UserId == UserId)) return Forbid();

        return await db.Messages
            .Where(m => m.ChannelId == channelId)
            .Include(m => m.Author)
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new MessageDto(m.Id, m.Content, m.Author.Username, m.AuthorId, m.CreatedAt, m.ChannelId, m.Source))
            .ToListAsync();
    }

    [HttpPost]
    public async Task<ActionResult<MessageDto>> SendMessage(int channelId, SendMessageRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Content) || req.Content.Length > 2000)
            return BadRequest("Message must be between 1 and 2000 characters.");

        var channel = await db.Channels.Include(c => c.Server).ThenInclude(s => s.Members)
            .FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return NotFound();
        if (!channel.Server.Members.Any(m => m.UserId == UserId)) return Forbid();

        var user = await db.Users.FindAsync(UserId);
        var message = new Message { Content = req.Content, AuthorId = UserId, ChannelId = channelId, Source = req.Source };
        db.Messages.Add(message);
        await db.SaveChangesAsync();

        var dto = new MessageDto(message.Id, message.Content, user!.Username, UserId, message.CreatedAt, channelId, message.Source);
        await hub.Clients.Group($"channel-{channelId}").SendAsync("ReceiveMessage", dto);

        return Ok(dto);
    }
}
