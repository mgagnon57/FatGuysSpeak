using Squelch.Server.Data;
using Squelch.Shared;
using Microsoft.EntityFrameworkCore;

namespace Squelch.Server.Services;

/// <summary>Deletes every stored voice transcription and anything derived directly from one (the
/// per-channel voice daily recaps). Voice must never be transcribed or kept, so this runs at every
/// startup to scrub what older builds collected. Idempotent — a no-op once the data is gone.</summary>
public static class VoiceTranscriptPurge
{
    public static async Task<int> RunAsync(AppDbContext db)
    {
        var voiceIds = await db.Messages
            .Where(m => m.Source == MessageSource.Voice)
            .Select(m => m.Id).ToListAsync();
        if (voiceIds.Count > 0)
        {
            // Clear dependents first so the delete is FK-safe; replies to a transcription keep their
            // own content but lose the quoted preview.
            await db.MessageReactions.Where(r => voiceIds.Contains(r.MessageId)).ExecuteDeleteAsync();
            await db.PinnedMessages.Where(p => voiceIds.Contains(p.MessageId)).ExecuteDeleteAsync();
            await db.Messages.Where(m => m.ReplyToId != null && voiceIds.Contains(m.ReplyToId.Value))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReplyToId, (int?)null));
            await db.Messages.Where(m => voiceIds.Contains(m.Id)).ExecuteDeleteAsync();
        }
        var recaps = await db.DailyChatSummaries.Where(s => s.Source == MessageSource.Voice).ExecuteDeleteAsync();
        return voiceIds.Count + recaps;
    }
}
