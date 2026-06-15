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
[Route("api/dm")]
[Authorize]
public class DirectMessagesController(AppDbContext db, IHubContext<ChatHub> hub) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<List<DirectConversationDto>>> GetConversations()
    {
        var convos = await db.DirectConversations
            .Where(dc => dc.User1Id == UserId || dc.User2Id == UserId)
            .Include(dc => dc.User1)
            .Include(dc => dc.User2)
            .Include(dc => dc.Messages.OrderByDescending(m => m.Id).Take(1))
            .ToListAsync();

        return Ok(convos
            .OrderByDescending(dc => dc.Messages.Count > 0 ? dc.Messages.Max(m => m.CreatedAt) : dc.CreatedAt)
            .Select(ToConvoDto)
            .ToList());
    }

    [HttpPost("open/{otherUserId}")]
    public async Task<ActionResult<DirectConversationDto>> OpenConversation(int otherUserId)
    {
        if (otherUserId == UserId) return BadRequest("Cannot DM yourself.");
        var other = await db.Users.FindAsync(otherUserId);
        if (other is null) return NotFound("User not found.");

        int u1 = Math.Min(UserId, otherUserId);
        int u2 = Math.Max(UserId, otherUserId);

        var existing = await db.DirectConversations
            .Include(dc => dc.User1)
            .Include(dc => dc.User2)
            .FirstOrDefaultAsync(dc => dc.User1Id == u1 && dc.User2Id == u2);

        if (existing is not null)
            return Ok(ToConvoDto(existing));

        var convo = new DirectConversation { User1Id = u1, User2Id = u2 };
        db.DirectConversations.Add(convo);
        await db.SaveChangesAsync();
        convo.User1 = (await db.Users.FindAsync(u1))!;
        convo.User2 = (await db.Users.FindAsync(u2))!;
        return Ok(ToConvoDto(convo));
    }

    [HttpGet("{conversationId}/messages")]
    public async Task<ActionResult<List<DirectMessageDto>>> GetMessages(int conversationId, [FromQuery] int limit = 50)
    {
        if (!await IsParticipantAsync(conversationId)) return Forbid();

        var messages = await db.DirectMessages
            .Where(dm => dm.ConversationId == conversationId)
            .Include(dm => dm.Author)
            .OrderByDescending(dm => dm.CreatedAt)
            .Take(Math.Min(limit, 100))
            .OrderBy(dm => dm.CreatedAt)
            .ToListAsync();

        return Ok(messages.Select(ToMsgDto).ToList());
    }

    [HttpPost("{conversationId}/messages")]
    public async Task<ActionResult<DirectMessageDto>> SendMessage(int conversationId, SendDirectMessageRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Content) && req.AttachmentUrl is null)
            return BadRequest("Message must have content or an attachment.");
        if (req.Content?.Length > 2000)
            return BadRequest("Message content must be 2000 characters or fewer.");

        var convo = await db.DirectConversations.FindAsync(conversationId);
        if (convo is null) return NotFound();
        if (convo.User1Id != UserId && convo.User2Id != UserId) return Forbid();

        var user = await db.Users.FindAsync(UserId);
        var msg = new DirectMessage
        {
            ConversationId = conversationId,
            AuthorId = UserId,
            Content = req.Content?.Trim() ?? "",
            AttachmentUrl = req.AttachmentUrl,
            AttachmentFileName = req.AttachmentFileName
        };
        db.DirectMessages.Add(msg);
        await db.SaveChangesAsync();
        msg.Author = user!;

        var dto = ToMsgDto(msg);
        int recipientId = convo.User1Id == UserId ? convo.User2Id : convo.User1Id;
        await hub.Clients.Group($"user-{recipientId}").SendAsync("ReceiveDirectMessage", dto);
        await hub.Clients.Group($"user-{UserId}").SendAsync("ReceiveDirectMessage", dto);

        return Ok(dto);
    }

    [HttpDelete("{conversationId}/messages/{messageId}")]
    public async Task<ActionResult> DeleteMessage(int conversationId, int messageId)
    {
        var msg = await db.DirectMessages
            .FirstOrDefaultAsync(dm => dm.Id == messageId && dm.ConversationId == conversationId);
        if (msg is null) return NotFound();
        if (msg.AuthorId != UserId) return Forbid();

        msg.IsDeleted = true;
        await db.SaveChangesAsync();

        var convo = await db.DirectConversations.FindAsync(conversationId);
        if (convo is not null)
        {
            int recipientId = convo.User1Id == UserId ? convo.User2Id : convo.User1Id;
            await hub.Clients.Group($"user-{recipientId}").SendAsync("DirectMessageDeleted", conversationId, messageId);
            await hub.Clients.Group($"user-{UserId}").SendAsync("DirectMessageDeleted", conversationId, messageId);
        }

        return NoContent();
    }

    private DirectConversationDto ToConvoDto(DirectConversation dc)
    {
        var other = dc.User1Id == UserId ? dc.User2 : dc.User1;
        var last = dc.Messages.OrderByDescending(m => m.CreatedAt).FirstOrDefault();
        string? preview = last is null ? null
            : last.IsDeleted ? "(message deleted)"
            : last.Content.Length > 60 ? last.Content[..60] + "…"
            : last.Content;
        return new DirectConversationDto(dc.Id, other.Id, other.Username, other.AvatarUrl, preview, last?.CreatedAt);
    }

    private static DirectMessageDto ToMsgDto(DirectMessage dm) => new(
        dm.Id, dm.Content, dm.Author.Username, dm.AuthorId, dm.CreatedAt,
        dm.ConversationId, dm.IsDeleted, dm.AttachmentUrl, dm.Author.AvatarUrl, dm.AttachmentFileName);

    private Task<bool> IsParticipantAsync(int conversationId) =>
        db.DirectConversations.AnyAsync(dc => dc.Id == conversationId
            && (dc.User1Id == UserId || dc.User2Id == UserId));
}
