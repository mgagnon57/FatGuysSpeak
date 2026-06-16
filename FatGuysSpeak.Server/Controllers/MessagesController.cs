using System.Security.Claims;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using FatGuysSpeak.Server.Hubs;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Route("api/channels/{channelId}/messages")]
[Authorize]
public class MessagesController(AppDbContext db, IHubContext<ChatHub> hub, ServerMetricsService metrics, BotService bot, AutomodService automod, WebhookDeliveryService webhooks) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    private static MessageDto ToDto(Message m, List<ReactionDto>? reactions = null, bool isPinned = false, int replyCount = 0) => new(
        m.Id, m.Content, m.Author.Username, m.AuthorId, m.CreatedAt, m.ChannelId, m.Source,
        m.AttachmentUrl, m.IsDeleted, m.EditedAt,
        reactions?.Count > 0 ? reactions : null,
        m.Author.AvatarUrl,
        m.ReplyToId,
        m.ReplyTo?.Author?.Username,
        m.ReplyToId.HasValue && m.ReplyTo is { IsDeleted: false }
            ? (m.ReplyTo.Content.Length > 100 ? m.ReplyTo.Content[..100] + "…" : m.ReplyTo.Content)
            : null,
        m.AttachmentFileName,
        isPinned,
        m.ThreadId,
        replyCount);

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
            .Where(m => m.ChannelId == channelId && m.ThreadId == null && !m.IsDeleted)
            .Include(m => m.Author)
            .Include(m => m.ReplyTo).ThenInclude(r => r!.Author);

        var messages = afterId.HasValue
            ? await query.Where(m => m.Id > afterId.Value).OrderBy(m => m.CreatedAt).Take(200).ToListAsync()
            : await query.OrderByDescending(m => m.CreatedAt).Take(Math.Min(limit, 200)).OrderBy(m => m.CreatedAt).ToListAsync();

        var ids = messages.Select(m => m.Id).ToList();
        var allReactions = await db.MessageReactions
            .Where(r => ids.Contains(r.MessageId))
            .ToListAsync();
        var byMsg = allReactions.GroupBy(r => r.MessageId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var pinnedIds = (await db.PinnedMessages
            .Where(p => p.ChannelId == channelId && ids.Contains(p.MessageId))
            .Select(p => p.MessageId)
            .ToListAsync()).ToHashSet();
        var replyCounts = await db.Messages
            .Where(m => m.ThreadId.HasValue && ids.Contains(m.ThreadId.Value))
            .GroupBy(m => m.ThreadId!.Value)
            .Select(g => new { RootId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RootId, x => x.Count);

        return messages.Select(m =>
        {
            var pinned = pinnedIds.Contains(m.Id);
            replyCounts.TryGetValue(m.Id, out var rc);
            if (!byMsg.TryGetValue(m.Id, out var rs)) return ToDto(m, isPinned: pinned, replyCount: rc);
            var reactions = rs
                .GroupBy(r => r.Emoji)
                .Select(g => new ReactionDto(g.Key, g.Count(), g.Any(r => r.UserId == UserId)))
                .OrderBy(r => r.Emoji)
                .ToList();
            return ToDto(m, reactions, pinned, rc);
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

        if (senderMember.MutedUntil.HasValue && senderMember.MutedUntil > DateTime.UtcNow)
        {
            var until = senderMember.MutedUntil.Value.ToLocalTime().ToString("HH:mm");
            return StatusCode(403, $"You are muted until {until}.");
        }

        if (channel.SlowmodeSeconds > 0 && senderMember.Role < ServerRole.Moderator)
        {
            var lastMsg = await db.Messages
                .Where(m => m.ChannelId == channelId && m.AuthorId == UserId)
                .OrderByDescending(m => m.CreatedAt)
                .FirstOrDefaultAsync();
            if (lastMsg is not null)
            {
                var elapsed = (DateTime.UtcNow - lastMsg.CreatedAt).TotalSeconds;
                if (elapsed < channel.SlowmodeSeconds)
                {
                    var remaining = (int)Math.Ceiling(channel.SlowmodeSeconds - elapsed);
                    return StatusCode(429, $"Slowmode active. Wait {remaining} more second{(remaining == 1 ? "" : "s")}.");
                }
            }
        }

        if (req.ThreadId.HasValue)
        {
            var root = await db.Messages.FindAsync(req.ThreadId.Value);
            if (root is null || root.ChannelId != channelId) return BadRequest("Invalid thread root.");
        }

        // Reply target must live in the same channel — otherwise the reply preview would
        // leak the first 100 chars of a message from a channel the sender can't read.
        if (req.ReplyToMessageId.HasValue)
        {
            var replyTarget = await db.Messages.FindAsync(req.ReplyToMessageId.Value);
            if (replyTarget is null || replyTarget.ChannelId != channelId)
                return BadRequest("Invalid reply target.");
        }

        var user = await db.Users.FindAsync(UserId);

        var filteredContent = req.Content?.Trim() ?? "";

        if (senderMember.Role < ServerRole.Moderator && !string.IsNullOrEmpty(filteredContent))
        {
            var filters = await db.WordFilters.Where(f => f.ServerId == channel.ServerId).ToListAsync();
            if (filters.Count > 0)
            {
                var wfResult = WordFiltersController.Apply(filteredContent, filters);
                if (wfResult.MaxSeverity == WordFilterSeverity.Mute)
                {
                    senderMember.MutedUntil = DateTime.UtcNow.AddMinutes(10);
                    await db.SaveChangesAsync();
                    return StatusCode(403, "Your message was blocked by word filter. You have been temporarily muted.");
                }
                if (wfResult.MaxSeverity == WordFilterSeverity.Delete)
                    return StatusCode(403, "Your message was blocked by word filter.");
                filteredContent = wfResult.FilteredContent;
            }
        }

        if (filteredContent.Contains("@everyone", StringComparison.OrdinalIgnoreCase))
        {
            var server = await db.Servers.FindAsync(channel.ServerId);
            if (server is not null && senderMember.Role < server.MinRoleToMentionEveryone)
                return StatusCode(403, "You don't have permission to mention @everyone in this server.");
        }

        if (senderMember.Role < ServerRole.Moderator && automod.IsSpam(UserId, channelId, filteredContent))
            return StatusCode(429, "Message blocked: slow down or avoid sending duplicate messages.");

        var message = new Message
        {
            Content = filteredContent,
            AuthorId = UserId,
            ChannelId = channelId,
            Source = req.Source,
            AttachmentUrl = req.AttachmentUrl,
            AttachmentFileName = req.AttachmentFileName,
            ReplyToId = req.ReplyToMessageId,
            ThreadId = req.ThreadId
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync();

        message.Author = user!;
        if (req.ReplyToMessageId.HasValue)
            message.ReplyTo = await db.Messages.Include(m => m.Author)
                .FirstOrDefaultAsync(m => m.Id == req.ReplyToMessageId.Value);
        metrics.RecordMessage();
        var dto = ToDto(message);

        if (req.ThreadId.HasValue)
        {
            var newCount = await db.Messages.CountAsync(m => m.ThreadId == req.ThreadId.Value);
            await hub.Clients.Group($"channel-{channelId}")
                .SendAsync("ThreadReplyReceived", dto, req.ThreadId.Value, newCount);
        }
        else
        {
            await hub.Clients.Group($"channel-{channelId}").SendAsync("ReceiveMessage", dto);
            await hub.Clients.Group($"server-{channel.ServerId}").SendAsync("NewMessageNotification", dto);
        }

        if (message.Content.Contains($"@{BotService.BotUsername}", StringComparison.OrdinalIgnoreCase))
            _ = bot.RespondAsync(channelId, channel.ServerId, message.Content);

        _ = DeliverMessageWebhooksAsync(channel.ServerId, channelId, message.Id, user!.Username, filteredContent);

        return Ok(dto);
    }

    [HttpGet("{messageId}/thread")]
    public async Task<ActionResult<List<MessageDto>>> GetThreadMessages(int channelId, int messageId)
    {
        var (isMember, hasRead) = await CheckChannelAccessAsync(channelId);
        if (!isMember) return Forbid();
        if (!hasRead) return Forbid();

        var root = await db.Messages.Include(m => m.Author).FirstOrDefaultAsync(m => m.Id == messageId);
        if (root is null || root.ChannelId != channelId) return NotFound();

        var replies = await db.Messages
            .Where(m => m.ThreadId == messageId)
            .Include(m => m.Author)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        var all = new List<MessageDto> { ToDto(root) };
        all.AddRange(replies.Select(m => ToDto(m)));
        return all;
    }

    private static bool IsValidAttachmentUrl(string url)
    {
        if (url.Contains("/uploads/", StringComparison.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && uri.Host.EndsWith(".giphy.com", StringComparison.OrdinalIgnoreCase);
    }

    [EnableRateLimiting("messages")]
    [HttpGet("search")]
    public async Task<ActionResult<List<MessageDto>>> SearchMessages(int channelId, [FromQuery] string q, [FromQuery] int limit = 25)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return BadRequest("Search query must be at least 2 characters.");

        var channel = await db.Channels.Include(c => c.Server).ThenInclude(s => s.Members)
            .FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return NotFound();
        if (!channel.Server.Members.Any(m => m.UserId == UserId)) return Forbid();

        // Enforce the same read permission as GetMessages so search can't bypass it.
        var perm = await db.ChannelPermissions.FindAsync(channelId);
        if (perm is not null && perm.MinRoleToRead > ServerRole.Member)
        {
            var m = channel.Server.Members.FirstOrDefault(x => x.UserId == UserId);
            if (m is null || m.Role < perm.MinRoleToRead) return Forbid();
        }

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

        var content = req.Content.Trim();
        var channel = await db.Channels.FindAsync(channelId);
        if (channel is not null)
        {
            var member = await db.ServerMembers.FindAsync(channel.ServerId, UserId);
            if (member is not null && member.Role < ServerRole.Moderator)
            {
                var filters = await db.WordFilters.Where(f => f.ServerId == channel.ServerId).ToListAsync();
                if (filters.Count > 0)
                {
                    var wfResult = WordFiltersController.Apply(content, filters);
                    if (wfResult.MaxSeverity == WordFilterSeverity.Mute)
                    {
                        member.MutedUntil = DateTime.UtcNow.AddMinutes(10);
                        await db.SaveChangesAsync();
                        return StatusCode(403, "Your message was blocked by word filter. You have been temporarily muted.");
                    }
                    if (wfResult.MaxSeverity == WordFilterSeverity.Delete)
                        return StatusCode(403, "Your message was blocked by word filter.");
                    content = wfResult.FilteredContent;
                }
            }
        }

        message.Content = content;
        message.EditedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var dto = ToDto(message);
        await hub.Clients.Group($"channel-{channelId}").SendAsync("MessageEdited", dto);

        return Ok(dto);
    }

    private async Task DeliverMessageWebhooksAsync(int serverId, int channelId, int messageId, string authorUsername, string content)
    {
        var serverWebhooks = await db.Webhooks.Where(w => w.ServerId == serverId).ToListAsync();
        foreach (var wh in serverWebhooks)
        {
            if (!wh.Events.Contains("message.created")) continue;
            _ = webhooks.DeliverAsync(wh.Url, "message.created", new
            {
                messageId,
                channelId,
                serverId,
                authorUsername,
                content = content.Length > 200 ? content[..200] : content
            });
        }
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
            if (actorMember is null || actorMember.Role < ServerRole.Admin) return Forbid();

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
