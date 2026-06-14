using System.Security.Claims;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
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
public class MessagesController(AppDbContext db, IHubContext<ChatHub> hub, ServerMetricsService metrics) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    private MessageDto ToDto(Message m) => new(
        m.Id, m.Content, m.Author.Username, m.AuthorId, m.CreatedAt, m.ChannelId, m.Source,
        m.AttachmentUrl,  // already stored as full URL
        m.IsDeleted, m.EditedAt);

    [HttpGet]
    public async Task<ActionResult<List<MessageDto>>> GetMessages(int channelId, [FromQuery] int limit = 50)
    {
        var channel = await db.Channels.Include(c => c.Server).ThenInclude(s => s.Members)
            .FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return NotFound();
        if (!channel.Server.Members.Any(m => m.UserId == UserId)) return Forbid();

        var messages = await db.Messages
            .Where(m => m.ChannelId == channelId)
            .Include(m => m.Author)
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        return messages.Select(ToDto).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<MessageDto>> SendMessage(int channelId, SendMessageRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Content) && req.AttachmentUrl is null)
            return BadRequest("Message must have content or an attachment.");
        if (req.Content is not null && req.Content.Length > 2000)
            return BadRequest("Message content must be 2000 characters or fewer.");
        if (req.AttachmentUrl is not null && !req.AttachmentUrl.Contains("/uploads/", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Invalid attachment URL.");

        var channel = await db.Channels.Include(c => c.Server).ThenInclude(s => s.Members)
            .FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return NotFound();
        if (!channel.Server.Members.Any(m => m.UserId == UserId)) return Forbid();

        var user = await db.Users.FindAsync(UserId);
        var message = new Message
        {
            Content = req.Content?.Trim() ?? "",
            AuthorId = UserId,
            ChannelId = channelId,
            Source = req.Source,
            AttachmentUrl = req.AttachmentUrl
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync();

        message.Author = user!;
        metrics.RecordMessage();
        var dto = ToDto(message);
        await hub.Clients.Group($"channel-{channelId}").SendAsync("ReceiveMessage", dto);
        // Broadcast to the server group so all connected clients can update unread badges
        // regardless of which channel they are currently viewing.
        await hub.Clients.Group($"server-{channel.ServerId}").SendAsync("NewMessageNotification", dto);

        return Ok(dto);
    }

    [HttpPut("{messageId}")]
    public async Task<ActionResult<MessageDto>> EditMessage(int channelId, int messageId, EditMessageRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Content) || req.Content.Length > 2000)
            return BadRequest("Message must be 1–2000 characters.");

        var message = await db.Messages.Include(m => m.Author)
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ChannelId == channelId);
        if (message is null) return NotFound();
        if (message.AuthorId != UserId) return Forbid();
        if (message.IsDeleted) return BadRequest("Cannot edit a deleted message.");

        message.Content = req.Content.Trim();
        message.EditedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var dto = ToDto(message);
        await hub.Clients.Group($"channel-{channelId}").SendAsync("MessageEdited", dto);

        return Ok(dto);
    }

    [HttpDelete("{messageId}")]
    public async Task<ActionResult> DeleteMessage(int channelId, int messageId)
    {
        var message = await db.Messages
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ChannelId == channelId);
        if (message is null) return NotFound();
        if (message.AuthorId != UserId) return Forbid();

        message.IsDeleted = true;
        await db.SaveChangesAsync();

        await hub.Clients.Group($"channel-{channelId}").SendAsync("MessageDeleted", messageId, channelId);

        return NoContent();
    }
}
