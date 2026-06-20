using System.Security.Claims;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Authorize]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("messages")]
public class PollsController(AppDbContext db, IHubContext<ChatHub> hub) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // POST /api/channels/{channelId}/polls — create a poll and post it into the channel as a card.
    [HttpPost("api/channels/{channelId}/polls")]
    public async Task<ActionResult<MessageDto>> Create(int channelId, CreatePollRequest req)
    {
        var question = (req.Question ?? "").Trim();
        var options = (req.Options ?? [])
            .Select(o => (o ?? "").Trim()).Where(o => o.Length > 0).Take(10).ToList();
        if (question.Length == 0) return BadRequest("Poll needs a question.");
        if (options.Count < 2) return BadRequest("Poll needs at least two options.");

        var channel = await db.Channels.Include(c => c.Server).ThenInclude(s => s.Members)
            .FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return NotFound();
        var member = channel.Server.Members.FirstOrDefault(m => m.UserId == UserId);
        if (member is null) return Forbid();
        var perm = await db.ChannelPermissions.FindAsync(channelId);
        if (perm is not null && member.Role < perm.MinRoleToWrite) return Forbid();

        var poll = new Poll
        {
            ChannelId = channelId,
            CreatorId = UserId,
            Question  = question,
            Options   = options.Select((t, i) => new PollOption { Text = t, Position = i }).ToList(),
        };
        db.Polls.Add(poll);
        await db.SaveChangesAsync();

        var msg = new Message { Content = question, AuthorId = UserId, ChannelId = channelId, Source = MessageSource.Text, PollId = poll.Id };
        db.Messages.Add(msg);
        await db.SaveChangesAsync();
        var author = await db.Users.FindAsync(UserId);

        var pollDto = await PollHelper.BuildAsync(db, poll.Id, UserId);
        var dto = new MessageDto(msg.Id, msg.Content, author!.Username, UserId, msg.CreatedAt, channelId,
            MessageSource.Text, AuthorAvatarUrl: author.AvatarUrl, Poll: pollDto);

        await hub.Clients.Group($"channel-{channelId}").SendAsync("ReceiveMessage", dto);
        await hub.Clients.Group($"server-{channel.ServerId}").SendAsync("NewMessageNotification", dto);
        return Ok(dto);
    }

    // POST /api/polls/{pollId}/vote — cast, change, or (tapping your current choice) retract your vote.
    [HttpPost("api/polls/{pollId}/vote")]
    public async Task<ActionResult<PollDto>> Vote(int pollId, PollVoteRequest req)
    {
        var poll = await db.Polls.Include(p => p.Options).FirstOrDefaultAsync(p => p.Id == pollId);
        if (poll is null) return NotFound();
        if (poll.Options.All(o => o.Id != req.OptionId)) return BadRequest("That option isn't part of this poll.");

        var channel = await db.Channels.Include(c => c.Server).ThenInclude(s => s.Members)
            .FirstOrDefaultAsync(c => c.Id == poll.ChannelId);
        if (channel is null) return NotFound();
        if (!channel.Server.Members.Any(m => m.UserId == UserId)) return Forbid();

        var existing = await db.PollVotes.FirstOrDefaultAsync(v => v.PollId == pollId && v.UserId == UserId);
        if (existing is null)
            db.PollVotes.Add(new PollVote { PollId = pollId, OptionId = req.OptionId, UserId = UserId });
        else if (existing.OptionId == req.OptionId)
            db.PollVotes.Remove(existing);          // tapping your current choice retracts it
        else
            existing.OptionId = req.OptionId;       // switch vote
        await db.SaveChangesAsync();

        // Broadcast shared tallies (no per-user vote); each client keeps its own highlight. The voter
        // gets their own MyVoteOptionId back in the HTTP response below.
        var shared = await PollHelper.BuildAsync(db, pollId, forUserId: null);
        await hub.Clients.Group($"channel-{poll.ChannelId}").SendAsync("PollUpdated", shared);

        return Ok(await PollHelper.BuildAsync(db, pollId, UserId));
    }
}
