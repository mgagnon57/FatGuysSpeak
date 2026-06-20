using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Shared;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Services;

public static class PollHelper
{
    /// <summary>Builds the poll DTO with per-option vote tallies. <paramref name="forUserId"/> sets
    /// MyVoteOptionId; pass null when broadcasting shared tallies (each client keeps its own vote).</summary>
    public static async Task<PollDto?> BuildAsync(AppDbContext db, int pollId, int? forUserId)
    {
        var poll = await db.Polls.Include(p => p.Options).FirstOrDefaultAsync(p => p.Id == pollId);
        if (poll is null) return null;

        var votes = await db.PollVotes.Where(v => v.PollId == pollId).ToListAsync();
        var counts = votes.GroupBy(v => v.OptionId).ToDictionary(g => g.Key, g => g.Count());
        var myVote = forUserId is int uid ? votes.FirstOrDefault(v => v.UserId == uid)?.OptionId : null;

        var options = poll.Options.OrderBy(o => o.Position)
            .Select(o => new PollOptionDto(o.Id, o.Text, counts.GetValueOrDefault(o.Id)))
            .ToList();
        return new PollDto(poll.Id, poll.Question, options, votes.Count, myVote);
    }
}
