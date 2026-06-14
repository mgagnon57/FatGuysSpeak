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
[Route("api/channels/{channelId}/messages/{messageId}/reactions")]
[Authorize]
public class ReactionsController(AppDbContext db, IHubContext<ChatHub> hub) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string Username => User.FindFirstValue(ClaimTypes.Name)!;

    // POST  …/reactions/{emoji} — toggle; adds if absent, removes if present
    [HttpPost("{emoji}")]
    public async Task<ActionResult<ReactionsUpdatedDto>> Toggle(int channelId, int messageId, string emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji) || emoji.Length > 8)
            return BadRequest("Invalid emoji.");

        var message = await db.Messages
            .Include(m => m.Channel).ThenInclude(c => c.Server).ThenInclude(s => s.Members)
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ChannelId == channelId);
        if (message is null) return NotFound();
        if (!message.Channel.Server.Members.Any(m => m.UserId == UserId)) return Forbid();
        if (message.IsDeleted) return BadRequest("Cannot react to a deleted message.");

        var existing = await db.MessageReactions.FirstOrDefaultAsync(r =>
            r.MessageId == messageId && r.UserId == UserId && r.Emoji == emoji);

        if (existing is not null)
            db.MessageReactions.Remove(existing);
        else
            db.MessageReactions.Add(new MessageReaction
            {
                MessageId = messageId,
                UserId = UserId,
                Username = Username,
                Emoji = emoji
            });

        await db.SaveChangesAsync();

        var reactions = await BuildReactionsAsync(messageId, UserId);
        var result = new ReactionsUpdatedDto(messageId, reactions);
        await hub.Clients.Group($"channel-{channelId}").SendAsync("ReactionsUpdated", result);

        return Ok(result);
    }

    public async Task<List<ReactionDto>> BuildReactionsAsync(int messageId, int currentUserId)
    {
        var rows = await db.MessageReactions
            .Where(r => r.MessageId == messageId)
            .ToListAsync();

        return rows
            .GroupBy(r => r.Emoji)
            .Select(g => new ReactionDto(g.Key, g.Count(), g.Any(r => r.UserId == currentUserId)))
            .OrderBy(r => r.Emoji)
            .ToList();
    }
}
