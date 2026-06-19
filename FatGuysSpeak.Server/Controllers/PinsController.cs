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
[Authorize]
public class PinsController(AppDbContext db, IHubContext<ChatHub> hub) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // ── Channel pins ──────────────────────────────────────────────────────────

    [HttpGet("api/channels/{channelId}/pins")]
    public async Task<ActionResult<List<MessageDto>>> GetChannelPins(int channelId)
    {
        if (!await IsMemberOfChannelAsync(channelId)) return Forbid();

        var pins = await db.PinnedMessages
            .Where(p => p.ChannelId == channelId && !p.Message.IsDeleted)
            .Include(p => p.Message).ThenInclude(m => m.Author)
            .Include(p => p.Message).ThenInclude(m => m.ReplyTo).ThenInclude(r => r!.Author)
            .OrderByDescending(p => p.PinnedAt)
            .ToListAsync();

        return Ok(pins.Select(p => ToChannelDto(p.Message, true)).ToList());
    }

    [HttpPost("api/channels/{channelId}/messages/{messageId}/pin")]
    public async Task<ActionResult> PinChannelMessage(int channelId, int messageId)
    {
        if (!await IsAdminOfChannelAsync(channelId)) return Forbid();

        var message = await db.Messages.FindAsync(messageId);
        if (message is null || message.ChannelId != channelId) return NotFound();
        if (await db.PinnedMessages.AnyAsync(p => p.MessageId == messageId))
            return Ok();

        db.PinnedMessages.Add(new PinnedMessage
            { MessageId = messageId, ChannelId = channelId, PinnedById = UserId });
        await db.SaveChangesAsync();

        await hub.Clients.Group($"channel-{channelId}")
            .SendAsync("MessagePinned", messageId, channelId);

        return Ok();
    }

    [HttpDelete("api/channels/{channelId}/messages/{messageId}/pin")]
    public async Task<ActionResult> UnpinChannelMessage(int channelId, int messageId)
    {
        if (!await IsAdminOfChannelAsync(channelId)) return Forbid();

        var pin = await db.PinnedMessages
            .FirstOrDefaultAsync(p => p.MessageId == messageId && p.ChannelId == channelId);
        if (pin is null) return NotFound();

        db.PinnedMessages.Remove(pin);
        await db.SaveChangesAsync();

        await hub.Clients.Group($"channel-{channelId}")
            .SendAsync("MessageUnpinned", messageId, channelId);

        return NoContent();
    }

    // ── DM pins ───────────────────────────────────────────────────────────────

    [HttpGet("api/dm/{conversationId}/pins")]
    public async Task<ActionResult<List<DirectMessageDto>>> GetDmPins(int conversationId)
    {
        if (!await IsParticipantAsync(conversationId)) return Forbid();

        var pins = await db.PinnedDirectMessages
            .Where(p => p.ConversationId == conversationId)
            .Include(p => p.DirectMessage).ThenInclude(m => m.Author)
            .OrderByDescending(p => p.PinnedAt)
            .ToListAsync();

        return Ok(pins.Select(p => ToDmDto(p.DirectMessage, true)).ToList());
    }

    [HttpPost("api/dm/{conversationId}/messages/{messageId}/pin")]
    public async Task<ActionResult> PinDmMessage(int conversationId, int messageId)
    {
        if (!await IsParticipantAsync(conversationId)) return Forbid();

        var msg = await db.DirectMessages.FindAsync(messageId);
        if (msg is null || msg.ConversationId != conversationId) return NotFound();
        if (await db.PinnedDirectMessages.AnyAsync(p => p.DirectMessageId == messageId))
            return Ok();

        db.PinnedDirectMessages.Add(new PinnedDirectMessage
            { DirectMessageId = messageId, ConversationId = conversationId, PinnedById = UserId });
        await db.SaveChangesAsync();

        var convo = await db.DirectConversations.FindAsync(conversationId);
        if (convo is not null)
        {
            int recipientId = convo.User1Id == UserId ? convo.User2Id : convo.User1Id;
            await hub.Clients.Group($"user-{recipientId}").SendAsync("DmMessagePinned", messageId, conversationId);
            await hub.Clients.Group($"user-{UserId}").SendAsync("DmMessagePinned", messageId, conversationId);
        }

        return Ok();
    }

    [HttpDelete("api/dm/{conversationId}/messages/{messageId}/pin")]
    public async Task<ActionResult> UnpinDmMessage(int conversationId, int messageId)
    {
        if (!await IsParticipantAsync(conversationId)) return Forbid();

        var pin = await db.PinnedDirectMessages
            .FirstOrDefaultAsync(p => p.DirectMessageId == messageId && p.ConversationId == conversationId);
        if (pin is null) return NotFound();

        db.PinnedDirectMessages.Remove(pin);
        await db.SaveChangesAsync();

        var convo = await db.DirectConversations.FindAsync(conversationId);
        if (convo is not null)
        {
            int recipientId = convo.User1Id == UserId ? convo.User2Id : convo.User1Id;
            await hub.Clients.Group($"user-{recipientId}").SendAsync("DmMessageUnpinned", messageId, conversationId);
            await hub.Clients.Group($"user-{UserId}").SendAsync("DmMessageUnpinned", messageId, conversationId);
        }

        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<bool> IsMemberOfChannelAsync(int channelId)
    {
        var channel = await db.Channels.Include(c => c.Server).ThenInclude(s => s.Members)
            .FirstOrDefaultAsync(c => c.Id == channelId);
        return channel?.Server.Members.Any(m => m.UserId == UserId) ?? false;
    }

    private async Task<bool> IsAdminOfChannelAsync(int channelId)
    {
        var channel = await db.Channels.Include(c => c.Server).ThenInclude(s => s.Members)
            .FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return false;
        var member = channel.Server.Members.FirstOrDefault(m => m.UserId == UserId);
        return member is not null && member.Role >= ServerRole.Admin;
    }

    private Task<bool> IsParticipantAsync(int conversationId) =>
        db.DirectConversations.AnyAsync(dc => dc.Id == conversationId
            && (dc.User1Id == UserId || dc.User2Id == UserId));

    private static MessageDto ToChannelDto(Message m, bool isPinned) => new(
        m.Id, m.Content, m.Author.Username, m.AuthorId, m.CreatedAt, m.ChannelId, m.Source,
        m.AttachmentUrl, m.IsDeleted, m.EditedAt, null, m.Author.AvatarUrl,
        m.ReplyToId, m.ReplyTo?.Author?.Username, null, m.AttachmentFileName, isPinned);

    private static DirectMessageDto ToDmDto(DirectMessage dm, bool isPinned) => new(
        dm.Id, dm.Content, dm.Author.Username, dm.AuthorId, dm.CreatedAt,
        dm.ConversationId, dm.IsDeleted, dm.AttachmentUrl, dm.Author.AvatarUrl,
        dm.AttachmentFileName, isPinned);
}
