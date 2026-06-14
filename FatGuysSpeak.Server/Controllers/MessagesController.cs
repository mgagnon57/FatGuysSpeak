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
public class MessagesController(AppDbContext db, IHubContext<ChatHub> hub, ServerMetricsService metrics, BotService bot) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    private static MessageDto ToDto(Message m, List<ReactionDto>? reactions = null) => new(
        m.Id, m.Content, m.Author.Username, m.AuthorId, m.CreatedAt, m.ChannelId, m.Source,
        m.AttachmentUrl, m.IsDeleted, m.EditedAt,
        reactions?.Count > 0 ? reactions : null,
        m.Author.AvatarUrl,
        m.ReplyToId,
        m.ReplyTo?.Author?.Username,
        m.ReplyToId.HasValue && m.ReplyTo is { IsDeleted: false }
            ? (m.ReplyTo.Content.Length > 100 ? m.ReplyTo.Content[..100] + "…" : m.ReplyTo.Content)
            : null,
        m.AttachmentFileName);

    private async Task<(bool isMember, bool hasReadAccess)> CheckChannelAccessAsync(int channelId)
    {
        var channel = await db.Channels.Include(c => c.Server).ThenInclude(s => s.Members)
            .FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return (false, false);

        var member = channel.Server.Members.FirstOrDefault(m => m.UserId == UserId);
        if (member is null) return (false, false);

        var perm = await db.ChannelPermissions.FindAsync(channelId);
        var hasRead = perm is null || member.Role >= perm.MinRoleToRead;
        return (true, hasRead);
    }

    private async Task<bool> CanWriteToChannelAsync(int channelId, int serverId)
    {
        var perm = await db.ChannelPermissions.FindAsync(channelId);
        if (perm is null || perm.MinRoleToWrite == ServerRole.Member) return true;
        var member = await db.ServerMembers.FindAsync(serverId, UserId);
        return member is not null && member.Role >= perm.MinRoleToWrite;
    }

    [HttpGet]
    public async Task<ActionResult<List<MessageDto>>> GetMessages(int channelId, [FromQuery] int limit = 50, [FromQuery] int? afterId = null)
    {
        var channel = await db.Channels.Include(c => c.Server).ThenInclude(s => s.Members)
            .FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return NotFound();
        if (!channel.Server.Members.Any(m => m.UserId == UserId)) return Forbid();

        var perm = await db.ChannelPermissions.FindAsync(channelId);
        if (perm is not null && perm.MinRoleToRead > ServerRole.Member)
        {
            var m = channel.Server.Members.FirstOrDefault(x => x.UserId == UserId);
            if (m is null || m.Role < perm.MinRoleToRead) return Forbid();
        }

        var query = db.Messages
            .Where(m => m.ChannelId == channelId)
            .Include(m => m.Author)
            .Include(m => m.ReplyTo).ThenInclude(r => r!.Author);

        var messages = afterId.HasValue
            ? await query.Where(m => m.Id > afterId.Value).OrderBy(m => m.CreatedAt).Take(200).ToListAsync()
            : await query.OrderByDescending(m => m.CreatedAt).Take(limit).OrderBy(m => m.CreatedAt).ToListAsync();

        var ids = messages.Select(m => m.Id).ToList();
        var allReactions = await db.MessageReactions
            .Where(r => ids.Contains(r.MessageId))
            .ToListAsync();
        var byMsg = allReactions.GroupBy(r => r.MessageId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return messages.Select(m =>
        {
            if (!byMsg.TryGetValue(m.Id, out var rs)) return ToDto(m);
            var reactions = rs
                .GroupBy(r => r.Emoji)
                .Select(g => new ReactionDto(g.Key, g.Count(), g.Any(r => r.UserId == UserId)))
                .OrderBy(r => r.Emoji)
                .ToList();
            return ToDto(m, reactions);
        }).ToList();
    }

    [HttpPost]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("messages")]
    public async Task<ActionResult<MessageDto>> SendMessage(int channelId, SendMessageRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Content) && req.AttachmentUrl is null)
            return BadRequest("Message must have content or an attachment.");
        if (req.Content is not null && req.Content.Length > 2000)
            return BadRequest("Message content must be 2000 characters or fewer.");
        if (req.AttachmentUrl is not null && !IsValidAttachmentUrl(req.AttachmentUrl))
            return BadRequest("Invalid attachment URL.");

        var channel = await db.Channels.Include(c => c.Server).ThenInclude(s => s.Members)
            .FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return NotFound();
        var senderMember = channel.Server.Members.FirstOrDefault(m => m.UserId == UserId);
        if (senderMember is null) return Forbid();

        var writePerm = await db.ChannelPermissions.FindAsync(channelId);
        if (writePerm is not null && senderMember.Role < writePerm.MinRoleToWrite)
            return Forbid();

        var user = await db.Users.FindAsync(UserId);
        var message = new Message
        {
            Content = req.Content?.Trim() ?? "",
            AuthorId = UserId,
            ChannelId = channelId,
            Source = req.Source,
            AttachmentUrl = req.AttachmentUrl,
            AttachmentFileName = req.AttachmentFileName,
            ReplyToId = req.ReplyToMessageId
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync();

        message.Author = user!;
        if (req.ReplyToMessageId.HasValue)
            message.ReplyTo = await db.Messages.Include(m => m.Author)
                .FirstOrDefaultAsync(m => m.Id == req.ReplyToMessageId.Value);
        metrics.RecordMessage();
        var dto = ToDto(message);
        await hub.Clients.Group($"channel-{channelId}").SendAsync("ReceiveMessage", dto);
        // Broadcast to the server group so all connected clients can update unread badges
        // regardless of which channel they are currently viewing.
        await hub.Clients.Group($"server-{channel.ServerId}").SendAsync("NewMessageNotification", dto);

        if (message.Content.Contains($"@{BotService.BotUsername}", StringComparison.OrdinalIgnoreCase))
            _ = bot.RespondAsync(channelId, channel.ServerId, message.Content);

        return Ok(dto);
    }

    private static bool IsValidAttachmentUrl(string url)
    {
        if (url.Contains("/uploads/", StringComparison.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && uri.Host.EndsWith(".giphy.com", StringComparison.OrdinalIgnoreCase);
    }

    [HttpGet("search")]
    public async Task<ActionResult<List<MessageDto>>> SearchMessages(int channelId, [FromQuery] string q, [FromQuery] int limit = 25)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return BadRequest("Search query must be at least 2 characters.");

        var channel = await db.Channels.Include(c => c.Server).ThenInclude(s => s.Members)
            .FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return NotFound();
        if (!channel.Server.Members.Any(m => m.UserId == UserId)) return Forbid();

        var ql = q.ToLower();
        var messages = await db.Messages
            .Where(m => m.ChannelId == channelId && !m.IsDeleted && m.Content.ToLower().Contains(ql))
            .Include(m => m.Author)
            .Include(m => m.ReplyTo).ThenInclude(r => r!.Author)
            .OrderByDescending(m => m.CreatedAt)
            .Take(Math.Min(limit, 50))
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        var ids = messages.Select(m => m.Id).ToList();
        var allReactions = await db.MessageReactions
            .Where(r => ids.Contains(r.MessageId))
            .ToListAsync();
        var byMsg = allReactions.GroupBy(r => r.MessageId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return messages.Select(m =>
        {
            if (!byMsg.TryGetValue(m.Id, out var rs)) return ToDto(m);
            var reactions = rs
                .GroupBy(r => r.Emoji)
                .Select(g => new ReactionDto(g.Key, g.Count(), g.Any(r => r.UserId == UserId)))
                .OrderBy(r => r.Emoji).ToList();
            return ToDto(m, reactions);
        }).ToList();
    }

    [HttpPut("{messageId}")]
    public async Task<ActionResult<MessageDto>> EditMessage(int channelId, int messageId, EditMessageRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Content) || req.Content.Length > 2000)
            return BadRequest("Message must be 1–2000 characters.");

        var message = await db.Messages.Include(m => m.Author)
            .Include(m => m.ReplyTo).ThenInclude(r => r!.Author)
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
            .Include(m => m.Channel).ThenInclude(c => c.Server)
            .Include(m => m.Author)
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ChannelId == channelId);
        if (message is null) return NotFound();

        if (message.AuthorId != UserId)
        {
            var actorMember = await db.ServerMembers.FindAsync(message.Channel.ServerId, UserId);
            if (actorMember is null || actorMember.Role < ServerRole.Moderator) return Forbid();

            db.AuditLogs.Add(new AuditLog
            {
                ServerId = message.Channel.ServerId,
                ActorId = UserId,
                ActorUsername = User.FindFirstValue(ClaimTypes.Name)!,
                Action = "MessageDeleted",
                TargetId = message.AuthorId,
                TargetUsername = message.Author.Username,
                Detail = $"Msg #{messageId} in #{message.Channel.Name}: {message.Content[..Math.Min(80, message.Content.Length)]}"
            });
        }

        message.IsDeleted = true;
        await db.SaveChangesAsync();

        await hub.Clients.Group($"channel-{channelId}").SendAsync("MessageDeleted", messageId, channelId);
        return NoContent();
    }
}
